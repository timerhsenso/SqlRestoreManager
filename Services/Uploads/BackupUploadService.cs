using System.Buffers;
using System.Diagnostics;
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

    private readonly RestoreOptions _options;
    private readonly BackupExtractor _extractor;
    private readonly ILogger<BackupUploadService> _logger;

    public BackupUploadService(
        IOptions<RestoreOptions> options, BackupExtractor extractor, ILogger<BackupUploadService> logger)
    {
        _options = options.Value;
        _extractor = extractor;
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

                if (!_extractor.IsAllowed(originalName))
                    throw new RestoreValidationException(
                        $"Extensão não suportada. Permitidas: {string.Join(", ", _extractor.AllowedExtensions)}.");

                var path = Path.Combine(folder, "upload" + extension);
                _logger.LogInformation("Job {JobId}: recebendo '{File}' em {Path}.", jobId, originalName, path);

                var stopwatch = Stopwatch.StartNew();
                var size = await CopyWithLimitAsync(section.Body, path, _options.MaxUploadBytes, jobId, ct);
                stopwatch.Stop();

                _logger.LogInformation(
                    "Job {JobId}: arquivo gravado ({SizeMB:N1} MB em {Seconds:N1}s, {Rate:N1} MB/s).",
                    jobId, size / 1024d / 1024d, stopwatch.Elapsed.TotalSeconds,
                    size / 1024d / 1024d / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001));

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

    private async Task<long> CopyWithLimitAsync(
        Stream source, string path, long maxBytes, Guid jobId, CancellationToken ct)
    {
        const long logEveryBytes = 100L * 1024 * 1024;
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
            long nextLog = logEveryBytes;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new RestoreValidationException(
                        $"O arquivo excede o limite de {maxBytes / 1024 / 1024} MB.",
                        StatusCodes.Status413PayloadTooLarge);

                await target.WriteAsync(buffer.AsMemory(0, read), ct);

                if (total >= nextLog)
                {
                    _logger.LogInformation("Job {JobId}: {SizeMB:N0} MB gravados...", jobId, total / 1024d / 1024d);
                    nextLog += logEveryBytes;
                }
            }

            _logger.LogDebug("Job {JobId}: finalizando gravação em disco...", jobId);
            await target.FlushAsync(ct);

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
