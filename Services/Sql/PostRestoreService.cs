using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;

namespace SqlRestoreManager.Services.Sql;

public sealed record PostRestoreStep(string Name, bool Success, string Message);

public sealed record PostRestoreResult(
    IReadOnlyList<PostRestoreStep> Steps,
    int? LogSizeBeforeMB,
    int? LogSizeAfterMB)
{
    public bool HasWarnings => Steps.Any(s => !s.Success);

    public string Summary => string.Join(
        Environment.NewLine,
        Steps.Select(s => $"[{(s.Success ? "OK" : "ALERTA")}] {s.Name}: {s.Message}"));
}

/// <summary>
/// Ajustes após o restore. Cada etapa é isolada: uma falha vira alerta e as demais continuam.
/// </summary>
public sealed partial class PostRestoreService
{
    private readonly SqlConnectionFactory _connections;
    private readonly PostRestoreOptions _options;
    private readonly ILogger<PostRestoreService> _logger;

    public PostRestoreService(
        SqlConnectionFactory connections,
        IOptions<RestoreOptions> options,
        ILogger<PostRestoreService> logger)
    {
        _connections = connections;
        _options = options.Value.PostRestore;
        _logger = logger;
    }

    private int TimeoutSeconds => _options.CommandTimeoutMinutes * 60;

    /// <param name="database">Nome já validado pelo DatabaseCatalog.</param>
    /// <param name="onStep">Notificação de etapa (para a tela).</param>
    public async Task<PostRestoreResult> RunAsync(string database, Func<string, Task> onStep)
    {
        var steps = new List<PostRestoreStep>();
        var logBefore = await TryGetLogSizeMbAsync(database);

        if (_options.RemoveReplication)
            await RunStepAsync(steps, "Replicação", "Verificando replicação herdada...", onStep,
                () => RemoveReplicationAsync(database));

        if (_options.SetRecoverySimple)
            await RunStepAsync(steps, "Recovery model", "Alterando recovery model para SIMPLE...", onStep,
                () => SetRecoverySimpleAsync(database));

        if (_options.ShrinkLog)
            await RunStepAsync(steps, "Log", "Reduzindo arquivo de log...", onStep,
                () => ShrinkLogAsync(database));

        if (_options.FixOrphanUsers)
            await RunStepAsync(steps, "Usuários órfãos", "Corrigindo usuários órfãos...", onStep,
                () => FixOrphanUsersAsync(database));

        if (!string.IsNullOrWhiteSpace(_options.ScriptsPath))
        {
            foreach (var script in GetScripts(_options.ScriptsPath, database))
            {
                var relative = Path.GetRelativePath(_options.ScriptsPath, script);
                await RunStepAsync(steps, $"Script {relative}", $"Executando script {relative}...", onStep,
                    () => RunScriptAsync(database, script));
            }
        }

        var logAfter = await TryGetLogSizeMbAsync(database);
        return new PostRestoreResult(steps, logBefore, logAfter);
    }

    // ----------------------------------------------------------------- Etapas

    private async Task<string> RemoveReplicationAsync(string database)
    {
        await using var cn = await _connections.OpenMasterAsync(CancellationToken.None);

        await using (var check = CreateCommand(cn, """
                         SELECT CAST(CASE WHEN is_published = 1
                                            OR is_subscribed = 1
                                            OR is_merge_published = 1
                                            OR log_reuse_wait_desc = N'REPLICATION'
                                          THEN 1 ELSE 0 END AS bit)
                         FROM sys.databases
                         WHERE name = @db;
                         """))
        {
            AddDatabaseParameter(check, database);
            if (await check.ExecuteScalarAsync(CancellationToken.None) is not true)
                return "sem replicação.";
        }

        await using var remove = CreateCommand(cn, "EXEC sys.sp_removedbreplication @dbname = @db;");
        AddDatabaseParameter(remove, database);
        await remove.ExecuteNonQueryAsync(CancellationToken.None);
        return "replicação herdada removida.";
    }

    private async Task<string> SetRecoverySimpleAsync(string database)
    {
        await using var cn = await _connections.OpenMasterAsync(CancellationToken.None);
        await using var cmd = CreateCommand(cn,
            $"ALTER DATABASE {QuoteName(database)} SET RECOVERY SIMPLE WITH NO_WAIT;");
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        return "SIMPLE.";
    }

    private async Task<string> ShrinkLogAsync(string database)
    {
        await using var cn = await OpenDatabaseAsync(database);
        var target = _options.LogTargetSizeMB;
        var messages = new List<string>();

        var files = new List<(int FileId, string Name)>();
        await using (var list = CreateCommand(cn,
                         "SELECT file_id, name FROM sys.database_files WHERE type = 1 ORDER BY file_id;"))
        await using (var rd = await list.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await rd.ReadAsync(CancellationToken.None))
                files.Add((rd.GetInt32(0), rd.GetString(1)));
        }

        foreach (var (fileId, name) in files)
        {
            // Duas passadas: após o CHECKPOINT o log costuma liberar mais VLFs.
            for (var pass = 0; pass < 2; pass++)
            {
                await ExecuteAsync(cn, "CHECKPOINT;");
                // Valores inteiros: sem risco de injeção.
                await ExecuteAsync(cn, $"DBCC SHRINKFILE ({fileId}, {target}) WITH NO_INFOMSGS;");

                if (await GetFileSizeMbAsync(cn, fileId) <= target)
                    break;
            }

            if (_options.LogGrowthMB > 0)
                await ExecuteAsync(cn,
                    $"ALTER DATABASE CURRENT MODIFY FILE (NAME = {QuoteName(name)}, FILEGROWTH = {_options.LogGrowthMB}MB);");

            var size = await GetFileSizeMbAsync(cn, fileId);
            messages.Add($"{name} = {size} MB");

            if (size > target)
            {
                var reason = await GetLogReuseWaitAsync(cn, database);
                throw new InvalidOperationException(
                    $"{string.Join("; ", messages)}. Não reduziu até {target} MB (log_reuse_wait: {reason}).");
            }
        }

        var growth = _options.LogGrowthMB > 0 ? $", crescimento {_options.LogGrowthMB} MB" : string.Empty;
        return $"{string.Join("; ", messages)}{growth}.";
    }

    private async Task<string> FixOrphanUsersAsync(string database)
    {
        await using var cn = await OpenDatabaseAsync(database);

        var orphans = new List<(string User, bool HasLogin)>();
        await using (var cmd = CreateCommand(cn, """
                         SELECT dp.name,
                                CAST(CASE WHEN byName.principal_id IS NULL THEN 0 ELSE 1 END AS bit)
                         FROM sys.database_principals AS dp
                         LEFT JOIN sys.server_principals AS bySid
                                ON bySid.sid = dp.sid
                         LEFT JOIN sys.server_principals AS byName
                                ON byName.name = dp.name COLLATE DATABASE_DEFAULT
                               AND byName.type = 'S'
                         WHERE dp.type = 'S'
                           AND dp.authentication_type = 1
                           AND dp.principal_id > 4
                           AND bySid.sid IS NULL
                         ORDER BY dp.name;
                         """))
        await using (var rd = await cmd.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await rd.ReadAsync(CancellationToken.None))
                orphans.Add((rd.GetString(0), rd.GetBoolean(1)));
        }

        if (orphans.Count == 0)
            return "nenhum usuário órfão.";

        var fixedUsers = new List<string>();
        foreach (var (user, _) in orphans.Where(o => o.HasLogin))
        {
            await ExecuteAsync(cn, $"ALTER USER {QuoteName(user)} WITH LOGIN = {QuoteName(user)};");
            fixedUsers.Add(user);
        }

        var withoutLogin = orphans.Where(o => !o.HasLogin).Select(o => o.User).ToList();

        var sb = new StringBuilder($"{fixedUsers.Count} corrigido(s)");
        if (fixedUsers.Count > 0)
            sb.Append($" ({string.Join(", ", fixedUsers)})");
        if (withoutLogin.Count > 0)
            sb.Append($"; sem login correspondente no servidor: {string.Join(", ", withoutLogin)}");
        return sb.Append('.').ToString();
    }

    private async Task<string> RunScriptAsync(string database, string path)
    {
        var batches = SplitBatches(await File.ReadAllTextAsync(path));
        if (batches.Count == 0)
            return "vazio.";

        await using var cn = await OpenDatabaseAsync(database);
        for (var i = 0; i < batches.Count; i++)
        {
            try
            {
                await ExecuteAsync(cn, batches[i]);
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException($"falha no lote {i + 1} de {batches.Count}: {ex.Message}", ex);
            }
        }

        return $"{batches.Count} lote(s) executado(s).";
    }

    // ------------------------------------------------------------ Auxiliares

    private async Task RunStepAsync(
        List<PostRestoreStep> steps, string name, string progress, Func<string, Task> onStep, Func<Task<string>> action)
    {
        await onStep(progress);
        try
        {
            var message = await action();
            steps.Add(new PostRestoreStep(name, true, message));
            _logger.LogInformation("Pós-restore [{Step}]: {Message}", name, message);
        }
        catch (Exception ex)
        {
            steps.Add(new PostRestoreStep(name, false, ex.Message));
            _logger.LogWarning(ex, "Pós-restore [{Step}] falhou.", name);
        }
    }

    internal static IEnumerable<string> GetScripts(string root, string database)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Restore:PostRestore:ScriptsPath não encontrado: {root}");

        var global = Directory.EnumerateFiles(root, "*.sql", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase);

        var dbFolder = Path.Combine(root, database);
        var specific = Directory.Exists(dbFolder)
            ? Directory.EnumerateFiles(dbFolder, "*.sql", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

        return global.Concat(specific).ToList();
    }

    internal static List<string> SplitBatches(string script) =>
        GoSeparatorRegex().Split(script)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToList();

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

    private async Task<int?> TryGetLogSizeMbAsync(string database)
    {
        try
        {
            await using var cn = await _connections.OpenMasterAsync(CancellationToken.None);
            await using var cmd = CreateCommand(cn, """
                SELECT CAST(SUM(CAST(size AS bigint)) * 8 / 1024 AS int)
                FROM sys.master_files
                WHERE database_id = DB_ID(@db) AND type = 1;
                """);
            AddDatabaseParameter(cmd, database);
            return await cmd.ExecuteScalarAsync(CancellationToken.None) is int mb ? mb : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível ler o tamanho do log de {Database}.", database);
            return null;
        }
    }

    private async Task<int> GetFileSizeMbAsync(SqlConnection cn, int fileId)
    {
        await using var cmd = CreateCommand(cn,
            "SELECT CAST(CAST(size AS bigint) * 8 / 1024 AS int) FROM sys.database_files WHERE file_id = @id;");
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = fileId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(CancellationToken.None));
    }

    private async Task<string> GetLogReuseWaitAsync(SqlConnection cn, string database)
    {
        await using var cmd = CreateCommand(cn,
            "SELECT log_reuse_wait_desc FROM sys.databases WHERE name = @db;");
        AddDatabaseParameter(cmd, database);
        return await cmd.ExecuteScalarAsync(CancellationToken.None) as string ?? "?";
    }

    private SqlCommand CreateCommand(SqlConnection cn, string sql)
    {
        var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = TimeoutSeconds;
        return cmd;
    }

    private async Task ExecuteAsync(SqlConnection cn, string sql)
    {
        await using var cmd = CreateCommand(cn, sql);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void AddDatabaseParameter(SqlCommand cmd, string database) =>
        cmd.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = database;

    private static string QuoteName(string value) => "[" + value.Replace("]", "]]") + "]";

    /// <summary>Linha contendo apenas GO (com comentário opcional), como no SSMS.</summary>
    [GeneratedRegex(@"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparatorRegex();
}
