using Newtonsoft.Json;

namespace AAML.Infrastructure.Common.Compatibility.GitHub;

/// <summary>A GitHub release shape consumed by the legacy updater.</summary>
public sealed record LegacyGitHubRelease(
    [property: JsonProperty("tag_name")] string TagName,
    [property: JsonProperty("name")] string Name,
    [property: JsonProperty("draft")] bool Draft,
    [property: JsonProperty("prerelease")] bool Prerelease,
    [property: JsonProperty("body")] string Body,
    [property: JsonProperty("assets")] IReadOnlyList<LegacyGitHubAsset> Assets);

/// <summary>A GitHub release asset consumed by the legacy updater.</summary>
public sealed record LegacyGitHubAsset(
    [property: JsonProperty("name")] string Name,
    [property: JsonProperty("size")] long Size,
    [property: JsonProperty("download_count")] long DownloadCount,
    [property: JsonProperty("browser_download_url")] Uri DownloadUrl);

/// <summary>Maps GitHub release JSON using the legacy selection policy.</summary>
public static class LegacyGitHubReleaseCodec
{
    /// <summary>Deserializes one release response.</summary>
    public static LegacyGitHubRelease Parse(string json) =>
        JsonConvert.DeserializeObject<LegacyGitHubRelease>(json)
        ?? throw new JsonSerializationException("The release response was null.");

    /// <summary>Selects the first release without sorting, as the legacy prerelease path did.</summary>
    public static LegacyGitHubRelease? ParseFirst(string json) =>
        JsonConvert.DeserializeObject<List<LegacyGitHubRelease>>(json)?.FirstOrDefault();
}
