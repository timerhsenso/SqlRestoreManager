namespace SqlRestoreManager.Configuration;

/// <summary>
/// Verificações e proteções executadas ANTES de sobrescrever o banco
/// (seção "Restore:PreRestore").
/// </summary>
public sealed class PreRestoreOptions
{
    /// <summary>
    /// RESTORE VERIFYONLY antes de restaurar. Lê o backup inteiro: mais seguro, porém mais lento.
    /// </summary>
    public bool VerifyBackup { get; set; } = true;

    /// <summary>
    /// Gera um BACKUP ... WITH COPY_ONLY do banco atual antes de sobrescrevê-lo.
    /// Ignorado quando o banco ainda não existe.
    /// </summary>
    public bool SafetyBackup { get; set; }

    /// <summary>Pasta do backup de segurança, vista pelo SQL Server.</summary>
    public string? SafetyBackupPath { get; set; }

    /// <summary>Usa COMPRESSION (com fallback automático se a edição não suportar).</summary>
    public bool SafetyBackupCompression { get; set; } = true;

    /// <summary>Apaga backups de segurança mais antigos que N dias. 0 = nunca apagar.</summary>
    public int SafetyBackupRetentionDays { get; set; } = 7;
}
