using AAML.Application.Common;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ResultTests
{
    [TestMethod]
    public void Failure_PreservesStructuredError()
    {
        var error = new Error("settings.invalid", "Synthetic invalid settings.", ErrorKind.InvalidData);

        var result = Result.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
    }

    [TestMethod]
    public void GenericSuccess_PreservesValueWithoutError()
    {
        var result = Result<string>.Success("value");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("value");
        result.Error.Should().BeNull();
    }
}
