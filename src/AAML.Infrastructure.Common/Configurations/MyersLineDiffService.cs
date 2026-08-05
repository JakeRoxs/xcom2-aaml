using AAML.Application.Common;
using AAML.Application.Configurations;

namespace AAML.Infrastructure.Common.Configurations;

/// <summary>Bounded Myers shortest-edit-script diff over normalized logical lines.</summary>
public sealed class MyersLineDiffService : ILineDiffService
{
    public Task<Result<AlignedLineDiff>> CompareAsync(string left, string right, LineDiffLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(limits);
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(Cancelled());
        return Task.Run(() => Compare(left, right, limits, cancellationToken), cancellationToken);
    }

    private static Result<AlignedLineDiff> Compare(string leftText, string rightText, LineDiffLimits limits, CancellationToken cancellationToken)
    {
        try
        {
            if (leftText.Length > limits.MaxCharactersPerSide || rightText.Length > limits.MaxCharactersPerSide)
                return Failure("configuration.diff_too_large", "A document exceeds the character limit.", ErrorKind.InvalidData);

            var left = Tokenize(leftText);
            var right = Tokenize(rightText);
            if (left.Length > limits.MaxLinesPerSide || right.Length > limits.MaxLinesPerSide)
                return Failure("configuration.diff_too_large", "A document exceeds the line limit.", ErrorKind.InvalidData);

            var n = left.Length;
            var m = right.Length;
            var max = n + m;
            var allowedDistance = Math.Min(max, limits.MaxEditDistance);
            var offset = max + 1;
            var v = new int[2 * max + 3];
            v[offset + 1] = 0;
            var trace = new List<int[]>(Math.Min(allowedDistance + 1, 256));
            long work = 0;
            var foundDistance = -1;

            for (var distance = 0; distance <= allowedDistance; distance++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
                {
                    if (++work > limits.MaxWorkUnits)
                        return Failure("configuration.diff_budget_exceeded", "The diff work budget was exceeded.", ErrorKind.Unavailable);

                    var index = offset + diagonal;
                    var x = diagonal == -distance || diagonal != distance && v[index - 1] < v[index + 1]
                        ? v[index + 1]
                        : v[index - 1] + 1;
                    var y = x - diagonal;
                    while (x < n && y < m && string.Equals(left[x], right[y], StringComparison.Ordinal))
                    {
                        x++;
                        y++;
                        if ((++work & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        if (work > limits.MaxWorkUnits)
                            return Failure("configuration.diff_budget_exceeded", "The diff work budget was exceeded.", ErrorKind.Unavailable);
                    }

                    v[index] = x;
                    if (x >= n && y >= m)
                    {
                        foundDistance = distance;
                        break;
                    }
                }

                trace.Add((int[])v.Clone());
                if (foundDistance >= 0) break;
            }

            if (foundDistance < 0)
                return Failure("configuration.diff_too_large", "The edit distance exceeds the configured limit.", ErrorKind.InvalidData);

            var operations = Backtrack(left, right, trace, foundDistance, offset, cancellationToken);
            return Result<AlignedLineDiff>.Success(Materialize(operations, left, right));
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
    }

    private static List<Operation> Backtrack(string[] left, string[] right, IReadOnlyList<int[]> trace, int distance, int offset, CancellationToken cancellationToken)
    {
        var operations = new List<Operation>(left.Length + right.Length);
        var x = left.Length;
        var y = right.Length;
        for (var d = distance; d > 0; d--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = trace[d - 1];
            var diagonal = x - y;
            var previousDiagonal = diagonal == -d || diagonal != d && previous[offset + diagonal - 1] < previous[offset + diagonal + 1]
                ? diagonal + 1
                : diagonal - 1;
            var previousX = previous[offset + previousDiagonal];
            var previousY = previousX - previousDiagonal;
            while (x > previousX && y > previousY)
            {
                operations.Add(new Operation(DiffRowKind.Unchanged, --x, --y));
            }

            if (x == previousX) operations.Add(new Operation(DiffRowKind.Inserted, null, --y));
            else operations.Add(new Operation(DiffRowKind.Deleted, --x, null));
        }

        while (x > 0 && y > 0) operations.Add(new Operation(DiffRowKind.Unchanged, --x, --y));
        while (x > 0) operations.Add(new Operation(DiffRowKind.Deleted, --x, null));
        while (y > 0) operations.Add(new Operation(DiffRowKind.Inserted, null, --y));
        operations.Reverse();
        return operations;
    }

    private static AlignedLineDiff Materialize(IReadOnlyList<Operation> operations, string[] leftLines, string[] rightLines)
    {
        var rows = new AlignedDiffRow[operations.Count];
        var unchanged = 0;
        var deleted = 0;
        var inserted = 0;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Kind == DiffRowKind.Unchanged) unchanged++;
            else if (operation.Kind == DiffRowKind.Deleted) deleted++;
            else inserted++;
            rows[index] = new AlignedDiffRow(
                index + 1,
                operation.LeftIndex is { } left ? left + 1 : null,
                operation.LeftIndex is { } leftText ? leftLines[leftText] : null,
                operation.RightIndex is { } right ? right + 1 : null,
                operation.RightIndex is { } rightText ? rightLines[rightText] : null,
                operation.Kind);
        }

        return new AlignedLineDiff(rows, unchanged, deleted, inserted);
    }

    private static string[] Tokenize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static Result<AlignedLineDiff> Failure(string code, string message, ErrorKind kind) => Result<AlignedLineDiff>.Failure(new Error(code, message, kind));
    private static Result<AlignedLineDiff> Cancelled() => Failure("configuration.diff_cancelled", "The diff was cancelled.", ErrorKind.Cancelled);

    private sealed record Operation(DiffRowKind Kind, int? LeftIndex, int? RightIndex);
}
