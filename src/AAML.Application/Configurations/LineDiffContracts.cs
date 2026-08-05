using AAML.Application.Common;

namespace AAML.Application.Configurations;

public enum DiffRowKind { Unchanged, Deleted, Inserted }

public sealed record AlignedDiffRow(
    int DisplayLineNumber,
    int? LeftLineNumber,
    string? LeftText,
    int? RightLineNumber,
    string? RightText,
    DiffRowKind Kind);

public sealed record AlignedLineDiff(
    IReadOnlyList<AlignedDiffRow> Rows,
    int UnchangedCount,
    int DeletedCount,
    int InsertedCount);

public sealed record LineDiffLimits(int MaxCharactersPerSide, int MaxLinesPerSide, int MaxEditDistance, long MaxWorkUnits)
{
    public static LineDiffLimits Default { get; } = new(4_000_000, 20_000, 4_000, 50_000_000);
}

public interface ILineDiffService
{
    Task<Result<AlignedLineDiff>> CompareAsync(string left, string right, LineDiffLimits limits, CancellationToken cancellationToken);
}
