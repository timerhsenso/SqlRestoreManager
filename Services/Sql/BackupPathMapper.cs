using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;

namespace SqlRestoreManager.Services.Sql;

/// <summary>
/// Converte um caminho gravado pela aplicação (TempPath) para o caminho
/// equivalente visto pelo serviço do SQL Server (SqlServerTempPath).
/// Necessário quando a aplicação roda em outra máquina (ex.: Visual Studio em dev).
/// </summary>
public sealed class BackupPathMapper
{
    private readonly RestoreOptions _options;

    public BackupPathMapper(IOptions<RestoreOptions> options) => _options = options.Value;

    public string ToSqlServerPath(string localPath)
    {
        if (string.IsNullOrWhiteSpace(_options.SqlServerTempPath))
            return localPath;

        var root = Path.GetFullPath(_options.TempPath);
        var full = Path.GetFullPath(localPath);
        var relative = Path.GetRelativePath(root, full);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("O arquivo de backup está fora da pasta temporária configurada.");

        return _options.SqlServerTempPath.TrimEnd('\\', '/') + "\\" + relative.Replace('/', '\\');
    }
}
