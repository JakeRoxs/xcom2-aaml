namespace AAML.Infrastructure.Steam.Internal;

internal static class SteamValueConversions
{
    public static DateTimeOffset FromUnixTimestamp(uint seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    public static double? DownloadFraction(ulong bytesDownloaded, ulong bytesTotal) =>
        bytesTotal == 0 ? null : Math.Clamp((double)bytesDownloaded / bytesTotal, 0d, 1d);
}
