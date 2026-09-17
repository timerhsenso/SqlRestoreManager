using System.Buffers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Services.Sql;

namespace SqlRestoreManager.Services.Uploads;

public sealed record UploadedBackup(
    Guid JobId,
    string JobFolder,
    string FilePath,
    string OriginalFileName,
    long SizeBytes);

/// <summary>
/// Recebe o multipart em streaming direto para TempPath
/// (sem IFormFile: evita a cópia intermediária no disco temporário do Windows).
/// </summary>
public sealed class BackupUploadService
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxFileNameLength = 260;
    private static readonly string[] AllowedExtensions = [".bak", ".zip"];

    private readonly RestoreOptions _options;
    private readonly ILogger<BackupUploadService> _logger;

    public BackupUploadService(IOptions<RestoreOptions> options, ILogger<BackupUploadService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string GetJobFolder(Guid jobId) => Path.Combine(_options.TempPath, jobId.ToString("N"));

    public async Task<UploadedBackup> ReceiveAsync(HttpRequest request, Guid jobId, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new RestoreValidationException("A requisição deve ser multipart/form-data.");

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            throw new RestoreValidationException("Boundary do multipart ausente.");

        var folder = GetJobFolder(jobId);
        Directory.CreateDirectory(folder);

        try
        {
            var reader = new MultipartReader(boundary, request.Body);
            UploadedBackup? result = null;

            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !disposition.IsFileDisposition())
                    continue;

                if (result is not null)
                    throw new RestoreValidationException("Envie apenas um arquivo por restore.");

                var rawName = StringSegment.IsNullOrEmpty(disposition.FileNameStar)
                    ? HeaderUtilities.RemoveQuotes(disposition.FileName).Value
                    : disposition.FileNameStar.Value;

                // Nome do cliente só é usado para exibição/validação da extensão, nunca como caminho.
                var originalName = Path.GetFileName(rawName ?? string.Empty);
                var extension = Path.GetExtension(originalName).ToLowerInvariant();

                if (!AllowedExtensions.Contains(extension))
                    throw new RestoreValidationException("Somente arquivos .bak ou .zip são permitidos.");

                var path = Path.Combine(folder, "upload" + extension);
                var size = await CopyWithLimitAsync(section.Body, path, _options.MaxUploadBytes, ct);

                if (size == 0)
                    throw new RestoreValidationException("O arquivo enviado está vazio.");

                if (originalName.Length > MaxFileNameLength)
                    originalName = originalName[..MaxFileNameLength];

                result = new UploadedBackup(jobId, folder, path, originalName, size);
            }

            return result ?? throw new RestoreValidationException("Selecione um arquivo .bak ou .zip.");
        }
        catch
        {
            TempFolder.TryDelete(folder, _logger);
            throw;
        }
    }

    private static async Task<long> CopyWithLimitAsync(Stream source, string path, long maxBytes, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var target = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 0,
                Options = FileOptions.Asynchronous
            });

            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new RestoreValidationException(
                        $"O arquivo excede o limite de {maxBytes / 1024 / 1024} MB.",
                        StatusCodes.Status413PayloadTooLarge);

                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
