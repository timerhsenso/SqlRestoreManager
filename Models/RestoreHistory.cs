namespace SqlRestoreManager.Models;

/// <summary>
/// Histórico de restores (dbo.RestoreHistory).
/// Fonte da verdade do schema: Database/001_RestoreHistory.sql.
/// Mapeamento: Data/AppDbContext.cs.
/// </summary>
public class RestoreHistory
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string TargetDatabase { get; set; } = string.Empty;
    public string? SourceDatabase { get; set; }
    public string Status { get; set; } = RestoreStatus.Queued;
    public string? CurrentStep { get; set; }
    public int PercentComplete { get; set; }
    public long FileSizeBytes { get; set; }
    public int DisconnectedSessions { get; set; }
    public int? BackupPosition { get; set; }
    public DateTime? BackupFinishDate { get; set; }
    public string? BackupServerName { get; set; }
    public string? NodeName { get; set; }
    public int? LogSizeBeforeMB { get; set; }
    public int? LogSizeAfterMB { get; set; }
    public string? PostRestoreLog { get; set; }
    public string? SafetyBackupPath { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
