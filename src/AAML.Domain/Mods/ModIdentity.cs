namespace AAML.Domain.Mods;

/// <summary>An internal package identifier that may be shared by duplicate installations.</summary>
public readonly record struct PackageId
{
    public PackageId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A Steam Workshop published-file identifier.</summary>
public readonly record struct WorkshopId(ulong Value);

/// <summary>A category identity independent of mod ownership.</summary>
public readonly record struct CategoryId
{
    public CategoryId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
}

/// <summary>A tag identity independent of presentation color.</summary>
public readonly record struct TagId
{
    public TagId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
}
