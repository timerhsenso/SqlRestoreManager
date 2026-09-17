using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Data;
using SqlRestoreManager.Filters;
using SqlRestoreManager.Models;
using SqlRestoreManager.Services.Backup;
using SqlRestoreManager.Services.Jobs;
using SqlRestoreManager.Services.Sql;
using SqlRestoreManager.Services.Uploads;

namespace SqlRestoreManager.Controllers;

public sealed class RestoreController : Controller
{
    /// <summary>Folga para cabeçalhos/boundaries do multipart.</summary>
    private const long MultipartOverheadBytes = 1024 * 1024;

    private readonly DatabaseCatalog _catalog;
    private readonly RestoreQueue _queue;
    private readonly RestoreJobRegistry _registry;
    private readonly AppDbContext _db;
    private readonly SqlRestoreService _sql;
    private readonly BackupUploadService _uploads;
    private readonly BackupLibrary _library;
    private readonly BackupRunner _backups;
    private readonly BackupExtractor _extractor;
    private readonly RestoreOptions _options;
    private readonly ILogger<RestoreController> _logger;

    public RestoreController(
        DatabaseCatalog catalog,
        RestoreQueue queue,
        RestoreJobRegistry registry,
        AppDbContext db,
        SqlRestoreService sql,
        BackupUploadService uploads,
        BackupLibrary library,
        BackupRunner backups,
        BackupExtractor extractor,
        IOptions<RestoreOptions> options,
        ILogger<RestoreController> logger)
    {
        _catalog = catalog;
        _queue = queue;
        _registry = registry;
        _db = db;
        _sql = sql;
        _uploads = uploads;
        _library = library;
        _backups = backups;
        _extractor = extractor;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            var databases = await _catalog.ListAsync(ct);
            return View(new RestoreIndexViewModel
            {
                Databases = databases,
                MaxUploadMB = _options.MaxUploadMB,
                LibraryEnabled = _library.Enabled,
                AllowedExtensions = _extractor.AllowedExtensions,
                AllowNewDatabases = _catalog.AllowNewDatabases
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Falha ao listar os bancos do servidor.");
            return View(new RestoreIndexViewModel
            {
                MaxUploadMB = _options.MaxUploadMB,
                LibraryEnabled = _library.Enabled,
                AllowedExtensions = _extractor.AllowedExtensions,
                LoadError = SqlErrorTranslator.Translate(ex)
            });
        }
    }

    [HttpGet]
    public async Task<IActionResult> Sessions(string? database, CancellationToken ct)
    {
        try
        {
            var sessions = await _sql.GetSessionsAsync(database ?? string.Empty, ct);
            return Json(sessions);
        }
        catch (RestoreValidationException ex)
        {
            return StatusCode(ex.StatusCode, ex.Message);
        }
    }

    /// <summary>Arquivos disponíveis na pasta de backups do servidor.</summary>
    [HttpGet]
    public IActionResult Files()
    {
        try
        {
            return Json(_library.List().Select(f => new
            {
                name = f.Name,
                sizeMB = Math.Round(f.SizeMB, 1),
                modifiedAt = f.ModifiedAt.ToString("dd/MM/yyyy HH:mm")
            }));
        }
        catch (RestoreValidationException ex)
        {
            return StatusCode(ex.StatusCode, ex.Message);
        }
    }

    /// <summary>Restaura um arquivo que já está na pasta do servidor (sem upload).</summary>
    [HttpPost]
    public async Task<IActionResult> StartFromFile(
        string? targetDatabase, string? fileName, bool createNew, CancellationToken ct)
    {
        string database;
        FileInfo file;
        try
        {
            database = (await _catalog.ResolveAsync(targetDatabase, createNew, ct)).Name;
            file = _library.Resolve(fileName);
        }
        catch (RestoreValidationException ex)
        {
            return StatusCode(ex.StatusCode, ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Falha ao validar o restore a partir da pasta.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, SqlErrorTranslator.Translate(ex));
        }

        if (_backups.IsRunning)
            return Conflict("Há um backup em andamento. Aguarde o término para restaurar.");

        var jobId = Guid.NewGuid();
        if (!_queue.TryReserve(database, jobId))
            return Conflict($"Já existe um restore na fila ou em andamento para '{database}'.");

        var enqueued = false;
        try
        {
            // Pasta só para extração: o arquivo original da pasta de backups não é tocado.
            var jobFolder = _uploads.GetJobFolder(jobId);
            Directory.CreateDirectory(jobFolder);

            var history = new RestoreHistory
            {
                JobId = jobId,
                OriginalFileName = file.Name,
                TargetDatabase = database,
                FileSizeBytes = file.Length,
                Status = RestoreStatus.Queued,
                CurrentStep = "Na fila de restore.",
                PercentComplete = 0,
                NodeName = _options.EffectiveNodeName,
                StartedAt = DateTime.Now
            };

            _db.RestoreHistory.Add(history);
            await _db.SaveChangesAsync(CancellationToken.None);

            await _queue.EnqueueAsync(new RestoreJob(
                jobId, RestoreSource.Library, file.FullName, jobFolder, file.Name, database, file.Length),
                CancellationToken.None);
            enqueued = true;

            _logger.LogInformation(
                "Job {JobId} enfileirado a partir da pasta: {File} → {Database}.", jobId, file.Name, database);

            return Json(new { jobId });
        }
        finally
        {
            if (!enqueued)
            {
                _queue.Release(database, jobId);
                TempFolder.TryDelete(_uploads.GetJobFolder(jobId), _logger);
            }
        }
    }

    /// <summary>Cancela um job desta instância (na fila ou em andamento).</summary>
    [HttpPost]
    public async Task<IActionResult> Cancel(Guid jobId, CancellationToken ct)
    {
        var history = await _db.RestoreHistory.SingleOrDefaultAsync(x => x.JobId == jobId, ct);
        if (history is null)
            return NotFound("Job não encontrado.");

        if (!RestoreStatus.IsActive(history.Status))
            return Conflict($"O job já está {RestoreStatus.ToLabel(history.Status).ToLowerInvariant()}.");

        if (!string.Equals(history.NodeName, _options.EffectiveNodeName, StringComparison.OrdinalIgnoreCase))
            return Conflict(
                $"Este job está sendo processado por outra instância ({history.NodeName}). " +
                "Cancele a partir dela.");

        var (known, sessionId) = _registry.RequestCancel(jobId);

        if (known && sessionId != 0)
        {
            // O SQL Server já está executando (verify, backup ou restore): encerra a sessão.
            try
            {
                await _sql.KillSessionAsync(sessionId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao encerrar a sessão {Spid} do job {JobId}.", sessionId, jobId);
            }
        }

        _logger.LogWarning("Cancelamento solicitado para o job {JobId} ({Status}).", jobId, history.Status);
        return Ok(known
            ? "Cancelamento solicitado."
            : "Cancelamento registrado: o job será descartado quando sair da fila.");
    }

    [HttpGet]
    public async Task<IActionResult> Status(Guid jobId, CancellationToken ct)
    {
        var item = await _db.RestoreHistory
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => new
            {
                x.JobId,
                x.Status,
                x.CurrentStep,
                x.PercentComplete,
                x.ErrorMessage,
                x.PostRestoreLog,
                x.SourceDatabase,
                x.TargetDatabase
            })
            .SingleOrDefaultAsync(ct);

        return item is null ? NotFound() : Json(item);
    }

    [HttpPost]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Start(
        [FromQuery] string? targetDatabase, [FromQuery] bool createNew, CancellationToken ct)
    {
        string database;
        try
        {
            database = (await _catalog.ResolveAsync(targetDatabase, createNew, ct)).Name;
        }
        catch (RestoreValidationException ex)
        {
            return StatusCode(ex.StatusCode, ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Falha ao validar o banco de destino.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                $"Não foi possível consultar o servidor SQL. {SqlErrorTranslator.Translate(ex)}");
        }

        ConfigureRequestBodyLimit();

        if (_backups.IsRunning)
            return Conflict("Há um backup em andamento. Aguarde o término para restaurar.");

        var jobId = Guid.NewGuid();
        if (!_queue.TryReserve(database, jobId))
            return Conflict($"Já existe um restore na fila ou em andamento para '{database}'.");

        var enqueued = false;
        try
        {
            var upload = await _uploads.ReceiveAsync(Request, jobId, ct);

            var history = new RestoreHistory
            {
                JobId = jobId,
                OriginalFileName = upload.OriginalFileName,
                TargetDatabase = database,
                FileSizeBytes = upload.SizeBytes,
                Status = RestoreStatus.Queued,
                CurrentStep = "Na fila de restore.",
                PercentComplete = 0,
                NodeName = _options.EffectiveNodeName,
                StartedAt = DateTime.Now
            };

            _db.RestoreHistory.Add(history);
            await _db.SaveChangesAsync(CancellationToken.None);

            await _queue.EnqueueAsync(new RestoreJob(
                jobId, RestoreSource.Upload, upload.FilePath, upload.JobFolder,
                upload.OriginalFileName, database, upload.SizeBytes),
                CancellationToken.None);
            enqueued = true;

            _logger.LogInformation(
                "Job {JobId} enfileirado: {File} ({Size} bytes) → {Database}.",
                jobId, upload.OriginalFileName, upload.SizeBytes, database);

            return Json(new { jobId });
        }
        catch (RestoreValidationException ex)
        {
            return StatusCode(ex.StatusCode, ex.Message);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return StatusCode(ex.StatusCode, $"O arquivo excede o limite de {_options.MaxUploadMB} MB.");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Upload do job {JobId} cancelado pelo cliente.", jobId);
            return new EmptyResult();
        }
        finally
        {
            if (!enqueued)
            {
                _queue.Release(database, jobId);
                TempFolder.TryDelete(_uploads.GetJobFolder(jobId), _logger);
            }
        }
    }

    /// <summary>
    /// Libera o tamanho do corpo SOMENTE nesta action (o restante do site mantém o padrão).
    /// O IIS ainda aplica requestLimits/maxAllowedContentLength do web.config.
    /// </summary>
    private void ConfigureRequestBodyLimit()
    {
        var feature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = _options.MaxUploadBytes + MultipartOverheadBytes;
    }
}
