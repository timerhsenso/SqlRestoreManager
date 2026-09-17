using System.Text.RegularExpressions;
using SqlRestoreManager.Configuration;

namespace SqlRestoreManager.Services.Sql;

public enum DatabaseVisibility
{
    /// <summary>Não aparece na lista.</summary>
    Hidden,

    /// <summary>Aparece desabilitado.</summary>
    Disabled,

    /// <summary>Pode ser restaurado.</summary>
    Allowed
}

public sealed record DatabasePolicyResult(DatabaseVisibility Visibility, string? Reason);

/// <summary>
/// Regras (puras, sem I/O) que decidem se um banco do servidor pode ser restaurado.
/// </summary>
public sealed partial class DatabasePolicy
{
    private static readonly IReadOnlySet<string> SystemDatabases =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "master", "model", "msdb", "tempdb", "distribution", "SSISDB"
        };

    private readonly Regex[] _blocked;
    private readonly bool _showBlocked;
    private readonly string? _historyDatabase;

    public DatabasePolicy(RestoreOptions options, string? historyDatabase)
    {
        _blocked = options.BlockedDatabases
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(ToRegex)
            .ToArray();
        _showBlocked = options.ShowBlockedDatabases;
        _historyDatabase = historyDatabase;
    }

    public DatabasePolicyResult Evaluate(string name, bool isDistributor = false)
    {
        if (SystemDatabases.Contains(name) || isDistributor)
            return new(DatabaseVisibility.Hidden, "banco de sistema");

        if (_historyDatabase is not null &&
            string.Equals(name, _historyDatabase, StringComparison.OrdinalIgnoreCase))
            return new(DatabaseVisibility.Disabled, "usado pelo Restore Manager");

        if (_blocked.Any(r => r.IsMatch(name)))
            return new(_showBlocked ? DatabaseVisibility.Disabled : DatabaseVisibility.Hidden, "bloqueado");

        if (!IsSupportedName(name))
            return new(DatabaseVisibility.Disabled, "nome não suportado");

        return new(DatabaseVisibility.Allowed, null);
    }

    /// <summary>
    /// O nome vira nome de arquivo (.mdf/.ldf): só letras, números, espaço, '_', '-' e '.'.
    /// </summary>
    public static bool IsSupportedName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name == name.Trim() &&
        !name.EndsWith('.') &&
        SupportedNameRegex().IsMatch(name);

    private static Regex ToRegex(string pattern)
    {
        var body = Regex.Escape(pattern.Trim())
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return new Regex($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_\-\. ]{1,128}$")]
    private static partial Regex SupportedNameRegex();
}
