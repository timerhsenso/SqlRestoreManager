using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;

namespace SqlRestoreManager.Services.Sql;

/// <summary>
/// Converte um caminho gravado/lido pela aplicação para o caminho equivalente
/// visto pelo serviço do SQL Server. Necessário quando a aplicação roda em outra
/// máquina (ex.: Visual Studio em dev acessando um compartilhamento).
/// </summary>
public sealed class BackupPathMapper
{
    private readonly (string Local, string Server)[] _mappings;

    public BackupPathMapper(IOptions<RestoreOptions> options)
    {
        var o = options.Value;
        _mappings = new[]
            {
                (o.TempPath, o.SqlServerTempPath),
                (o.LibraryPath, o.SqlServerLibraryPath)
            }
            .Where(m => !string.IsNullOrWhiteSpace(m.Item1) && !string.IsNullOrWhiteSpace(m.Item2))
            .Select(m => (Local: Path.GetFullPath(m.Item1!), Server: m.Item2!.TrimEnd('\\', '/')))
            .ToArray();
    }

    public string ToSqlServerPath(string localPath)
    {
        if (_mappings.Length == 0)
            return localPath;

        var full = Path.GetFullPath(localPath);

        foreach (var (local, server) in _mappings)
        {
            var relative = Path.GetRelativePath(local, full);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                continue;

            return server + "\\" + relative.Replace('/', '\\');
        }

        // Caminho fora das pastas mapeadas: o SQL Server precisa enxergá-lo como está.
        return localPath;
    }
}
