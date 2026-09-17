using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Data;
using SqlRestoreManager.Models;

namespace SqlRestoreManager.Controllers;

public sealed class HistoryController : Controller
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 200;
    private const int MaxExportRows = 10_000;

    private static readonly CultureInfo Culture = new("pt-BR");

    private readonly AppDbContext _db;
    private readonly RestoreOptions _options;

    public HistoryController(AppDbContext db, IOptions<RestoreOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? q, string? status, int page = 1, int pageSize = DefaultPageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, MaxPageSize);

        var query = Filter(q, status);
        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(x => x.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return View(new HistoryViewModel
        {
            Items = items,
            Query = q,
            Status = status,
            Page = page,
            PageSize = pageSize,
            Total = total,
            NodeName = _options.EffectiveNodeName
        });
    }

    /// <summary>CSV com o mesmo filtro da tela (separador ';' e BOM, para o Excel pt-BR).</summary>
    [HttpGet]
    public async Task<IActionResult> Export(string? q, string? status, CancellationToken ct = default)
    {
        var items = await Filter(q, status)
            .OrderByDescending(x => x.StartedAt)
            .Take(MaxExportRows)
            .ToListAsync(ct);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';',
            "Inicio", "Fim", "Arquivo", "TamanhoMB", "Origem", "ServidorOrigem", "Destino", "BackupDe",
            "Status", "Progresso", "ConexoesEncerradas", "LogAntesMB", "LogDepoisMB", "BackupSeguranca",
            "Instancia", "Mensagem"));

        foreach (var x in items)
        {
            csv.AppendLine(string.Join(';',
                Field(x.StartedAt.ToString("dd/MM/yyyy HH:mm:ss", Culture)),
                Field(x.FinishedAt?.ToString("dd/MM/yyyy HH:mm:ss", Culture)),
                Field(x.OriginalFileName),
                Field((x.FileSizeBytes / 1024d / 1024d).ToString("N1", Culture)),
                Field(x.SourceDatabase),
                Field(x.BackupServerName),
                Field(x.TargetDatabase),
                Field(x.BackupFinishDate?.ToString("dd/MM/yyyy HH:mm", Culture)),
                Field(RestoreStatus.ToLabel(x.Status)),
                Field(x.PercentComplete.ToString(Culture)),
                Field(x.DisconnectedSessions.ToString(Culture)),
                Field(x.LogSizeBeforeMB?.ToString(Culture)),
                Field(x.LogSizeAfterMB?.ToString(Culture)),
                Field(x.SafetyBackupPath),
                Field(x.NodeName),
                Field(x.ErrorMessage ?? x.CurrentStep)));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(bytes, "text/csv", $"restores_{DateTime.Now:yyyyMMdd_HHmm}.csv");
    }

    private IQueryable<RestoreHistory> Filter(string? q, string? status)
    {
        var query = _db.RestoreHistory.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(x =>
                EF.Functions.Like(x.OriginalFileName, $"%{term}%") ||
                EF.Functions.Like(x.TargetDatabase, $"%{term}%") ||
                (x.SourceDatabase != null && EF.Functions.Like(x.SourceDatabase, $"%{term}%")));
        }

        if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => RestoreStatus.Active.Contains(x.Status));
        else if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status);

        return query;
    }

    /// <summary>Escapa o campo para CSV (aspas e quebras de linha).</summary>
    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalized = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return $"\"{normalized.Replace("\"", "\"\"")}\"";
    }
}
