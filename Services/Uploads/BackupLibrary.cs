using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Services.Sql;

namespace SqlRestoreManager.Services.Uploads;

public sealed record LibraryFile(string Name, long SizeBytes, DateTime ModifiedAt)
{
    public double SizeMB => SizeBytes / 1024d / 1024d;
}

/// <summary>
/// Pasta do servidor onde os backups são copiados manualmente
/// (alternativa ao upload, sem limite de tamanho).
/// </summary>
public sealed class BackupLibrary
{
    private readonly RestoreOptions _options;
    private readonly BackupExtractor _extractor;

    public BackupLibrary(IOptions<RestoreOptions> options, BackupExtractor extractor)
    {
        _options = options.Value;
        _extractor = extractor;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.LibraryPath);

    public IReadOnlyList<LibraryFile> List()
    {
        if (!Enabled)
            return [];

        if (!Directory.Exists(_options.LibraryPath))
            throw new RestoreValidationException(
                $"A pasta de backups não foi encontrada: {_options.LibraryPath}");

        return new DirectoryInfo(_options.LibraryPath!)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => _extractor.IsAllowed(f.Name))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new LibraryFile(f.Name, f.Length, f.LastWriteTime))
            .ToList();
    }

    /// <summary>Valida o nome informado e devolve o arquivo real da pasta.</summary>
    public FileInfo Resolve(string? fileName)
    {
        if (!Enabled)
            throw new RestoreValidationException("A pasta de backups não está configurada (Restore:LibraryPath).");

        if (string.IsNullOrWhiteSpace(fileName))
            throw new RestoreValidationException("Selecione um arquivo da pasta.");

        // Só nome simples: nada de subpastas, ".." ou caminho absoluto.
        var name = fileName.Trim();
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new RestoreValidationException("Nome de arquivo inválido.");

        if (!_extractor.IsAllowed(name))
            throw new RestoreValidationException(
                $"Extensão não suportada. Permitidas: {string.Join(", ", _extractor.AllowedExtensions)}.");

        var file = new FileInfo(Path.Combine(_options.LibraryPath!, name));
        if (!file.Exists)
            throw new RestoreValidationException($"Arquivo não encontrado na pasta: {name}",
                StatusCodes.Status404NotFound);

        return file;
    }
}
