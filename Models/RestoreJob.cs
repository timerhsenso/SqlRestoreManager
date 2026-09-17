namespace SqlRestoreManager.Models;

public enum RestoreSource
{
    /// <summary>Arquivo enviado pelo navegador (fica dentro da pasta do job).</summary>
    Upload,

    /// <summary>Arquivo já presente na pasta de backups do servidor (não é removido).</summary>
    Library
}

/// <summary>Item da fila em memória processado pelo RestoreWorker.</summary>
/// <param name="SourcePath">Caminho local do .bak/.zip/.rar/.7z.</param>
/// <param name="JobFolder">Pasta temporária do job (extração e, no upload, o próprio arquivo).</param>
public sealed record RestoreJob(
    Guid JobId,
    RestoreSource Source,
    string SourcePath,
    string JobFolder,
    string OriginalFileName,
    string TargetDatabase,
    long FileSizeBytes);
