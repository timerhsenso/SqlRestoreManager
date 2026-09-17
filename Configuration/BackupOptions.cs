namespace SqlRestoreManager.Configuration;

/// <summary>
/// Backup dos bancos de usuário (seção "Backup").
/// Bancos de sistema nunca entram.
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Habilita a aba e a execução de backups.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pasta dos arquivos .bak, vista pelo SQL Server.</summary>
    public string? Path { get; set; }

    /// <summary>Bancos que não devem entrar no backup. Aceita curingas (* e ?).</summary>
    public List<string> ExcludedDatabases { get; set; } = new();

    public bool Compression { get; set; } = true;

    public bool Checksum { get; set; } = true;

    /// <summary>Apaga .bak mais antigos que N dias após a execução. 0 = nunca.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Coloca em RECOVERY SIMPLE e reduz o log antes do backup.</summary>
    public bool ShrinkLogBefore { get; set; } = true;

    public int LogTargetSizeMB { get; set; } = 512;

    public int LogGrowthMB { get; set; } = 256;

    public int CommandTimeoutMinutes { get; set; } = 120;
}
