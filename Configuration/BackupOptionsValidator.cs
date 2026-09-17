using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SqlRestoreManager.Configuration;

public sealed partial class BackupOptionsValidator : IValidateOptions<BackupOptions>
{
    public ValidateOptionsResult Validate(string? name, BackupOptions o)
    {
        var errors = new List<string>();

        if (o.Enabled && string.IsNullOrWhiteSpace(o.Path))
            errors.Add("Backup:Path é obrigatório quando Backup:Enabled = true.");

        if (!string.IsNullOrWhiteSpace(o.Path) && !WindowsAbsolutePathRegex().IsMatch(o.Path))
            errors.Add("Backup:Path deve ser um caminho absoluto Windows visto pelo SQL Server.");

        if (o.RetentionDays is < 0 or > 3650)
            errors.Add("Backup:RetentionDays deve estar entre 0 e 3650.");

        if (o.LogTargetSizeMB is < 1 or > 1_048_576)
            errors.Add("Backup:LogTargetSizeMB deve estar entre 1 e 1048576.");

        if (o.LogGrowthMB is < 0 or > 10_240)
            errors.Add("Backup:LogGrowthMB deve estar entre 0 e 10240.");

        if (o.CommandTimeoutMinutes is < 1 or > 1440)
            errors.Add("Backup:CommandTimeoutMinutes deve estar entre 1 e 1440.");

        foreach (var pattern in o.ExcludedDatabases)
        {
            if (string.IsNullOrWhiteSpace(pattern) || !PatternRegex().IsMatch(pattern))
                errors.Add($"Backup:ExcludedDatabases contém padrão inválido: '{pattern}'.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_\-\. \*\?]{1,128}$")]
    private static partial Regex PatternRegex();

    [GeneratedRegex(@"^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)")]
    private static partial Regex WindowsAbsolutePathRegex();
}
