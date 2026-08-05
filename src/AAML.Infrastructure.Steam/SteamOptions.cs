namespace AAML.Infrastructure.Steam;

/// <summary>Steam adapter timing and query limits.</summary>
public sealed record SteamOptions(TimeSpan CallbackInterval, TimeSpan QueryTimeout, int QueryBatchSize = 50, uint AppId = 268500)
{
    public static SteamOptions Default { get; } = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(15));
}
