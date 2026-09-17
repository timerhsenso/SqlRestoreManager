using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Data;
using SqlRestoreManager.Filters;
using SqlRestoreManager.Models;
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
    private readonly AppDbContext _db;
    private readonly SqlRestoreService _sql;
    private readonly BackupUploadService _uploads;
    private readonly RestoreOptions _options;
    private readonly ILogger<RestoreController> _logger;

    public RestoreController(
        DatabaseCatalog catalog,
        RestoreQueue queue,
        AppDbContext db,
        SqlRestoreService sql,
        BackupUploadService uploads,
        IOptions<RestoreOptions> options,
        ILogger<RestoreController> logger)
    {
        _catalog = catalog;
        _queue = queue;
        _db = db;
        _sql = sql;
        _uploads = uploads;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            var databases = await _catalog.ListAsync(ct);
            return View(new RestoreIndexViewModel { Databases = databases, MaxUploadMB = _options.MaxUploadMB });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Falha ao listar os bancos do servidor.");
            return View(new RestoreIndexViewModel
            {
                MaxUploadMB = _options.MaxUploadMB,
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
                x.SourceDatabase,
                x.TargetDatabase
            })
            .SingleOrDefaultAsync(ct);

        return item is null ? NotFound() : Json(item);
    }

    [HttpPost]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Start([FromQuery] string? targetDatabase, CancellationToken ct)
    {
        string database;
        try
        {
            database = await _catalog.ResolveAsync(targetDatabase, ct);
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
                jobId, upload.FilePath, upload.JobFolder, upload.OriginalFileName, database, upload.SizeBytes),
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
