using System.Text.RegularExpressions;

namespace SqlRestoreManager.Services.Sql;

/// <summary>Padrões com * e ?, sem diferenciar maiúsculas (usado em bloqueios/exclusões).</summary>
public sealed class WildcardMatcher
{
    private readonly Regex[] _patterns;

    public WildcardMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(ToRegex)
            .ToArray();
    }

    public bool IsMatch(string value) => _patterns.Any(r => r.IsMatch(value));

    private static Regex ToRegex(string pattern)
    {
        var body = Regex.Escape(pattern.Trim())
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return new Regex($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
