namespace SqlRestoreManager.Models;

/// <summary>Status persistidos em RestoreHistory.Status (nvarchar(50)).</summary>
public static class RestoreStatus
{
    public const string Queued = "Queued";
    public const string Preparing = "Preparing";
    public const string Validating = "Validating";
    public const string Verifying = "Verifying";
    public const string SafetyBackup = "SafetyBackup";
    public const string Restoring = "Restoring";
    public const string PostRestoring = "PostRestoring";
    public const string Success = "Success";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Canceled = "Canceled";
    public const string Interrupted = "Interrupted";

    /// <summary>Status que indicam job ainda não finalizado.</summary>
    public static readonly string[] Active =
        [Queued, Preparing, Validating, Verifying, SafetyBackup, Restoring, PostRestoring];

    public static bool IsActive(string status) => Active.Contains(status);

    public static string ToLabel(string status) => status switch
    {
        Queued => "Na fila",
        Preparing => "Preparando",
        Validating => "Validando",
        Verifying => "Verificando backup",
        SafetyBackup => "Backup de segurança",
        Restoring => "Restaurando",
        PostRestoring => "Pós-restore",
        Success => "Concluído",
        Warning => "Concluído com alertas",
        Error => "Erro",
        Canceled => "Cancelado",
        Interrupted => "Interrompido",
        _ => status
    };
}
