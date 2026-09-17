using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;

namespace SqlRestoreManager.Services.Sql;

/// <summary>
/// Operações administrativas no SQL Server (sempre conectado ao master).
/// Stateless: registrado como singleton.
/// </summary>
public sealed class SqlRestoreService
{
    private const int ShortTimeoutSeconds = 120;
    private const string LockResourcePrefix = "SqlRestoreManager:";

    private readonly SqlConnectionFactory _connections;
    private readonly DatabaseCatalog _catalog;
    private readonly RestoreOptions _options;
    private readonly ILogger<SqlRestoreService> _logger;

    public SqlRestoreService(
        SqlConnectionFactory connections,
        DatabaseCatalog catalog,
        IOptions<RestoreOptions> options,
        ILogger<SqlRestoreService> logger)
    {
        _connections = connections;
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------ Sessões

    public async Task<IReadOnlyList<SqlSessionInfo>> GetSessionsAsync(
        string database, CancellationToken ct = default)
    {
        var db = await _catalog.EnsureRestorableAsync(database, ct);
        await using var cn = await _connections.OpenMasterAsync(ct);
        return await QuerySessionsAsync(cn, db, ct);
    }

    private static async Task<List<SqlSessionInfo>> QuerySessionsAsync(
        SqlConnection cn, string database, CancellationToken ct)
    {
        // session_id é smallint: CAST evita InvalidCastException no GetInt32.
        await using var cmd = CreateCommand(cn, """
            SELECT CAST(s.session_id AS int),
                   ISNULL(s.login_name, N''),
                   ISNULL(s.host_name, N''),
                   ISNULL(s.program_name, N'')
            FROM sys.dm_exec_sessions AS s
            WHERE s.database_id = DB_ID(@db)
              AND s.session_id <> @@SPID
            ORDER BY s.session_id;
            """, ShortTimeoutSeconds);
        AddDatabaseParameter(cmd, database);

        var result = new List<SqlSessionInfo>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new SqlSessionInfo(rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3)));

        return result;
    }

    // ------------------------------------------------------------- Inspeção

    public async Task<BackupInspection> InspectBackupAsync(
        string sqlServerBackupPath, CancellationToken ct = default)
    {
        await using var cn = await _connections.OpenMasterAsync(ct);
        var serverMajor = await GetServerMajorVersionAsync(cn, ct);

        var sets = new List<(int Position, int Type, string Database, DateTime? Finish, string? Server, int Major,
            bool HasChecksums)>();

        await using (var header = CreateCommand(cn, "RESTORE HEADERONLY FROM DISK = @bak;", ShortTimeoutSeconds))
        {
            AddPathParameter(header, "@bak", sqlServerBackupPath);

            await using var rd = await header.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                sets.Add((
                    Convert.ToInt32(rd["Position"]),
                    Convert.ToInt32(rd["BackupType"]),
                    rd["DatabaseName"] as string ?? string.Empty,
                    rd["BackupFinishDate"] as DateTime?,
                    rd["ServerName"] as string,
                    Convert.ToInt32(rd["SoftwareVersionMajor"]),
                    rd["HasBackupChecksums"] is bool hasChecksums && hasChecksums));
            }
        }

        if (sets.Count == 0)
            throw new RestoreValidationException("Backup inválido ou sem cabeçalho.");

        // BackupType 1 = FULL. Havendo vários sets anexados, usa o FULL mais recente.
        var fullSets = sets.Where(s => s.Type == 1).ToList();
        if (fullSets.Count == 0)
            throw new RestoreValidationException(
                "O arquivo não contém backup FULL. Backups diferenciais/log não são suportados.");

        var full = fullSets.MaxBy(s => s.Position);

        if (full.Major > serverMajor)
            throw new RestoreValidationException(
                $"Backup gerado no SQL Server versão {full.Major}; o servidor de destino é versão {serverMajor}. " +
                "Não é possível restaurar em versão inferior.");

        var files = new List<BackupFile>();
        await using (var list = CreateCommand(cn,
                         "RESTORE FILELISTONLY FROM DISK = @bak WITH FILE = @pos;", ShortTimeoutSeconds))
        {
            AddPathParameter(list, "@bak", sqlServerBackupPath);
            list.Parameters.Add("@pos", SqlDbType.Int).Value = full.Position;

            await using var rd = await list.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var type = (rd["Type"] as string)?.Trim();
                files.Add(new BackupFile(
                    rd["LogicalName"] as string ?? string.Empty,
                    rd["PhysicalName"] as string ?? string.Empty,
                    string.IsNullOrEmpty(type) ? '?' : char.ToUpperInvariant(type[0])));
            }
        }

        if (files.Count == 0)
            throw new RestoreValidationException("Nenhum arquivo lógico encontrado no backup.");

        if (!full.HasChecksums)
            _logger.LogInformation(
                "Backup gerado sem CHECKSUM: a verificação de integridade será feita sem essa opção.");

        return new BackupInspection(
            full.Database, full.Position, full.Finish, full.Server, full.Major, full.HasChecksums, files);
    }

    // --------------------------------------------------------------- Restore

    /// <summary>
    /// Executa o restore. <paramref name="onSessionStarted"/> recebe o SPID da conexão
    /// (usado para consultar o progresso). O <paramref name="ct"/> só é respeitado ANTES
    /// de o banco ser colocado OFFLINE: a partir daí o restore não é abortado.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        Action<int> onSessionStarted,
        CancellationToken ct = default)
    {
        // Revalida no momento do restore (o banco pode ter sido bloqueado/removido após o upload).
        var db = await _catalog.EnsureRestorableAsync(request.TargetDatabase, ct);

        await using var cn = await _connections.OpenMasterAsync(ct);
        onSessionStarted(await GetSessionIdAsync(cn, ct));

        // Lock entre instâncias (dev + IIS) — liberado ao fechar a conexão.
        await AcquireRestoreLockAsync(cn, db, ct);

        var (dataPath, logPath) = await ResolveTargetPathsAsync(cn, ct);
        var moves = BuildMoves(db, request.Backup.Files, dataPath, logPath);

        var state = await GetDatabaseStateAsync(cn, db, ct);
        var sessions = state is null ? 0 : (await QuerySessionsAsync(cn, db, ct)).Count;
        var quoted = QuoteName(db);
        var tookOffline = false;

        ct.ThrowIfCancellationRequested();

        // A partir daqui NÃO usamos o ct: abortar deixaria o banco inconsistente.
        if (state is { State: "ONLINE" })
        {
            _logger.LogInformation("Colocando {Database} OFFLINE ({Sessions} sessões serão encerradas).", db, sessions);
            await ExecuteAsync(cn, $"ALTER DATABASE {quoted} SET OFFLINE WITH ROLLBACK IMMEDIATE;");
            tookOffline = true;
        }
        else if (state is not null)
        {
            _logger.LogWarning("Banco {Database} está em estado {State}; restore seguirá sem OFFLINE.", db, state.State);
        }

        try
        {
            await ExecuteRestoreAsync(cn, db, request, moves);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no RESTORE de {Database}.", db);
            if (tookOffline)
                await TryBringOnlineAsync(cn, db, quoted);
            throw;
        }

        await EnsureMultiUserAsync(cn, db, quoted);
        return new RestoreResult(sessions, dataPath, logPath);
    }

    public async Task<bool> DatabaseExistsAsync(string database, CancellationToken ct = default)
    {
        await using var cn = await _connections.OpenMasterAsync(ct);
        return await GetDatabaseStateAsync(cn, database, ct) is not null;
    }

    /// <summary>RESTORE VERIFYONLY: confere se o backup é legível e íntegro.</summary>
    public async Task VerifyBackupAsync(
        string sqlServerBackupPath,
        int position,
        bool hasChecksums,
        Action<int> onSessionStarted,
        CancellationToken ct = default)
    {
        await using var cn = await _connections.OpenMasterAsync(ct);
        onSessionStarted(await GetSessionIdAsync(cn, ct));

        // CHECKSUM só é aceito quando o backup foi gerado com essa opção.
        var options = hasChecksums ? "FILE = @pos, CHECKSUM, STATS = 5" : "FILE = @pos, STATS = 5";

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $"RESTORE VERIFYONLY FROM DISK = @bak WITH {options};";
        cmd.CommandTimeout = 0;
        AddPathParameter(cmd, "@bak", sqlServerBackupPath);
        cmd.Parameters.Add("@pos", SqlDbType.Int).Value = position;

        _logger.LogInformation("Verificando backup {Backup} (FILE={Position}).", sqlServerBackupPath, position);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// BACKUP ... WITH COPY_ONLY do banco atual, para poder voltar atrás.
    /// Retorna o caminho gerado (visto pelo SQL Server).
    /// </summary>
    public async Task<string> CreateSafetyBackupAsync(
        string database, string folder, Action<int> onSessionStarted, CancellationToken ct = default)
    {
        var db = await _catalog.EnsureRestorableAsync(database, ct);
        var fileName = $"{db}_pre_restore_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        var path = CombineServerPath(folder, fileName);

        await using var cn = await _connections.OpenMasterAsync(ct);
        onSessionStarted(await GetSessionIdAsync(cn, ct));

        _logger.LogInformation("Backup de segurança de {Database} em {Path}.", db, path);

        try
        {
            await ExecuteBackupAsync(cn, db, path, _options.PreRestore.SafetyBackupCompression, ct);
        }
        catch (SqlException ex) when (_options.PreRestore.SafetyBackupCompression)
        {
            // Edições sem suporte a compressão (ex.: Express): repete sem COMPRESSION.
            _logger.LogWarning(ex, "Backup com COMPRESSION falhou; repetindo sem compressão.");
            await ExecuteBackupAsync(cn, db, path, compression: false, ct);
        }

        return path;
    }

    /// <summary>Remove backups de segurança antigos (xp_delete_file).</summary>
    public async Task CleanupSafetyBackupsAsync(string folder, int retentionDays, CancellationToken ct = default)
    {
        if (retentionDays <= 0)
            return;

        await using var cn = await _connections.OpenMasterAsync(ct);
        await using var cmd = CreateCommand(cn, """
            EXEC master.dbo.xp_delete_file 0, @folder, N'BAK', @cutoff, 0;
            """, ShortTimeoutSeconds);
        AddPathParameter(cmd, "@folder", folder.TrimEnd('\\', '/') + "\\");
        cmd.Parameters.Add("@cutoff", SqlDbType.DateTime).Value = DateTime.Now.AddDays(-retentionDays);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Encerra uma sessão (usado no cancelamento).</summary>
    public async Task KillSessionAsync(int sessionId, CancellationToken ct = default)
    {
        await using var cn = await _connections.OpenMasterAsync(ct);
        // Valor inteiro: sem risco de injeção.
        await using var cmd = CreateCommand(cn, $"KILL {sessionId};", ShortTimeoutSeconds);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogWarning("Sessão {Spid} encerrada (KILL).", sessionId);
    }

    private static async Task ExecuteBackupAsync(
        SqlConnection cn, string database, string path, bool compression, CancellationToken ct)
    {
        var options = compression ? "COPY_ONLY, COMPRESSION, CHECKSUM, STATS = 5" : "COPY_ONLY, CHECKSUM, STATS = 5";

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $"BACKUP DATABASE @db TO DISK = @path WITH {options};";
        cmd.CommandTimeout = 0;
        AddDatabaseParameter(cmd, database);
        AddPathParameter(cmd, "@path", path);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<double?> GetRestorePercentAsync(int sessionId, CancellationToken ct = default)
    {
        await using var cn = await _connections.OpenMasterAsync(ct);
        await using var cmd = CreateCommand(cn, """
            SELECT TOP (1) CAST(r.percent_complete AS float)
            FROM sys.dm_exec_requests AS r
            WHERE r.session_id = @spid
              AND (r.command LIKE N'RESTORE%' OR r.command LIKE N'BACKUP%');
            """, 30);
        cmd.Parameters.Add("@spid", SqlDbType.Int).Value = sessionId;

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is double d ? d : null;
    }

    private async Task ExecuteRestoreAsync(
        SqlConnection cn, string db, RestoreRequest request, IReadOnlyList<(string Logical, string Physical)> moves)
    {
        // Banco, caminho, FILE e MOVE totalmente parametrizados (sem concatenação de valores).
        var sql = new StringBuilder("""
            RESTORE DATABASE @db
            FROM DISK = @bak
            WITH FILE = @pos,
                 REPLACE,
                 STATS = 5
            """);

        await using var cmd = cn.CreateCommand();
        AddDatabaseParameter(cmd, db);
        AddPathParameter(cmd, "@bak", request.SqlServerBackupPath);
        cmd.Parameters.Add("@pos", SqlDbType.Int).Value = request.Backup.Position;

        for (var i = 0; i < moves.Count; i++)
        {
            sql.Append($",{Environment.NewLine}     MOVE @l{i} TO @p{i}");
            cmd.Parameters.Add($"@l{i}", SqlDbType.NVarChar, 128).Value = moves[i].Logical;
            AddPathParameter(cmd, $"@p{i}", moves[i].Physical);
        }

        sql.Append(';');
        cmd.CommandText = sql.ToString();
        cmd.CommandTimeout = 0;

        _logger.LogInformation(
            "Iniciando RESTORE de {Backup} (FILE={Position}) em {Database}.",
            request.SqlServerBackupPath, request.Backup.Position, db);

        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task TryBringOnlineAsync(SqlConnection cn, string db, string quoted)
    {
        try
        {
            await using var cmd = CreateCommand(cn, $"""
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND state_desc = N'OFFLINE')
                    ALTER DATABASE {quoted} SET ONLINE;
                """, ShortTimeoutSeconds);
            AddDatabaseParameter(cmd, db);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);

            var state = await GetDatabaseStateAsync(cn, db, CancellationToken.None);
            _logger.LogWarning("Após falha, {Database} está em estado {State}.", db, state?.State ?? "INEXISTENTE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível recolocar {Database} ONLINE após falha.", db);
        }
    }

    private async Task EnsureMultiUserAsync(SqlConnection cn, string db, string quoted)
    {
        try
        {
            await using var cmd = CreateCommand(cn, $"""
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND user_access_desc <> N'MULTI_USER')
                    ALTER DATABASE {quoted} SET MULTI_USER WITH ROLLBACK IMMEDIATE;
                """, ShortTimeoutSeconds);
            AddDatabaseParameter(cmd, db);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Restore já concluído: apenas registra.
            _logger.LogWarning(ex, "Restore concluído, mas não foi possível garantir MULTI_USER em {Database}.", db);
        }
    }

    // ----------------------------------------------------------- Auxiliares

    private static async Task AcquireRestoreLockAsync(SqlConnection cn, string db, CancellationToken ct)
    {
        await using var cmd = CreateCommand(cn, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                 @Resource    = @resource,
                 @LockMode    = N'Exclusive',
                 @LockOwner   = N'Session',
                 @LockTimeout = 0,
                 @DbPrincipal = N'public';
            SELECT @result;
            """, ShortTimeoutSeconds);
        cmd.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value =
            LockResourcePrefix + db.ToUpperInvariant();

        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (result < 0)
            throw new RestoreValidationException(
                $"Já existe um restore em andamento para '{db}' (possivelmente em outra instância do SqlRestoreManager).",
                StatusCodes.Status409Conflict);
    }

    private async Task<(string DataPath, string LogPath)> ResolveTargetPathsAsync(
        SqlConnection cn, CancellationToken ct)
    {
        await using var cmd = CreateCommand(cn, """
            SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512)),
                   CAST(SERVERPROPERTY('InstanceDefaultLogPath')  AS nvarchar(512));
            """, ShortTimeoutSeconds);

        string? defaultData = null, defaultLog = null;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            if (await rd.ReadAsync(ct))
            {
                defaultData = rd.IsDBNull(0) ? null : rd.GetString(0);
                defaultLog = rd.IsDBNull(1) ? null : rd.GetString(1);
            }
        }

        var data = FirstNonEmpty(_options.DataPath, defaultData)
            ?? throw new RestoreValidationException(
                "Não foi possível determinar a pasta de dados. Configure Restore:DataPath.");
        var log = FirstNonEmpty(_options.LogPath, defaultLog)
            ?? throw new RestoreValidationException(
                "Não foi possível determinar a pasta de log. Configure Restore:LogPath.");

        return (data, log);
    }

    internal static List<(string Logical, string Physical)> BuildMoves(
        string db, IReadOnlyList<BackupFile> files, string dataPath, string logPath)
    {
        var moves = new List<(string, string)>(files.Count);
        int data = 0, log = 0, stream = 0, fullText = 0;

        foreach (var f in files)
        {
            var physical = f.Type switch
            {
                'D' => CombineServerPath(dataPath, ++data == 1 ? $"{db}.mdf" : $"{db}_{data}.ndf"),
                'L' => CombineServerPath(logPath, ++log == 1 ? $"{db}_log.ldf" : $"{db}_log{log}.ldf"),
                'S' => CombineServerPath(dataPath, $"{db}_fs{++stream}"),   // FILESTREAM: diretório
                'F' => CombineServerPath(dataPath, $"{db}_ft{++fullText}"), // full-text: diretório
                _ => throw new RestoreValidationException(
                    $"Tipo de arquivo '{f.Type}' não suportado (arquivo lógico '{f.LogicalName}').")
            };

            moves.Add((f.LogicalName, physical));
        }

        return moves;
    }

    private static async Task<DatabaseState?> GetDatabaseStateAsync(
        SqlConnection cn, string db, CancellationToken ct)
    {
        await using var cmd = CreateCommand(cn,
            "SELECT state_desc, user_access_desc FROM sys.databases WHERE name = @db;", ShortTimeoutSeconds);
        AddDatabaseParameter(cmd, db);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new DatabaseState(rd.GetString(0), rd.GetString(1)) : null;
    }

    private static async Task<int> GetSessionIdAsync(SqlConnection cn, CancellationToken ct)
    {
        await using var cmd = CreateCommand(cn, "SELECT CAST(@@SPID AS int);", ShortTimeoutSeconds);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<int> GetServerMajorVersionAsync(SqlConnection cn, CancellationToken ct)
    {
        await using var cmd = CreateCommand(cn,
            "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);", ShortTimeoutSeconds);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static SqlCommand CreateCommand(SqlConnection cn, string sql, int timeoutSeconds)
    {
        var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;
        return cmd;
    }

    private static async Task ExecuteAsync(SqlConnection cn, string sql)
    {
        await using var cmd = CreateCommand(cn, sql, ShortTimeoutSeconds);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void AddDatabaseParameter(SqlCommand cmd, string db) =>
        cmd.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = db;

    private static void AddPathParameter(SqlCommand cmd, string name, string path) =>
        cmd.Parameters.Add(name, SqlDbType.NVarChar, 1024).Value = path;

    /// <summary>Nome já validado pelo DatabaseCatalog; o escape é defesa adicional.</summary>
    private static string QuoteName(string value) => "[" + value.Replace("]", "]]") + "]";

    private static string CombineServerPath(string directory, string name) =>
        directory.TrimEnd('\\', '/') + "\\" + name;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
