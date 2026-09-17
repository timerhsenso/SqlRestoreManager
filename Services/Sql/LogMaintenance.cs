using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlRestoreManager.Services.Sql;

public sealed record LogShrinkResult(int? BeforeMB, int? AfterMB, string Message);

/// <summary>
/// RECOVERY SIMPLE + CHECKPOINT + DBCC SHRINKFILE nos arquivos de log.
/// Usado pelo pós-restore e antes do backup.
/// </summary>
public sealed class LogMaintenance
{
    private readonly SqlConnectionFactory _connections;
    private readonly ILogger<LogMaintenance> _logger;

    public LogMaintenance(SqlConnectionFactory connections, ILogger<LogMaintenance> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public async Task<LogShrinkResult> ShrinkAsync(
        string database, int targetSizeMB, int growthMB, bool setRecoverySimple, int timeoutSeconds)
    {
        var before = await GetLogSizeMbAsync(database, timeoutSeconds);

        if (setRecoverySimple)
        {
            await using var master = await _connections.OpenMasterAsync(CancellationToken.None);
            await ExecuteAsync(master,
                $"ALTER DATABASE {QuoteName(database)} SET RECOVERY SIMPLE WITH NO_WAIT;", timeoutSeconds);
        }

        await using var cn = await OpenDatabaseAsync(database);
        var messages = new List<string>();

        foreach (var (fileId, name) in await GetLogFilesAsync(cn, timeoutSeconds))
        {
            for (var pass = 0; pass < 2; pass++)
            {
                await ExecuteAsync(cn, "CHECKPOINT;", timeoutSeconds);
                // Valores inteiros: sem risco de injeção.
                await ExecuteAsync(cn, $"DBCC SHRINKFILE ({fileId}, {targetSizeMB}) WITH NO_INFOMSGS;", timeoutSeconds);

                if (await GetFileSizeMbAsync(cn, fileId, timeoutSeconds) <= targetSizeMB)
                    break;
            }

            if (growthMB > 0)
                await ExecuteAsync(cn,
                    $"ALTER DATABASE CURRENT MODIFY FILE (NAME = {QuoteName(name)}, FILEGROWTH = {growthMB}MB);",
                    timeoutSeconds);

            messages.Add($"{name} = {await GetFileSizeMbAsync(cn, fileId, timeoutSeconds)} MB");
        }

        var after = await GetLogSizeMbAsync(database, timeoutSeconds);
        var result = new LogShrinkResult(before, after, string.Join("; ", messages));

        _logger.LogInformation("Log de {Database}: {Before} MB → {After} MB.", database, before, after);
        return result;
    }

    public async Task<int?> GetLogSizeMbAsync(string database, int timeoutSeconds)
    {
        await using var cn = await _connections.OpenMasterAsync(CancellationToken.None);
        await using var cmd = Command(cn, """
            SELECT CAST(SUM(CAST(size AS bigint)) * 8 / 1024 AS int)
            FROM sys.master_files
            WHERE database_id = DB_ID(@db) AND type = 1;
            """, timeoutSeconds);
        cmd.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = database;
        return await cmd.ExecuteScalarAsync(CancellationToken.None) is int mb ? mb : null;
    }

    public async Task<string> GetLogReuseWaitAsync(string database, int timeoutSeconds)
    {
        await using var cn = await _connections.OpenMasterAsync(CancellationToken.None);
        await using var cmd = Command(cn,
            "SELECT log_reuse_wait_desc FROM sys.databases WHERE name = @db;", timeoutSeconds);
        cmd.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = database;
        return await cmd.ExecuteScalarAsync(CancellationToken.None) as string ?? "?";
    }

    private static async Task<List<(int FileId, string Name)>> GetLogFilesAsync(SqlConnection cn, int timeoutSeconds)
    {
        await using var cmd = Command(cn,
            "SELECT file_id, name FROM sys.database_files WHERE type = 1 ORDER BY file_id;", timeoutSeconds);

        var files = new List<(int, string)>();
        await using var rd = await cmd.ExecuteReaderAsync(CancellationToken.None);
        while (await rd.ReadAsync(CancellationToken.None))
            files.Add((rd.GetInt32(0), rd.GetString(1)));

        return files;
    }

    private static async Task<int> GetFileSizeMbAsync(SqlConnection cn, int fileId, int timeoutSeconds)
    {
        await using var cmd = Command(cn,
            "SELECT CAST(CAST(size AS bigint) * 8 / 1024 AS int) FROM sys.database_files WHERE file_id = @id;",
            timeoutSeconds);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = fileId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(CancellationToken.None));
    }

    private async Task<SqlConnection> OpenDatabaseAsync(string database)
    {
        var cn = await _connections.OpenMasterAsync(CancellationToken.None);
        try
        {
            await cn.ChangeDatabaseAsync(database, CancellationToken.None);
            return cn;
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
    }

    private static SqlCommand Command(SqlConnection cn, string sql, int timeoutSeconds)
    {
        var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;
        return cmd;
    }

    private static async Task ExecuteAsync(SqlConnection cn, string sql, int timeoutSeconds)
    {
        await using var cmd = Command(cn, sql, timeoutSeconds);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string QuoteName(string value) => "[" + value.Replace("]", "]]") + "]";
}
