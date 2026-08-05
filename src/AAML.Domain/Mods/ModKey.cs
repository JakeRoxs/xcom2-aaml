namespace AAML.Domain.Mods;

/// <summary>Identifies one physical mod installation independently of its package ID.</summary>
public readonly record struct ModKey
{
    /// <summary>Creates a key from an adapter-normalized location identity.</summary>
    public ModKey(ModSource source, string locationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationIdentity);
        Source = source;
        LocationIdentity = locationIdentity;
    }

    /// <summary>Gets the mod source.</summary>
    public ModSource Source { get; }

    /// <summary>Gets the normalized physical-location identity.</summary>
    public string LocationIdentity { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Source}:{LocationIdentity}";
}

/// <summary>Describes where a mod installation originated.</summary>
public enum ModSource
{
    Unknown,
    Manual,
    SteamWorkshop
}
