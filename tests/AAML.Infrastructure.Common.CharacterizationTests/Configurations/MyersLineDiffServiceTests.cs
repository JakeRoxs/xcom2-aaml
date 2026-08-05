using System.Diagnostics;
using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Infrastructure.Common.Configurations;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Configurations;

[TestClass]
public sealed class MyersLineDiffServiceTests
{
    private readonly MyersLineDiffService service = new();

    [TestMethod]
    public async Task InsertDeleteAndUnchangedRows_AreAlignedWithPlaceholders()
    {
        var result = await service.CompareAsync("A\nB\nC", "A\nX\nC\nD", LineDiffLimits.Default, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Rows.Select(row => row.Kind).Should().Equal(
            DiffRowKind.Unchanged,
            DiffRowKind.Deleted,
            DiffRowKind.Inserted,
            DiffRowKind.Unchanged,
            DiffRowKind.Inserted);
        result.Value.Rows.Single(row => row.Kind == DiffRowKind.Deleted).RightText.Should().BeNull();
        result.Value.Rows.Where(row => row.Kind == DiffRowKind.Inserted).Should().OnlyContain(row => row.LeftText == null);
        result.Value.Rows.Select(row => row.DisplayLineNumber).Should().Equal(Enumerable.Range(1, result.Value.Rows.Count));
    }

    [TestMethod]
    public async Task CrLfAndLf_AreLogicallyEquivalentButTerminalNewlineIsObservable()
    {
        var equivalent = await service.CompareAsync("A\r\nB", "A\nB", LineDiffLimits.Default, TestContext.CancellationToken);
        var terminal = await service.CompareAsync("A", "A\n", LineDiffLimits.Default, TestContext.CancellationToken);

        equivalent.Value!.Rows.Should().OnlyContain(row => row.Kind == DiffRowKind.Unchanged);
        terminal.Value!.InsertedCount.Should().Be(1);
        terminal.Value.Rows.Last().RightText.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IdenticalDuplicateLines_AreDeterministic()
    {
        const string left = "Key=A\n\nKey=A\nKey=B";
        const string right = "Key=A\nKey=A\n\nKey=B";

        var first = await service.CompareAsync(left, right, LineDiffLimits.Default, TestContext.CancellationToken);
        var second = await service.CompareAsync(left, right, LineDiffLimits.Default, TestContext.CancellationToken);

        second.Value.Should().BeEquivalentTo(first.Value, options => options.WithStrictOrdering());
    }

    [TestMethod]
    public async Task LimitsAndCancellation_ReturnStructuredFailures()
    {
        var limited = await service.CompareAsync("A\nB", "A\nB", new LineDiffLimits(2, 20, 20, 100), TestContext.CancellationToken);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var cancelled = await service.CompareAsync("A", "B", LineDiffLimits.Default, source.Token);

        limited.Error!.Code.Should().Be("configuration.diff_too_large");
        cancelled.Error!.Kind.Should().Be(ErrorKind.Cancelled);
    }

    [TestMethod]
    public async Task TenThousandLineSparseDiff_CompletesWithinBoundedThreshold()
    {
        var left = Enumerable.Range(0, 10_000).Select(index => $"Key{index:D5}=Value{index:D5}").ToArray();
        var right = left.ToArray();
        for (var index = 100; index < 10_000; index += 500) right[index] += "-changed";
        var timer = Stopwatch.StartNew();

        var result = await service.CompareAsync(string.Join('\n', left), string.Join('\n', right), LineDiffLimits.Default, TestContext.CancellationToken);
        timer.Stop();

        result.IsSuccess.Should().BeTrue();
        result.Value!.DeletedCount.Should().Be(20);
        result.Value.InsertedCount.Should().Be(20);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    public TestContext TestContext { get; set; }
}
