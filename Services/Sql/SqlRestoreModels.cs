namespace SqlRestoreManager.Services.Sql;

public sealed record SqlSessionInfo(int SessionId, string Login, string Host, string Program);

/// <param name="Type">D = dados, L = log, S = FILESTREAM/memory-optimized, F = full-text.</param>
public sealed record BackupFile(string LogicalName, string PhysicalName, char Type);

/// <param name="HasChecksums">
/// O backup foi gerado WITH CHECKSUM. Só então VERIFYONLY/RESTORE podem usar CHECKSUM.
/// </param>
public sealed record BackupInspection(
    string SourceDatabase,
    int Position,
    DateTime? BackupFinishDate,
    string? ServerName,
    int SoftwareVersionMajor,
    bool HasChecksums,
    IReadOnlyList<BackupFile> Files);

/// <param name="SqlServerBackupPath">Caminho do .bak como o SQL Server o enxerga.</param>
public sealed record RestoreRequest(
    string TargetDatabase,
    string SqlServerBackupPath,
    BackupInspection Backup);

public sealed record RestoreResult(int DisconnectedSessions, string DataPath, string LogPath);

internal sealed record DatabaseState(string State, string UserAccess);
