namespace SqlRestoreManager.Models;

/// <summary>Status de BackupRun/BackupItem.</summary>
public static class BackupStatus
{
    public const string Running = "Running";
    public const string Success = "Success";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Skipped = "Skipped";

    public static bool IsActive(string status) => status == Running;

    public static string ToLabel(string status) => status switch
    {
        Running => "Em andamento",
        Success => "Concluído",
        Warning => "Concluído com falhas",
        Error => "Erro",
        Skipped => "Ignorado",
        _ => status
    };
}

public static class BackupTrigger
{
    public const string Manual = "Manual";
    public const string Scheduled = "Scheduled";
}

/// <summary>Uma execução do backup (dbo.BackupRun).</summary>
public class BackupRun
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public string Trigger { get; set; } = BackupTrigger.Manual;
    public string Status { get; set; } = BackupStatus.Running;
    public string? CurrentStep { get; set; }
    public int PercentComplete { get; set; }
    public int TotalDatabases { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public string? NodeName { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public List<BackupItem> Items { get; set; } = new();
}

/// <summary>Backup de um banco dentro da execução (dbo.BackupItem).</summary>
public class BackupItem
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public string DatabaseName { get; set; } = string.Empty;
    public string Status { get; set; } = BackupStatus.Running;
    public string? FilePath { get; set; }
    public long SizeBytes { get; set; }
    public int? LogSizeBeforeMB { get; set; }
    public int? LogSizeAfterMB { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public double DurationSeconds => ((FinishedAt ?? DateTime.Now) - StartedAt).TotalSeconds;
}
