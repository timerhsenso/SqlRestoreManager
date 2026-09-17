namespace SqlRestoreManager.Models;

/// <summary>Status persistidos em RestoreHistory.Status (nvarchar(50)).</summary>
public static class RestoreStatus
{
    public const string Queued = "Queued";
    public const string Preparing = "Preparing";
    public const string Validating = "Validating";
    public const string Restoring = "Restoring";
    public const string Success = "Success";
    public const string Error = "Error";
    public const string Interrupted = "Interrupted";

    /// <summary>Status que indicam job ainda não finalizado.</summary>
    public static readonly string[] Active = [Queued, Preparing, Validating, Restoring];

    public static bool IsActive(string status) => Active.Contains(status);

    public static string ToLabel(string status) => status switch
    {
        Queued => "Na fila",
        Preparing => "Preparando",
        Validating => "Validando",
        Restoring => "Restaurando",
        Success => "Concluído",
        Error => "Erro",
        Interrupted => "Interrompido",
        _ => status
    };
}
