using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;

namespace SqlRestoreManager.Services.Sql;

public sealed record DatabaseInfo(string Name, string State, bool CanRestore, string? Reason)
{
    public string Label
    {
        get
        {
            var label = Name;
            if (!string.Equals(State, "ONLINE", StringComparison.OrdinalIgnoreCase))
                label += $" ({State})";
            if (Reason is not null)
                label += $" - {Reason}";
            return label;
        }
    }
}

/// <param name="Exists">false = será criado pelo restore.</param>
public sealed record ResolvedDatabase(string Name, bool Exists);

/// <summary>
/// Lista os bancos reais do servidor aplicando <see cref="DatabasePolicy"/>.
/// Também é a única porta de entrada para nomes de banco vindos do usuário.
/// </summary>
public sealed class DatabaseCatalog
{
    private readonly SqlConnectionFactory _connections;
    private readonly DatabasePolicy _policy;
    private readonly RestoreOptions _options;

    public DatabaseCatalog(SqlConnectionFactory connections, DatabasePolicy policy, IOptions<RestoreOptions> options)
    {
        _connections = connections;
        _policy = policy;
        _options = options.Value;
    }

    public bool AllowNewDatabases => _options.AllowNewDatabases;

    public async Task<IReadOnlyList<DatabaseInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var cn = await _connections.OpenMasterAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = """
            SELECT d.name,
                   d.state_desc,
                   CAST(d.is_distributor AS bit)
            FROM sys.databases AS d
            WHERE d.database_id > 4
            ORDER BY d.name;
            """;

        var result = new List<DatabaseInfo>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var name = rd.GetString(0);
            var policy = _policy.Evaluate(name, rd.GetBoolean(2));
            if (policy.Visibility == DatabaseVisibility.Hidden)
                continue;

            result.Add(new DatabaseInfo(
                name,
                rd.GetString(1),
                policy.Visibility == DatabaseVisibility.Allowed,
                policy.Reason));
        }

        return result;
    }

    /// <summary>
    /// Valida o nome informado contra o servidor e as regras.
    /// </summary>
    /// <param name="createNew">
    /// true = o banco NÃO pode existir (será criado pelo restore);
    /// false = o banco precisa existir e estar liberado.
    /// </param>
    public async Task<ResolvedDatabase> ResolveAsync(
        string? database, bool createNew, CancellationToken ct = default)
    {
        var requested = CheckPolicy(database);
        var match = await FindAsync(requested, ct);

        if (!createNew)
        {
            if (match is null)
                throw new RestoreValidationException(
                    $"O banco '{requested}' não existe no servidor. Marque \"criar banco novo\" se quiser criá-lo.",
                    StatusCodes.Status404NotFound);

            EnsureCanRestore(match);
            return new ResolvedDatabase(match.Name, true);
        }

        if (!_options.AllowNewDatabases)
            throw new RestoreValidationException(
                "A criação de bancos novos está desabilitada (Restore:AllowNewDatabases).",
                StatusCodes.Status403Forbidden);

        if (match is not null)
            throw new RestoreValidationException(
                $"O banco '{match.Name}' já existe. Selecione-o na lista em vez de criar um novo.",
                StatusCodes.Status409Conflict);

        return new ResolvedDatabase(requested, false);
    }

    /// <summary>
    /// Revalidação no momento do restore: aceita banco existente liberado ou,
    /// se permitido, um banco que ainda será criado.
    /// </summary>
    public async Task<string> EnsureRestorableAsync(string? database, CancellationToken ct = default)
    {
        var requested = CheckPolicy(database);
        var match = await FindAsync(requested, ct);

        if (match is not null)
        {
            EnsureCanRestore(match);
            return match.Name;
        }

        if (!_options.AllowNewDatabases)
            throw new RestoreValidationException(
                $"O banco '{requested}' não existe no servidor.", StatusCodes.Status404NotFound);

        return requested;
    }

    private string CheckPolicy(string? database)
    {
        if (string.IsNullOrWhiteSpace(database))
            throw new RestoreValidationException("Informe o banco de destino.");

        var requested = database.Trim();
        var policy = _policy.Evaluate(requested);
        if (policy.Visibility != DatabaseVisibility.Allowed)
            throw new RestoreValidationException(
                $"O banco '{requested}' não pode ser restaurado ({policy.Reason}).",
                StatusCodes.Status403Forbidden);

        return requested;
    }

    private async Task<DatabaseInfo?> FindAsync(string requested, CancellationToken ct)
    {
        var databases = await ListAsync(ct);
        return databases.FirstOrDefault(d => string.Equals(d.Name, requested, StringComparison.Ordinal))
               ?? databases.SingleOrDefault(d => string.Equals(d.Name, requested, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureCanRestore(DatabaseInfo database)
    {
        if (!database.CanRestore)
            throw new RestoreValidationException(
                $"O banco '{database.Name}' não pode ser restaurado ({database.Reason}).",
                StatusCodes.Status403Forbidden);
    }
}
