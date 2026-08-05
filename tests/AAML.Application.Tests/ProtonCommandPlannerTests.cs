using AAML.Application.Steam;
using AAML.Domain.Games;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ProtonCommandPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Plan_ReplacesExactlyOnePayloadAndPreservesTokensAndEnvironment()
    {
        var request = Request(GameVariant.XCom2WarOfTheChosen, "/other library/XCOM 2/XCom2-WarOfTheChosen/Binaries/Win64/XCom2.exe", ["-Name=Mixed Case", "&;|$() Ω"]);
        var command = new[] { "/steam/runtime/run", "--verb=waitforexitandrun", "/tools/proton", "run", "/library/XCOM 2/Binaries/Win64/XCom2.exe", "-steam-original", "Value With Spaces" };
        var environment = new Dictionary<string, string> { ["STEAM_COMPAT_DATA_PATH"] = "/prefix path", ["LD_LIBRARY_PATH"] = "/one:/two" };

        var result = ProtonCommandPlanner.Plan(request, command, environment, "/opt/aaml/aaml-steam-wrapper");

        result.Value!.Tokens.Should().Equal(
            "/steam/runtime/run", "--verb=waitforexitandrun", "/tools/proton", "run",
            request.TargetExecutablePath, "-steam-original", "Value With Spaces", "-Name=Mixed Case", "&;|$() Ω");
        result.Value.Environment.Should().Contain(environment);
        result.Value.Environment[ProtonCommandPlanner.RecursionMarker].Should().Be("1");
        result.Value.IsPassThrough.Should().BeFalse();
    }

    [TestMethod]
    public void NoRequest_PassesOriginalVectorUnchanged()
    {
        var command = new[] { "/runtime", "token with spaces", "$HOME;rm -rf /" };

        var result = ProtonCommandPlanner.Plan(null, command, new Dictionary<string, string>(), "/wrapper");

        result.Value!.Tokens.Should().Equal(command);
        result.Value.IsPassThrough.Should().BeTrue();
    }

    [TestMethod]
    public void MissingAmbiguousAndRecursiveCommands_FailClosed()
    {
        var request = Request(GameVariant.XCom2, "/games/XCOM 2/Binaries/Win64/XCom2.exe", []);

        ProtonCommandPlanner.Plan(request, ["/proton", "not-the-game.exe"], new Dictionary<string, string>(), "/wrapper").Error!.Code.Should().Be("steam.launch.executable_token_not_found");
        ProtonCommandPlanner.Plan(request, ["/a/XCom2.exe", "/b/XCom2.exe"], new Dictionary<string, string>(), "/wrapper").Error!.Code.Should().Be("steam.launch.executable_token_ambiguous");
        ProtonCommandPlanner.Plan(request, ["/a/XCom2.exe"], new Dictionary<string, string> { [ProtonCommandPlanner.RecursionMarker] = "1" }, "/wrapper").Error!.Code.Should().Be("steam.launch.recursive_invocation");
    }

    [TestMethod]
    public void RequestPolicy_RejectsStaleFutureWrongAppAndEscapedTargets()
    {
        var valid = Request(GameVariant.XCom2, "/games/XCOM 2/Binaries/Win64/XCom2.exe", []);

        SteamLaunchRequestPolicy.Validate(valid, Now).IsSuccess.Should().BeTrue();
        SteamLaunchRequestPolicy.Validate(valid with { ExpiresAtUtc = Now }, Now).Error!.Code.Should().Be("steam.launch.request_expired");
        SteamLaunchRequestPolicy.Validate(valid with { CreatedAtUtc = Now.AddSeconds(6), ExpiresAtUtc = Now.AddSeconds(30) }, Now).Error!.Code.Should().Be("steam.launch.request_not_yet_valid");
        SteamLaunchRequestPolicy.Validate(valid with { AppId = SteamAppId.ChimeraSquad }, Now).Error!.Code.Should().Be("steam.launch.wrong_app_id");
        SteamLaunchRequestPolicy.Validate(valid with { TargetExecutablePath = "/games/XCOM 2/../outside/Binaries/Win64/XCom2.exe" }, Now).Error!.Code.Should().Be("steam.launch.target_outside_install");
    }

    private static SteamLaunchRequest Request(GameVariant variant, string target, IReadOnlyList<string> arguments) => new(
        SteamLaunchRequestPolicy.CurrentProtocolVersion, Guid.NewGuid(), new SteamAppId(GameVariantPolicy.GetSteamAppId(variant)), variant,
        variant == GameVariant.XCom2 ? "/games/XCOM 2" : "/other library/XCOM 2",
        target, [], ["/games/XCOM 2/Workshop"], arguments, Now, Now.AddSeconds(30));
}
