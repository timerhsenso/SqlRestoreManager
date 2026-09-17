using Microsoft.Data.SqlClient;

namespace SqlRestoreManager.Services.Sql;

/// <summary>
/// Cria conexões administrativas (sempre no master) e expõe o nome do banco de histórico.
/// </summary>
public sealed class SqlConnectionFactory
{
    private readonly string _masterConnectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        var admin = configuration.GetConnectionString("SqlAdminConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlAdminConnection não configurada.");

        var builder = new SqlConnectionStringBuilder(admin) { InitialCatalog = "master" };
        if (string.IsNullOrWhiteSpace(builder.ApplicationName) ||
            builder.ApplicationName == "Core Microsoft SqlClient Data Provider")
            builder.ApplicationName = "SqlRestoreManager";

        _masterConnectionString = builder.ConnectionString;

        var history = configuration.GetConnectionString("HistoryConnection");
        HistoryDatabaseName = string.IsNullOrWhiteSpace(history)
            ? null
            : new SqlConnectionStringBuilder(history).InitialCatalog;
    }

    /// <summary>Banco usado pelo histórico — nunca pode ser restaurado.</summary>
    public string? HistoryDatabaseName { get; }

    public async Task<SqlConnection> OpenMasterAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_masterConnectionString);
        try
        {
            await cn.OpenAsync(ct);
            return cn;
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
    }
}
