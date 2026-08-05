namespace AAML.Application.Common;

/// <summary>Outcome of one item in a batch operation.</summary>
public sealed record ItemOutcome<TKey>(TKey Item, Result Outcome);

/// <summary>Preserves ordered per-item success and failure outcomes.</summary>
public sealed record BatchResult<TKey>(IReadOnlyList<ItemOutcome<TKey>> Items)
{
    public bool IsSuccess => Items.All(item => item.Outcome.IsSuccess);
    public bool IsPartialSuccess => Items.Any(item => item.Outcome.IsSuccess) && Items.Any(item => !item.Outcome.IsSuccess);
}
