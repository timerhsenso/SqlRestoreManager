using SqlRestoreManager.Services.Backup;

namespace SqlRestoreManager.Models;

public sealed class BackupIndexViewModel
{
    public IReadOnlyList<BackupTarget> Targets { get; init; } = [];
    public IReadOnlyList<BackupRun> Runs { get; init; } = [];
    public bool Enabled { get; init; }
    public bool RestoreInProgress { get; init; }
    public bool BackupInProgress { get; init; }
    public Guid? CurrentRunId { get; init; }
    public string? BackupPath { get; init; }
    public int RetentionDays { get; init; }
    public bool ShrinkLogBefore { get; init; }
    public string? LoadError { get; init; }
}
