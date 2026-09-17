namespace SqlRestoreManager.Configuration;

/// <summary>
/// Configurações do restore (seção "Restore" do appsettings).
/// Caminhos "SqlServer*" / DataPath / LogPath são vistos pelo SERVIÇO do SQL Server,
/// e não pela aplicação.
/// </summary>
public sealed class RestoreOptions
{
    public const string SectionName = "Restore";

    /// <summary>Pasta onde a APLICAÇÃO grava os uploads (local ou UNC).</summary>
    public string TempPath { get; set; } = string.Empty;

    /// <summary>
    /// Mesma pasta de <see cref="TempPath"/>, porém como o SQL Server a enxerga.
    /// Vazio = igual ao TempPath (app e SQL na mesma máquina).
    /// </summary>
    public string? SqlServerTempPath { get; set; }

    /// <summary>Pasta dos .mdf/.ndf no servidor SQL. Vazio = InstanceDefaultDataPath.</summary>
    public string? DataPath { get; set; }

    /// <summary>Pasta dos .ldf no servidor SQL. Vazio = InstanceDefaultLogPath.</summary>
    public string? LogPath { get; set; }

    /// <summary>Tamanho máximo do upload em MB (IIS limita a ~4 GB).</summary>
    public int MaxUploadMB { get; set; } = 4000;

    /// <summary>Tamanho máximo do .bak descompactado de um .zip, em GB.</summary>
    public int MaxExtractedGB { get; set; } = 200;

    /// <summary>Intervalo de consulta do progresso do RESTORE, em segundos.</summary>
    public int ProgressPollSeconds { get; set; } = 2;

    /// <summary>Pastas temporárias órfãs mais antigas que isso são removidas.</summary>
    public int TempRetentionHours { get; set; } = 24;

    /// <summary>Identifica a instância no histórico. Vazio = nome da máquina.</summary>
    public string? NodeName { get; set; }

    /// <summary>
    /// Bancos que NÃO podem ser restaurados. Aceita curingas: * (qualquer sequência) e ? (um caractere).
    /// Ex.: "banco01", "PROD_*", "ReportServer*". Comparação sem diferenciar maiúsculas.
    /// Bancos de sistema e o banco de histórico já são bloqueados automaticamente.
    /// </summary>
    public List<string> BlockedDatabases { get; set; } = new();

    /// <summary>
    /// true = bancos bloqueados aparecem na lista desabilitados; false = não aparecem.
    /// </summary>
    public bool ShowBlockedDatabases { get; set; } = true;

    /// <summary>
    /// Permite restaurar criando um banco que ainda não existe (nome digitado na tela).
    /// O nome continua sujeito a BlockedDatabases e às regras de nome.
    /// </summary>
    public bool AllowNewDatabases { get; set; } = true;

    /// <summary>
    /// Pasta (vista pela aplicação) onde os backups são copiados manualmente, para restaurar
    /// sem upload. Vazio = recurso desligado.
    /// </summary>
    public string? LibraryPath { get; set; }

    /// <summary>Mesma pasta de <see cref="LibraryPath"/>, como o SQL Server a enxerga.</summary>
    public string? SqlServerLibraryPath { get; set; }

    /// <summary>Caminho do 7z.exe no servidor da aplicação. Habilita .rar e .7z.</summary>
    public string? SevenZipPath { get; set; }

    /// <summary>Verificações antes de sobrescrever o banco.</summary>
    public PreRestoreOptions PreRestore { get; set; } = new();

    /// <summary>Ajustes automáticos após o restore.</summary>
    public PostRestoreOptions PostRestore { get; set; } = new();

    public string EffectiveNodeName =>
        string.IsNullOrWhiteSpace(NodeName) ? Environment.MachineName : NodeName.Trim();

    public long MaxUploadBytes => (long)MaxUploadMB * 1024 * 1024;

    public long MaxExtractedBytes => (long)MaxExtractedGB * 1024 * 1024 * 1024;
}
