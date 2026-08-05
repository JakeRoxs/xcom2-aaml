using System.Text;
using AAML.Application.Logging;
using AAML.Infrastructure.Common.Logging;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Logging;

[TestClass]
public sealed class RollingLocalFileLogTests
{
    [TestMethod]
    public async Task Flush_WritesStructuredLocalDiagnosticsWithoutBom()
    {
        var directory = CreateDirectory();
        try
        {
            var log = new RollingLocalFileLog(new RollingLocalFileLogOptions(directory, 10_000, 2));
            log.Write(LocalLogLevel.Information, "application.started", "AAML started.", new Dictionary<string, string> { ["platform"] = "fixture" });

            var result = await log.FlushAsync(TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            await log.DisposeAsync();
            var path = Path.Combine(directory, "aaml.log");
            var bytes = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
            Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 3))).Should().NotBe(Convert.ToHexString(Encoding.UTF8.GetPreamble()));
            var text = Encoding.UTF8.GetString(bytes);
            text.Should().Contain("application.started").And.Contain("AAML started.").And.Contain("platform");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Rotation_RetainsConfiguredGenerationsAndShutdownFlushes()
    {
        var directory = CreateDirectory();
        try
        {
            var log = new RollingLocalFileLog(new RollingLocalFileLogOptions(directory, 300, 2));
            for (var index = 0; index < 20; index++) log.Write(LocalLogLevel.Warning, $"event.{index:D2}", new string('x', 100));
            await log.DisposeAsync();

            Directory.EnumerateFiles(directory, "aaml.log*").Should().HaveCount(3);
            File.Exists(Path.Combine(directory, "aaml.log.3")).Should().BeFalse();
            File.ReadAllText(Path.Combine(directory, "aaml.log")).Should().Contain("event.19");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CancelledFlush_ReturnsStructuredCancellation()
    {
        var directory = CreateDirectory();
        try
        {
            await using var log = new RollingLocalFileLog(RollingLocalFileLogOptions.Create(directory));
            using var source = new CancellationTokenSource();
            await source.CancelAsync();

            var result = await log.FlushAsync(source.Token);

            result.Error!.Code.Should().Be("log.flush_cancelled");
        }
        finally { Directory.Delete(directory, true); }
    }

    public TestContext TestContext { get; set; }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AAML.LogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
