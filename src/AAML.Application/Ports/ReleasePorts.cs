using AAML.Application.Common;

namespace AAML.Application.Ports;

/// <summary>Release channel queried by the update workflow.</summary>
public enum ReleaseChannel
{
    Stable,
    IncludePrerelease
}

/// <summary>Application-owned release information independent of the provider API.</summary>
public sealed record ReleaseInfo(
    string TagName,
    string Name,
    bool IsDraft,
    bool IsPrerelease,
    string Notes,
    Uri PageUri,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>An application release download.</summary>
public sealed record ReleaseAsset(string Name, long Size, long DownloadCount, Uri DownloadUri);

/// <summary>Queries available application releases.</summary>
public interface IReleaseService
{
    Task<Result<ReleaseInfo?>> GetLatestAsync(ReleaseChannel channel, CancellationToken cancellationToken);
}

public interface IApplicationVersionProvider
{
    Result<string> GetCurrentVersion();
}
