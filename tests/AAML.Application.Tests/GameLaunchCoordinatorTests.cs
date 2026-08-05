using AAML.Application.Common;
using AAML.Application.Launching;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class GameLaunchCoordinatorTests
{
    [TestMethod]
    public async Task ConfigurationSucceeds_LaunchesAndReturnsBothReceipts()
    {
        var configuration = new RecordingConfigurationWriter();
        var launcher = new RecordingLauncher();
        var coordinator = new GameLaunchCoordinator(configuration, launcher);

        var result = await coordinator.LaunchAsync(Request(), TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Configuration!.WrittenFiles.Should().ContainSingle();
        result.Value.Launch.ProcessId.Should().Be(42);
        configuration.Calls.Should().Be(1);
        launcher.Calls.Should().Be(1);
    }

    [TestMethod]
    public async Task ConfigurationFailure_DoesNotStartGame()
    {
        var configuration = new RecordingConfigurationWriter { Failure = new Error("configuration.failed", "Failed.", ErrorKind.Io) };
        var launcher = new RecordingLauncher();

        var result = await new GameLaunchCoordinator(configuration, launcher).LaunchAsync(Request(), TestContext.CancellationToken);

        result.Error!.Code.Should().Be("configuration.failed");
        launcher.Calls.Should().Be(0);
    }

    [TestMethod]
    public async Task InvalidExecutable_DoesNotModifyConfiguration()
    {
        var configuration = new RecordingConfigurationWriter();
        var launcher = new RecordingLauncher { ValidationFailure = new Error("launch.executable_missing", "Missing.", ErrorKind.NotFound) };

        var result = await new GameLaunchCoordinator(configuration, launcher).LaunchAsync(Request(), TestContext.CancellationToken);

        result.Error!.Code.Should().Be("launch.executable_missing");
        configuration.Calls.Should().Be(0);
        launcher.Calls.Should().Be(0);
    }

    [TestMethod]
    public async Task ChallengeMode_SkipsConfigurationAndRemovesAllowConsole()
    {
        var configuration = new RecordingConfigurationWriter();
        var launcher = new RecordingLauncher();
        var request = Request() with { Variant = GameVariant.XCom2WarOfTheChosenChallengeMode, Arguments = [new LaunchArgument("-AllowConsole"), new LaunchArgument("-Name=Mixed Case")] };

        var result = await new GameLaunchCoordinator(configuration, launcher).LaunchAsync(request, TestContext.CancellationToken);

        result.Value!.Configuration.Should().BeNull();
        configuration.Calls.Should().Be(0);
        launcher.Last!.Arguments.Select(argument => argument.Value).Should().Equal("-Name=Mixed Case");
    }

    [TestMethod]
    public async Task VanillaWithWotcOnlyMod_FailsBeforeValidationOrConfiguration()
    {
        var configuration = new RecordingConfigurationWriter();
        var launcher = new RecordingLauncher();
        var request = Request() with
        {
            ActiveMods = [new GameLaunchMod(new AAML.Domain.Mods.ModKey(AAML.Domain.Mods.ModSource.Manual, "C:\\Mods\\WotC"), new AAML.Domain.Mods.PackageId("WotCOnly"), 0, true)]
        };

        var result = await new GameLaunchCoordinator(configuration, launcher).LaunchAsync(request, TestContext.CancellationToken);

        result.Error!.Code.Should().Be("launch.wotc_mods_in_vanilla");
        configuration.Calls.Should().Be(0);
        launcher.Calls.Should().Be(0);
    }

    public TestContext TestContext { get; set; }

    private static GameLaunchRequest Request() => new(GameVariant.XCom2, "C:\\Games\\XCOM 2", [], [], []);

    private sealed class RecordingConfigurationWriter : IGameConfigurationWriter
    {
        public int Calls { get; private set; }
        public Error? Failure { get; init; }
        public Task<Result<GameConfigurationReceipt>> ApplyAsync(GameLaunchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Failure is null
                ? Result<GameConfigurationReceipt>.Success(new GameConfigurationReceipt(["XComModOptions.ini"], [], []))
                : Result<GameConfigurationReceipt>.Failure(Failure));
        }
    }

    private sealed class RecordingLauncher : IGameLauncher
    {
        public int Calls { get; private set; }
        public GameLaunchRequest? Last { get; private set; }
        public Error? ValidationFailure { get; init; }
        public Task<Result> ValidateAsync(GameLaunchRequest request, CancellationToken cancellationToken) => Task.FromResult(ValidationFailure is null ? Result.Success() : Result.Failure(ValidationFailure));
        public Task<Result<GameLaunchReceipt>> LaunchAsync(GameLaunchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            return Task.FromResult(Result<GameLaunchReceipt>.Success(new GameLaunchReceipt(DateTimeOffset.UtcNow, 42, "XCom2.exe")));
        }
    }
}
