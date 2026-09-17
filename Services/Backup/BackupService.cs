using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Services.Sql;

namespace SqlRestoreManager.Services.Backup;

public sealed record BackupTarget(string Name, string State, long SizeMB);

public sealed record BackupFileResult(string Path, long SizeBytes);

/// <summary>Operações de BACKUP no SQL Server.</summary>
public sealed class BackupService
{
    private readonly SqlConnectionFactory _connections;
    private readonly BackupOptions _options;
    private readonly WildcardMatcher _excluded;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        SqlConnectionFactory connections, IOptions<BackupOptions> options, ILogger<BackupService> logger)
    {
        _connections = connections;
        _options = options.Value;
        _excluded = new WildcardMatcher(_options.ExcludedDatabases);
        _logger = logger;
    }

    private int TimeoutSeconds => _options.CommandTimeoutMinutes * 60;

    /// <summary>Bancos de usuário elegíveis (exclui sistema, histórico e a lista de exclusões).</summary>
    public async Task<IReadOnlyList<BackupTarget>> ListTargetsAsync(CancellationToken ct = default)
    {
        await using var cn = await _connections.OpenMasterAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = """
            SELECT d.name,
                   d.state_desc,
                   CAST(ISNULL(SUM(CAST(mf.size AS bigint)) * 8 / 1024, 0) AS bigint)
            FROM sys.databases AS d
            LEFT JOIN sys.master_files AS mf ON mf.database_id = d.database_id
            WHERE d.database_id > 4
              AND d.is_distributor = 0
            GROUP BY d.name, d.state_desc
            ORDER BY d.name;
            """;

        var history = _connections.HistoryDatabaseName;
        var result = new List<BackupTarget>();

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var name = rd.GetString(0);
            if (_excluded.IsMatch(name))
                continue;
            if (history is not null && string.Equals(name, history, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new BackupTarget(name, rd.GetString(1), rd.GetInt64(2)));
        }

        return result;
    }

    /// <summary>Confere se o nome informado está entre os bancos elegíveis.</summary>
    public async Task<string> ResolveTargetAsync(string database, CancellationToken ct = default)
    {
        var targets = await ListTargetsAsync(ct);
        var match = targets.FirstOrDefault(t => string.Equals(t.Name, database, StringComparison.OrdinalIgnoreCase))
            ?? throw new RestoreValidationException(
                $"O banco '{database}' não está disponível para backup.", StatusCodes.Status403Forbidden);

        if (!string.Equals(match.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            throw new RestoreValidationException(
                $"O banco '{match.Name}' está em estado {match.State} e não pode ser copiado.");

        return match.Name;
    }

    /// <summary>BACKUP COPY_ONLY do banco. Nome: banco_DDMMAAAA_HHmm.bak.</summary>
    public async Task<BackupFileResult> BackupAsync(
        string database, Action<int> onSessionStarted, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Path))
            throw new RestoreValidationException("Configure Backup:Path antes de executar backups.");

        var fileName = $"{database}_{DateTime.Now:ddMMyyyy_HHmm}.bak";
        var path = _options.Path.TrimEnd('\\', '/') + "\\" + fileName;

        await using var cn = await _connections.OpenMasterAsync(ct);
        onSessionStarted(await GetSessionIdAsync(cn, ct));

        _logger.LogInformation("Backup de {Database} em {Path}.", database, path);

        try
        {
            await ExecuteBackupAsync(cn, database, path, _options.Compression, ct);
        }
        catch (SqlException ex) when (_options.Compression)
        {
            _logger.LogWarning(ex, "Backup com COMPRESSION falhou; repetindo sem compressão.");
            await ExecuteBackupAsync(cn, database, path, compression: false, ct);
        }

        return new BackupFileResult(path, await GetBackupSizeAsync(cn, path, ct));
    }

    /// <summary>Remove backups mais antigos que a retenção (xp_delete_file).</summary>
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        if (_options.RetentionDays <= 0 || string.IsNullOrWhiteSpace(_options.Path))
            return;

        await using var cn = await _connections.OpenMasterAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC master.dbo.xp_delete_file 0, @folder, N'BAK', @cutoff, 0;";
        cmd.Parameters.Add("@folder", SqlDbType.NVarChar, 1024).Value = _options.Path.TrimEnd('\\', '/') + "\\";
        cmd.Parameters.Add("@cutoff", SqlDbType.DateTime).Value = DateTime.Now.AddDays(-_options.RetentionDays);

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Backups anteriores a {Days} dias removidos de {Path}.",
            _options.RetentionDays, _options.Path);
    }

    private async Task ExecuteBackupAsync(
        SqlConnection cn, string database, string path, bool compression, CancellationToken ct)
    {
        var options = new List<string> { "COPY_ONLY", "INIT", "STATS = 5" };
        if (compression) options.Insert(1, "COMPRESSION");
        if (_options.Checksum) options.Insert(1, "CHECKSUM");

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $"BACKUP DATABASE @db TO DISK = @path WITH {string.Join(", ", options)};";
        cmd.CommandTimeout = 0;
        cmd.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = database;
        cmd.Parameters.Add("@path", SqlDbType.NVarChar, 1024).Value = path;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> GetBackupSizeAsync(SqlConnection cn, string path, CancellationToken ct)
    {
        try
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandTimeout = 60;
            cmd.CommandText = """
                SELECT TOP (1) CAST(bs.compressed_backup_size AS bigint)
                FROM msdb.dbo.backupmediafamily AS bmf
                JOIN msdb.dbo.backupset AS bs ON bs.media_set_id = bmf.media_set_id
                WHERE bmf.physical_device_name = @path
                ORDER BY bs.backup_finish_date DESC;
                """;
            cmd.Parameters.Add("@path", SqlDbType.NVarChar, 1024).Value = path;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<int> GetSessionIdAsync(SqlConnection cn, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT CAST(@@SPID AS int);";
        cmd.CommandTimeout = 60;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}
