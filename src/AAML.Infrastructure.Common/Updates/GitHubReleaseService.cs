using System.Net;
using AAML.Application.Common;
using AAML.Application.Ports;
using Newtonsoft.Json;
using AAML.Application;

namespace AAML.Infrastructure.Common.Updates;

public sealed class GitHubReleaseService(HttpClient client) : IReleaseService
{

    public async Task<Result<ReleaseInfo?>> GetLatestAsync(ReleaseChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProjectIdentity.ReleasesApiUri);
            request.Headers.UserAgent.ParseAdd("AAML/1.0"); request.Headers.Accept.ParseAdd("application/vnd.github+json"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Contains("X-RateLimit-Remaining")) return Failure("release.rate_limited", "GitHub's public API rate limit was reached.", ErrorKind.ExternalService);
            if (!response.IsSuccessStatusCode) return Failure("release.http_failed", $"GitHub returned HTTP {(int)response.StatusCode}.", ErrorKind.ExternalService);
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var documents = JsonConvert.DeserializeObject<ReleaseDocument[]>(json) ?? [];
            var candidates = documents.Where(document => !document.Draft && !string.IsNullOrWhiteSpace(document.TagName) && (channel == ReleaseChannel.IncludePrerelease || !document.Prerelease)).Select(Map).Where(result => result is not null).ToArray();
            return Result<ReleaseInfo?>.Success(candidates.OrderByDescending(release => release!.PublishedAt).FirstOrDefault());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("release.timeout", "The update check timed out.", ErrorKind.ExternalService); }
        catch (OperationCanceledException) { return Failure("release.cancelled", "The update check was cancelled.", ErrorKind.Cancelled); }
        catch (HttpRequestException) { return Failure("release.network_failed", "GitHub could not be reached.", ErrorKind.ExternalService); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or UriFormatException) { return Failure("release.invalid_response", "GitHub returned invalid release information.", ErrorKind.InvalidData); }
    }

    private static ReleaseInfo? Map(ReleaseDocument document)
    {
        if (!Uri.TryCreate(document.HtmlUrl, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps || !page.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        return new(document.TagName!, document.Name ?? document.TagName!, document.Draft, document.Prerelease, document.Body ?? string.Empty, page, document.PublishedAt,
            (document.Assets ?? []).Where(asset => asset.Size >= 0 && Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out _)).Select(asset => new ReleaseAsset(asset.Name ?? string.Empty, asset.Size, asset.DownloadCount, new Uri(asset.BrowserDownloadUrl!))).ToArray());
    }
    private static Result<ReleaseInfo?> Failure(string code, string message, ErrorKind kind) => Result<ReleaseInfo?>.Failure(new Error(code, message, kind));
    private sealed record ReleaseDocument([property: JsonProperty("tag_name")] string? TagName, [property: JsonProperty("name")] string? Name, [property: JsonProperty("html_url")] string? HtmlUrl, [property: JsonProperty("draft")] bool Draft, [property: JsonProperty("prerelease")] bool Prerelease, [property: JsonProperty("body")] string? Body, [property: JsonProperty("published_at")] DateTimeOffset? PublishedAt, [property: JsonProperty("assets")] ReleaseAssetDocument[]? Assets);
    private sealed record ReleaseAssetDocument([property: JsonProperty("name")] string? Name, [property: JsonProperty("size")] long Size, [property: JsonProperty("download_count")] long DownloadCount, [property: JsonProperty("browser_download_url")] string? BrowserDownloadUrl);
}
