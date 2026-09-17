using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Data;
using SqlRestoreManager.Models;
using SqlRestoreManager.Services.Backup;
using SqlRestoreManager.Services.Sql;

namespace SqlRestoreManager.Controllers;

public sealed class BackupController : Controller
{
    private const int RecentRuns = 10;

    private readonly AppDbContext _db;
    private readonly BackupService _service;
    private readonly BackupRunner _runner;
    private readonly BackupOptions _options;
    private readonly ILogger<BackupController> _logger;

    public BackupController(
        AppDbContext db,
        BackupService service,
        BackupRunner runner,
        IOptions<BackupOptions> options,
        ILogger<BackupController> logger)
    {
        _db = db;
        _service = service;
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var runs = await _db.BackupRuns
            .AsNoTracking()
            .Include(x => x.Items)
            .OrderByDescending(x => x.StartedAt)
            .Take(RecentRuns)
            .ToListAsync(ct);

        BackupIndexViewModel Build(IReadOnlyList<BackupTarget> targets, string? error) => new()
        {
            Enabled = _options.Enabled,
            RestoreInProgress = _runner.RestoreInProgress,
            BackupInProgress = _runner.IsRunning,
            CurrentRunId = _runner.CurrentRunId,
            BackupPath = _options.Path,
            RetentionDays = _options.RetentionDays,
            ShrinkLogBefore = _options.ShrinkLogBefore,
            Runs = runs,
            Targets = targets,
            LoadError = error
        };

        if (!_options.Enabled)
            return View(Build([], null));

        try
        {
            return View(Build(await _service.ListTargetsAsync(ct), null));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Falha ao listar os bancos para backup.");
            return View(Build([], SqlErrorTranslator.Translate(ex)));
        }
    }

    [HttpPost]
    public async Task<IActionResult> Start([FromForm] string[]? databases, CancellationToken ct)
    {
        try
        {
            var runId = await _runner.StartAsync(databases ?? [], BackupTrigger.Manual, ct);
            return Json(new { runId });
        }
        catch (RestoreValidationException ex)
        {
            return StatusCode(ex.StatusCode, ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Falha ao iniciar o backup.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, SqlErrorTranslator.Translate(ex));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Status(Guid runId, CancellationToken ct)
    {
        var run = await _db.BackupRuns
            .AsNoTracking()
            .Where(x => x.RunId == runId)
            .Select(x => new
            {
                x.RunId,
                x.Status,
                x.CurrentStep,
                x.PercentComplete,
                x.TotalDatabases,
                x.SucceededCount,
                x.FailedCount,
                x.ErrorMessage
            })
            .SingleOrDefaultAsync(ct);

        return run is null ? NotFound() : Json(run);
    }
}
