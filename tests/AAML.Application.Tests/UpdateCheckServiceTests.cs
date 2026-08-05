using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Application.Updates;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class UpdateCheckServiceTests
{
    [TestMethod] public async Task NewerStableRelease_IsAvailable()
    {
        var service = new UpdateCheckService(new ReleaseSource(Release("v2.0.0")), new VersionSource("1.9.0"));
        var result = await service.CheckAsync(UpdateChannelPreference.Stable, CancellationToken.None);
        result.Value!.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
    }
    [TestMethod] public async Task PrereleaseChannel_ExcludesAlphaButAlphaChannelIncludesIt()
    {
        var release = new ReleaseSource(Release("2.0.0-alpha.1", true)); var service = new UpdateCheckService(release, new VersionSource("1.0.0"));
        (await service.CheckAsync(UpdateChannelPreference.Prerelease, CancellationToken.None)).Value!.Status.Should().Be(UpdateCheckStatus.NoEligibleRelease);
        (await service.CheckAsync(UpdateChannelPreference.Alpha, CancellationToken.None)).Value!.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
    }
    [TestMethod] public async Task SemanticPrereleaseIdentifiers_AreComparedNumerically()
    {
        var service = new UpdateCheckService(new ReleaseSource(Release("v2.0.0-beta.10", true)), new VersionSource("2.0.0-beta.2+local"));
        (await service.CheckAsync(UpdateChannelPreference.Prerelease, CancellationToken.None)).Value!.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
    }
    private static ReleaseInfo Release(string tag, bool prerelease = false) => new(tag, tag, false, prerelease, "notes", new Uri("https://github.com/JakeRoxs/xcom2-aaml/releases/tag/test"), DateTimeOffset.UtcNow, []);
    private sealed class ReleaseSource(ReleaseInfo release) : IReleaseService { public Task<Result<ReleaseInfo?>> GetLatestAsync(ReleaseChannel channel, CancellationToken cancellationToken) => Task.FromResult(Result<ReleaseInfo?>.Success(release)); }
    private sealed class VersionSource(string version) : IApplicationVersionProvider { public Result<string> GetCurrentVersion() => Result<string>.Success(version); }
}
