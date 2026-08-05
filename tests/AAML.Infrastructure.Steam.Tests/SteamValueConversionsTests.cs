using AAML.Infrastructure.Steam.Internal;
using FluentAssertions;

namespace AAML.Infrastructure.Steam.Tests;

[TestClass]
public sealed class SteamValueConversionsTests
{
    [TestMethod]
    public void UnixTimestamp_IsConvertedFromSeconds()
    {
        SteamValueConversions.FromUnixTimestamp(1_700_000_000).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    }

    [TestMethod]
    [DataRow(0UL, 0UL, null)]
    [DataRow(0UL, 100UL, 0d)]
    [DataRow(1UL, 2UL, 0.5d)]
    [DataRow(101UL, 100UL, 1d)]
    public void Progress_UsesFloatingPointAndBoundsResult(ulong downloaded, ulong total, double? expected)
    {
        SteamValueConversions.DownloadFraction(downloaded, total).Should().Be(expected);
    }
}
