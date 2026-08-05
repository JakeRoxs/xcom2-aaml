using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Mods;

namespace AAML.Infrastructure.Common.Steam;

/// <summary>Caches bounded Workshop preview image downloads in application-owned cache storage.</summary>
public sealed class WorkshopPreviewCache(IApplicationPaths paths, HttpClient httpClient) : IWorkshopPreviewCache
{
    private const long MaximumBytes = 5L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif", ["image/webp"] = ".webp"
    };

    public async Task<Result<string?>> GetAsync(WorkshopId workshopId, string? previewUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(previewUrl)) return Result<string?>.Success(null);
        if (!Uri.TryCreate(previewUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Failure("workshop.preview_url_invalid", "Workshop preview URLs must use absolute HTTPS URLs.", ErrorKind.Validation);

        var directory = Path.Combine(paths.CacheDirectory, "WorkshopPreviews");
        var key = $"{workshopId.Value}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))}";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(directory))
            {
                var cached = Extensions.Values.Select(extension => Path.Combine(directory, key + extension)).FirstOrDefault(File.Exists);
                if (cached is not null) return Result<string?>.Success(cached);
            }

            Directory.CreateDirectory(directory);
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Failure("workshop.preview_http_failed", $"Workshop preview returned HTTP {(int)response.StatusCode}.", ErrorKind.ExternalService);
            if (!TryExtension(response.Content.Headers.ContentType, out var extension)) return Failure("workshop.preview_content_type_invalid", "Workshop preview content type is not a supported image format.", ErrorKind.InvalidData);
            if (response.Content.Headers.ContentLength is > MaximumBytes) return TooLarge();

            var finalPath = Path.Combine(directory, key + extension);
            var temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    long total = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        total += read;
                        if (total > MaximumBytes) return TooLarge();
                        await output.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    }
                    await output.FlushAsync(linked.Token).ConfigureAwait(false);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                await output.DisposeAsync().ConfigureAwait(false);
                File.Move(temporaryPath, finalPath, true);
                return Result<string?>.Success(finalPath);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Failure("workshop.preview_cancelled", "Workshop preview download was cancelled.", ErrorKind.Cancelled); }
        catch (OperationCanceledException) { return Failure("workshop.preview_timeout", "Workshop preview download timed out.", ErrorKind.Timeout); }
        catch (HttpRequestException exception) { return Failure("workshop.preview_network_failed", exception.Message, ErrorKind.Network); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Failure("workshop.preview_cache_failed", exception.Message, ErrorKind.Io); }
    }

    private static bool TryExtension(MediaTypeHeaderValue? contentType, out string extension) => Extensions.TryGetValue(contentType?.MediaType ?? string.Empty, out extension!);
    private static Result<string?> TooLarge() => Failure("workshop.preview_too_large", "Workshop preview exceeds the 5 MiB limit.", ErrorKind.InvalidData);
    private static Result<string?> Failure(string code, string message, ErrorKind kind) => Result<string?>.Failure(new Error(code, message, kind));
}
