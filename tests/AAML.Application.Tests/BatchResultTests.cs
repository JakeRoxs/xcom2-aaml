using AAML.Application.Common;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class BatchResultTests
{
    [TestMethod]
    public void PartialBatch_PreservesEveryItemOutcomeInOrder()
    {
        var failure = new Error("item.failed", "Synthetic failure.", ErrorKind.Io);
        var result = new BatchResult<string>(
        [
            new ItemOutcome<string>("first", Result.Success()),
            new ItemOutcome<string>("second", Result.Failure(failure))
        ]);

        result.IsSuccess.Should().BeFalse();
        result.IsPartialSuccess.Should().BeTrue();
        result.Items.Select(item => item.Item).Should().Equal("first", "second");
    }
}
