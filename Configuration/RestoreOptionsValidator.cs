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
