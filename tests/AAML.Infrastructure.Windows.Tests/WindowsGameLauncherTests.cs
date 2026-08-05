using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Windows.Launching;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsGameLauncherTests
{
    [TestMethod]
    [DataRow(GameVariant.XCom2, "")]
    [DataRow(GameVariant.XCom2WarOfTheChosen, "XCom2-WarOfTheChosen")]
    [DataRow(GameVariant.ChimeraSquad, "")]
    public async Task KnownVariant_UsesFixedContainedExecutableAndStructuredArguments(GameVariant variant, string relativeRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Game Ω", Guid.NewGuid().ToString("N"));
        var working = Path.Combine(root, relativeRoot);
        var executable = Path.Combine(working, "Binaries", "Win64", variant == GameVariant.ChimeraSquad ? "xcom.exe" : "XCom2.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllBytesAsync(executable, [], TestContext.CancellationToken);
            var process = new RecordingProcessRunner();
            var launcher = new WindowsGameLauncher(process);
            var request = new GameLaunchRequest(variant, root, [], [], [new LaunchArgument("-Name=Mixed Case"), new LaunchArgument("&;|$() Ω")]);

            var result = await launcher.LaunchAsync(request, TestContext.CancellationToken);

            result.Value!.ProcessId.Should().Be(123);
            result.Value.ExecutablePath.Should().Be(Path.GetFullPath(executable));
            process.Last!.WorkingDirectory.Should().Be(Path.GetFullPath(working));
            process.Last.Arguments.Should().Equal("-Name=Mixed Case", "&;|$() Ω");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MissingExecutable_ReturnsNotFoundWithoutStartingProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var process = new RecordingProcessRunner();

            var result = await new WindowsGameLauncher(process).LaunchAsync(new GameLaunchRequest(GameVariant.XCom2, root, [], [], []), TestContext.CancellationToken);

            result.Error!.Code.Should().Be("launch.executable_missing");
            process.Last.Should().BeNull();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProcessFailure_IsReturnedWithoutException()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "Binaries", "Win64", "XCom2.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllBytesAsync(executable, [], TestContext.CancellationToken);
            var process = new RecordingProcessRunner { Failure = new Error("process.start_failed", "Denied.", ErrorKind.ExternalService) };

            var result = await new WindowsGameLauncher(process).LaunchAsync(new GameLaunchRequest(GameVariant.XCom2, root, [], [], []), TestContext.CancellationToken);

            result.Error!.Code.Should().Be("process.start_failed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancelledRequest_DoesNotInspectOrStartInstallation()
    {
        var process = new RecordingProcessRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new WindowsGameLauncher(process).LaunchAsync(new GameLaunchRequest(GameVariant.XCom2, "Z:\\Missing", [], [], []), cancellation.Token);

        result.Error!.Code.Should().Be("launch.cancelled");
        process.Last.Should().BeNull();
    }

    public TestContext TestContext { get; set; }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public ProcessLaunchRequest? Last { get; private set; }
        public Error? Failure { get; init; }
        public Task<Result<ProcessStartResult>> StartAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(Failure is null
                ? Result<ProcessStartResult>.Success(new ProcessStartResult(123))
                : Result<ProcessStartResult>.Failure(Failure));
        }
    }
}
