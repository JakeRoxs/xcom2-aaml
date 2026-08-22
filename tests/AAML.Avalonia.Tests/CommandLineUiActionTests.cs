using System.Text.Json;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using FluentAssertions;

namespace AAML.Avalonia.Tests;

[TestClass]
public sealed class CommandLineUiActionTests
{
    [TestMethod]
    public void ParserLeavesUnrecognizedAvaloniaArgumentsForDesktopStartup()
    {
        var request = CommandLineStartupParser.Parse(["--some-avalonia-switch", "value"]);

        request.Mode.Should().Be(CommandLineStartupMode.Desktop);
    }

    [TestMethod]
    public void ParserCreatesActionRequestWithValuesAndFlags()
    {
        var request = CommandLineStartupParser.Parse(["--ui-action", "refresh-mods", "--game", "xcom2", "--dry-run", "--output=json"]);

        request.Mode.Should().Be(CommandLineStartupMode.UiAction);
        request.Action.Should().NotBeNull();
        request.Action!.Name.Should().Be("refresh-mods");
        request.Action.Options.Should().ContainKey("game").WhoseValue.Should().Be("xcom2");
        request.Action.Options.Should().ContainKey("dry-run").WhoseValue.Should().Be("true");
        request.Action.Options.Should().ContainKey("output").WhoseValue.Should().Be("json");
    }

    [TestMethod]
    public void ParserRejectsMissingActionName()
    {
        var request = CommandLineStartupParser.Parse(["--ui-action"]);

        request.Mode.Should().Be(CommandLineStartupMode.Invalid);
        request.Error.Should().Contain("Missing UI action name");
    }

    [TestMethod]
    public void ParserRejectsArgumentsBeforeExplicitUiAction()
    {
        var request = CommandLineStartupParser.Parse(["--pretty", "--ui-action", "linux-environment"]);

        request.Mode.Should().Be(CommandLineStartupMode.Invalid);
        request.Error.Should().Contain("first command-line argument");
    }

    [TestMethod]
    [DataRow("--ui-action", "startup-smoke", "--list-ui-actions")]
    [DataRow("--ui-action", "startup-smoke", "--help")]
    [DataRow("--help", "--list-ui-actions", null)]
    public void ParserRejectsAmbiguousCommandModes(string first, string second, string? third)
    {
        var arguments = third is null ? new[] { first, second } : new[] { first, second, third };

        var request = CommandLineStartupParser.Parse(arguments);

        request.Mode.Should().Be(CommandLineStartupMode.Invalid);
        request.Error.Should().Contain("exactly one command mode");
    }

    [TestMethod]
    public async Task DispatcherListsAndRunsRegisteredStartupSmokeAction()
    {
        var dispatcher = CommandLineUiActionRegistry.CreateDispatcher();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var listExitCode = await dispatcher.RunAsync(new(CommandLineStartupMode.ListUiActions), output, error, TestContext.CancellationToken);
        var runExitCode = await dispatcher.RunAsync(CommandLineStartupParser.Parse(["--ui-action", "startup-smoke"]), output, error, TestContext.CancellationToken);

        listExitCode.Should().Be(0);
        runExitCode.Should().Be(0);
        output.ToString().Should().Contain("startup-smoke").And.Contain("linux-environment").And.Contain("AAML UI action dispatcher is available.");
        error.ToString().Should().BeEmpty();
    }

    [TestMethod]
    public async Task LinuxEnvironmentAction_WritesStableJsonAndReturnsSuccess()
    {
        var report = Report(success: true);
        var action = new LinuxEnvironmentUiAction(new StubLinuxEnvironmentService(Result<LinuxEnvironmentDiagnostic>.Success(report)));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await action.ExecuteAsync(new("linux-environment", new Dictionary<string, string> { ["variant"] = "wotc", ["pretty"] = "true", ["output"] = "json" }), output, error, TestContext.CancellationToken);

        exitCode.Should().Be(0);
        error.ToString().Should().BeEmpty();
        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("variants")[0].GetProperty("configurationDirectory").GetString().Should().Be("/prefix/Documents/my games/XCOM2/XComGame/Config");
    }

    [TestMethod]
    public async Task LinuxEnvironmentAction_UsesDeterministicFailureExitCodes()
    {
        var failedReport = Report(success: false) with
        {
            Variants = [Report(true).Variants[0] with { Success = false, ErrorCode = "path.known_artifact_case_ambiguous", ErrorMessage = "Ambiguous path" }]
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var layoutExit = await new LinuxEnvironmentUiAction(new StubLinuxEnvironmentService(Result<LinuxEnvironmentDiagnostic>.Success(failedReport)))
            .ExecuteAsync(new("linux-environment", new Dictionary<string, string>()), output, error, TestContext.CancellationToken);
        var platformExit = await RunFailureAsync(new Error("linux_environment.platform_unsupported", "Linux required", ErrorKind.Unavailable));
        var discoveryExit = await RunFailureAsync(new Error("linux_environment.installation_missing", "Missing", ErrorKind.NotFound));

        layoutExit.Should().Be(5);
        error.ToString().Should().Contain("path.known_artifact_case_ambiguous");
        platformExit.Should().Be(3);
        discoveryExit.Should().Be(4);
    }

    [TestMethod]
    [DataRow("unknown", "true")]
    [DataRow("variant", "challenge")]
    [DataRow("pretty", "sometimes")]
    [DataRow("output", "text")]
    [DataRow("installation", "")]
    [DataRow("steam-root", "true")]
    [DataRow("output", "")]
    [DataRow("installation", "relative/path")]
    [DataRow("steam-root", "relative/path")]
    public async Task LinuxEnvironmentAction_RejectsUnknownOrInvalidOptions(string option, string value)
    {
        var action = new LinuxEnvironmentUiAction(new StubLinuxEnvironmentService(Result<LinuxEnvironmentDiagnostic>.Success(Report(true))));
        using var error = new StringWriter();

        var exitCode = await action.ExecuteAsync(new("linux-environment", new Dictionary<string, string> { [option] = value }), TextWriter.Null, error, TestContext.CancellationToken);

        exitCode.Should().Be(2);
        error.ToString().Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task LinuxEnvironmentAction_MapsCancellationAndExpectedFilesystemExceptions()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new LinuxEnvironmentUiAction(new ThrowingLinuxEnvironmentService(new OperationCanceledException(cancellation.Token)));
        var denied = new LinuxEnvironmentUiAction(new ThrowingLinuxEnvironmentService(new UnauthorizedAccessException("denied")));
        using var cancelledError = new StringWriter();
        using var deniedError = new StringWriter();

        var cancelledExit = await cancelled.ExecuteAsync(new("linux-environment", new Dictionary<string, string>()), TextWriter.Null, cancelledError, cancellation.Token);
        var deniedExit = await denied.ExecuteAsync(new("linux-environment", new Dictionary<string, string>()), TextWriter.Null, deniedError, TestContext.CancellationToken);

        cancelledExit.Should().Be(130);
        cancelledError.ToString().Should().Contain("linux_environment.cancelled").And.NotContain("OperationCanceledException");
        deniedExit.Should().Be(1);
        deniedError.ToString().Should().Be("linux_environment.inspection_failed: denied" + Environment.NewLine);
    }

    [TestMethod]
    public async Task DispatcherReportsUnknownActionsWithoutStartingDesktop()
    {
        var dispatcher = CommandLineUiActionRegistry.CreateDispatcher();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await dispatcher.RunAsync(CommandLineStartupParser.Parse(["--ui-action", "missing-action"]), output, error, TestContext.CancellationToken);

        exitCode.Should().Be(2);
        error.ToString().Should().Contain("Unknown UI action 'missing-action'").And.Contain("startup-smoke");
        output.ToString().Should().BeEmpty();
    }

    public TestContext TestContext { get; set; }

    private async Task<int> RunFailureAsync(Error failure) => await new LinuxEnvironmentUiAction(new StubLinuxEnvironmentService(Result<LinuxEnvironmentDiagnostic>.Failure(failure)))
        .ExecuteAsync(new("linux-environment", new Dictionary<string, string>()), TextWriter.Null, TextWriter.Null, TestContext.CancellationToken);

    private static LinuxEnvironmentDiagnostic Report(bool success) => new(
        1, success, 268500, "/games/XCOM 2", ["/steamapps/workshop/content/268500"], ["/steamapps/compatdata/268500/pfx"],
        [new("XCom2", true, null, null, "/games/XCOM 2", "/games/XCOM 2/Binaries/Win64/XCom2.exe", "/steamapps", "/steamapps/compatdata/268500/pfx", "steamuser", "/prefix/Documents/my games/XCOM2", true, "/prefix/Documents/my games/XCOM2/XComGame/Config", true, [new("/prefix/Documents/My Games", "/prefix/Documents/my games")])], []);

    private sealed class StubLinuxEnvironmentService(Result<LinuxEnvironmentDiagnostic> result) : ILinuxEnvironmentDiagnosticService
    {
        public Task<Result<LinuxEnvironmentDiagnostic>> InspectAsync(LinuxEnvironmentDiagnosticRequest request, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingLinuxEnvironmentService(Exception exception) : ILinuxEnvironmentDiagnosticService
    {
        public Task<Result<LinuxEnvironmentDiagnostic>> InspectAsync(LinuxEnvironmentDiagnosticRequest request, CancellationToken cancellationToken) => Task.FromException<Result<LinuxEnvironmentDiagnostic>>(exception);
    }
}
