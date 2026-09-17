namespace SqlRestoreManager.Models;

/// <summary>Item da fila em memória processado pelo RestoreWorker.</summary>
public sealed record RestoreJob(
    Guid JobId,
    string UploadedPath,
    string JobFolder,
    string OriginalFileName,
    string TargetDatabase,
    long FileSizeBytes);
