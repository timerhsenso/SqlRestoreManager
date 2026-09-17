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

/// <summary>
/// Lista os bancos reais do servidor aplicando <see cref="DatabasePolicy"/>.
/// Também é a única porta de entrada para nomes de banco vindos do usuário.
/// </summary>
public sealed class DatabaseCatalog
{
    private readonly SqlConnectionFactory _connections;
    private readonly DatabasePolicy _policy;

    public DatabaseCatalog(SqlConnectionFactory connections, DatabasePolicy policy)
    {
        _connections = connections;
        _policy = policy;
    }

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
    /// Retorna o nome exatamente como está no SQL Server.
    /// </summary>
    public async Task<string> ResolveAsync(string? database, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(database))
            throw new RestoreValidationException("Informe o banco de destino.");

        var requested = database.Trim();

        // Checagem prévia sem I/O (bancos de sistema/bloqueados/nomes inválidos).
        var policy = _policy.Evaluate(requested);
        if (policy.Visibility != DatabaseVisibility.Allowed)
            throw new RestoreValidationException(
                $"O banco '{requested}' não pode ser restaurado ({policy.Reason}).",
                StatusCodes.Status403Forbidden);

        var databases = await ListAsync(ct);
        var match = databases.FirstOrDefault(d => string.Equals(d.Name, requested, StringComparison.Ordinal))
                    ?? databases.SingleOrDefault(d => string.Equals(d.Name, requested, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new RestoreValidationException(
                $"O banco '{requested}' não existe no servidor.", StatusCodes.Status404NotFound);

        if (!match.CanRestore)
            throw new RestoreValidationException(
                $"O banco '{match.Name}' não pode ser restaurado ({match.Reason}).",
                StatusCodes.Status403Forbidden);

        return match.Name;
    }
}
