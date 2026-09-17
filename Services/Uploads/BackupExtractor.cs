using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Services.Sql;

namespace SqlRestoreManager.Services.Uploads;

/// <summary>
/// Entrega o caminho de um .bak a partir do arquivo enviado/escolhido.
/// .bak e .zip são tratados pelo .NET; .rar e .7z exigem o 7-Zip no servidor.
/// </summary>
public sealed class BackupExtractor
{
    private static readonly string[] NativeExtensions = [".bak", ".zip"];
    private static readonly string[] SevenZipExtensions = [".rar", ".7z"];

    private readonly RestoreOptions _options;
    private readonly ILogger<BackupExtractor> _logger;

    public BackupExtractor(IOptions<RestoreOptions> options, ILogger<BackupExtractor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool SevenZipAvailable =>
        !string.IsNullOrWhiteSpace(_options.SevenZipPath) && File.Exists(_options.SevenZipPath);

    public IReadOnlyList<string> AllowedExtensions =>
        SevenZipAvailable ? [.. NativeExtensions, .. SevenZipExtensions] : NativeExtensions;

    public bool IsAllowed(string fileName) =>
        AllowedExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Extensões conhecidas, mesmo sem 7-Zip (para mensagem de erro melhor).</summary>
    public static bool IsArchiveExtension(string fileName) =>
        SevenZipExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// Retorna o caminho local do .bak. Compactados são extraídos em <paramref name="workFolder"/>.
    /// </summary>
    public async Task<string> PrepareAsync(string sourcePath, string workFolder, CancellationToken ct)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();

        if (extension == ".bak")
            return sourcePath;

        if (!IsAllowed(sourcePath))
            throw new RestoreValidationException(IsArchiveExtension(sourcePath)
                ? $"Arquivos {extension} exigem o 7-Zip no servidor (Restore:SevenZipPath)."
                : $"Extensão não suportada: {extension}.");

        var extractFolder = Path.Combine(workFolder, "extract");
        Directory.CreateDirectory(extractFolder);

        var bak = extension == ".zip"
            ? await Task.Run(() => ExtractZip(sourcePath, extractFolder), ct)
            : await ExtractWithSevenZipAsync(sourcePath, extractFolder, ct);

        var size = new FileInfo(bak).Length;
        if (size > _options.MaxExtractedBytes)
        {
            File.Delete(bak);
            throw new RestoreValidationException(
                $"O .bak extraído tem {size / 1024 / 1024} MB e excede o limite de {_options.MaxExtractedGB} GB.");
        }

        _logger.LogInformation("Extraído {Bak} ({SizeMB:N0} MB) de {Source}.", bak, size / 1024d / 1024d, sourcePath);
        return bak;
    }

    private string ExtractZip(string sourcePath, string extractFolder)
    {
        // Nome de saída fixo: nada vindo do arquivo vira caminho (proteção contra Zip Slip).
        var output = Path.Combine(extractFolder, "backup.bak");

        using var archive = ZipFile.OpenRead(sourcePath);
        var entries = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) &&
                        string.Equals(Path.GetExtension(e.Name), ".bak", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entries.Count != 1)
            throw new RestoreValidationException(
                $"O arquivo compactado deve conter exatamente um .bak (encontrados: {entries.Count}).");

        if (entries[0].Length > _options.MaxExtractedBytes)
            throw new RestoreValidationException(
                $"O .bak descompactado excede o limite de {_options.MaxExtractedGB} GB.");

        entries[0].ExtractToFile(output, overwrite: true);
        return output;
    }

    private async Task<string> ExtractWithSevenZipAsync(string sourcePath, string extractFolder, CancellationToken ct)
    {
        // "e" ignora a estrutura de pastas do arquivo e extrai só os .bak.
        var arguments = $"e -y -bso0 -bsp0 -o\"{extractFolder}\" \"{sourcePath}\" *.bak -r";
        var info = new ProcessStartInfo(_options.SevenZipPath!, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Não foi possível executar {_options.SevenZipPath}.");

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new RestoreValidationException(
                $"Falha ao extrair o arquivo (7-Zip código {process.ExitCode}). {stderr.Trim()}");

        var files = Directory.GetFiles(extractFolder, "*.bak", SearchOption.TopDirectoryOnly);
        if (files.Length != 1)
            throw new RestoreValidationException(
                $"O arquivo compactado deve conter exatamente um .bak (encontrados: {files.Length}).");

        return files[0];
    }
}
