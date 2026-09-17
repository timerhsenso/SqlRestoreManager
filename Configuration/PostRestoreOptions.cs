namespace SqlRestoreManager.Configuration;

/// <summary>
/// Ajustes executados automaticamente após um restore bem-sucedido (seção "Restore:PostRestore").
/// Falhas aqui NÃO desfazem o restore: viram alertas no histórico.
/// </summary>
public sealed class PostRestoreOptions
{
    /// <summary>Liga/desliga todo o pós-restore.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Remove publicação/assinatura de replicação herdada do servidor do cliente.</summary>
    public bool RemoveReplication { get; set; } = true;

    /// <summary>Coloca o banco em RECOVERY SIMPLE (recomendado para bancos de teste).</summary>
    public bool SetRecoverySimple { get; set; } = true;

    /// <summary>Reduz os arquivos de log para <see cref="LogTargetSizeMB"/>.</summary>
    public bool ShrinkLog { get; set; } = true;

    /// <summary>Tamanho alvo do log após o shrink, em MB.</summary>
    public int LogTargetSizeMB { get; set; } = 512;

    /// <summary>Crescimento fixo do log, em MB (0 = não altera).</summary>
    public int LogGrowthMB { get; set; } = 256;

    /// <summary>Religa usuários SQL órfãos a logins de mesmo nome existentes no servidor.</summary>
    public bool FixOrphanUsers { get; set; } = true;

    /// <summary>
    /// Pasta (vista pela aplicação) com scripts .sql executados no banco restaurado, em ordem alfabética:
    /// primeiro os da raiz (todos os bancos), depois os da subpasta com o nome do banco.
    /// Lotes separados por GO. Vazio = não executa scripts.
    /// </summary>
    public string? ScriptsPath { get; set; }

    /// <summary>Timeout de cada comando do pós-restore, em minutos.</summary>
    public int CommandTimeoutMinutes { get; set; } = 30;
}
