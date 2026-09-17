using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SqlRestoreManager.Configuration;

/// <summary>
/// Valida <see cref="RestoreOptions"/> no startup (ValidateOnStart):
/// a aplicação não sobe com configuração perigosa ou incompleta.
/// </summary>
public sealed partial class RestoreOptionsValidator : IValidateOptions<RestoreOptions>
{
    /// <summary>Limite imposto pelo IIS (requestLimits/maxAllowedContentLength é uint).</summary>
    public const int IisMaxUploadMB = 4000;

    public ValidateOptionsResult Validate(string? name, RestoreOptions o)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(o.TempPath) || !Path.IsPathFullyQualified(o.TempPath))
            errors.Add("Restore:TempPath deve ser um caminho absoluto (local ou UNC).");

        ValidateServerPath(o.SqlServerTempPath, "Restore:SqlServerTempPath", errors);
        ValidateServerPath(o.DataPath, "Restore:DataPath", errors);
        ValidateServerPath(o.LogPath, "Restore:LogPath", errors);

        if (o.MaxUploadMB is < 1 or > IisMaxUploadMB)
            errors.Add($"Restore:MaxUploadMB deve estar entre 1 e {IisMaxUploadMB}.");

        if (o.MaxExtractedGB < 1)
            errors.Add("Restore:MaxExtractedGB deve ser maior que zero.");

        if (o.ProgressPollSeconds is < 1 or > 60)
            errors.Add("Restore:ProgressPollSeconds deve estar entre 1 e 60.");

        if (o.TempRetentionHours < 1)
            errors.Add("Restore:TempRetentionHours deve ser maior que zero.");

        foreach (var pattern in o.BlockedDatabases)
        {
            if (string.IsNullOrWhiteSpace(pattern) || !BlockedPatternRegex().IsMatch(pattern))
                errors.Add($"Restore:BlockedDatabases contém padrão inválido: '{pattern}'.");
        }

        if (!string.IsNullOrWhiteSpace(o.LibraryPath) && !Path.IsPathFullyQualified(o.LibraryPath))
            errors.Add("Restore:LibraryPath deve ser um caminho absoluto (local ou UNC).");

        ValidateServerPath(o.SqlServerLibraryPath, "Restore:SqlServerLibraryPath", errors);

        if (!string.IsNullOrWhiteSpace(o.SqlServerLibraryPath) && string.IsNullOrWhiteSpace(o.LibraryPath))
            errors.Add("Restore:SqlServerLibraryPath exige Restore:LibraryPath.");

        if (!string.IsNullOrWhiteSpace(o.SevenZipPath) && !Path.IsPathFullyQualified(o.SevenZipPath))
            errors.Add("Restore:SevenZipPath deve ser o caminho absoluto do 7z.exe.");

        var pre = o.PreRestore;
        if (pre.SafetyBackup && string.IsNullOrWhiteSpace(pre.SafetyBackupPath))
            errors.Add("Restore:PreRestore:SafetyBackup exige Restore:PreRestore:SafetyBackupPath.");

        ValidateServerPath(pre.SafetyBackupPath, "Restore:PreRestore:SafetyBackupPath", errors);

        if (pre.SafetyBackupRetentionDays is < 0 or > 3650)
            errors.Add("Restore:PreRestore:SafetyBackupRetentionDays deve estar entre 0 e 3650.");

        var post = o.PostRestore;
        if (post.LogTargetSizeMB is < 1 or > 1_048_576)
            errors.Add("Restore:PostRestore:LogTargetSizeMB deve estar entre 1 e 1048576.");

        if (post.LogGrowthMB is < 0 or > 10_240)
            errors.Add("Restore:PostRestore:LogGrowthMB deve estar entre 0 e 10240.");

        if (post.CommandTimeoutMinutes is < 1 or > 1440)
            errors.Add("Restore:PostRestore:CommandTimeoutMinutes deve estar entre 1 e 1440.");

        if (!string.IsNullOrWhiteSpace(post.ScriptsPath) && !Path.IsPathFullyQualified(post.ScriptsPath))
            errors.Add("Restore:PostRestore:ScriptsPath deve ser um caminho absoluto.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateServerPath(string? path, string key, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!WindowsAbsolutePathRegex().IsMatch(path))
            errors.Add($"{key} deve ser um caminho absoluto Windows (ex.: D:\\Pasta ou \\\\servidor\\share).");
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_\-\. \*\?]{1,128}$")]
    private static partial Regex BlockedPatternRegex();

    [GeneratedRegex(@"^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)")]
    private static partial Regex WindowsAbsolutePathRegex();
}
