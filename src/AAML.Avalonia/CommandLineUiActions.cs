using System.Text.Json;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
#if LINUX_RID
using AAML.Infrastructure.Linux.Paths;
using AAML.Infrastructure.Linux.Steam;
#endif

namespace AAML.Avalonia;

internal enum CommandLineStartupMode
{
    Desktop,
    Help,
    ListUiActions,
    UiAction,
    Invalid
}

internal sealed record CommandLineStartupRequest(CommandLineStartupMode Mode, CommandLineUiActionRequest? Action = null, string? Error = null)
{
    public static CommandLineStartupRequest Desktop { get; } = new(CommandLineStartupMode.Desktop);
}

internal sealed record CommandLineUiActionRequest(string Name, IReadOnlyDictionary<string, string> Options);

internal interface ICommandLineUiAction
{
    string Name { get; }
    string Description { get; }
    ValueTask<int> ExecuteAsync(CommandLineUiActionRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken);
}

internal static class CommandLineStartupParser
{
    public static CommandLineStartupRequest Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0) return CommandLineStartupRequest.Desktop;
        if (!arguments.Any(IsAamlCommandLineOption)) return CommandLineStartupRequest.Desktop;

        var help = arguments.Any(argument => argument is "--help" or "-h");
        var list = arguments.Any(argument => argument == "--list-ui-actions");
        var actionCount = arguments.Count(argument => argument == "--ui-action");
        if ((help ? 1 : 0) + (list ? 1 : 0) + (actionCount > 0 ? 1 : 0) > 1 || actionCount > 1)
            return Invalid("Choose exactly one command mode: --ui-action, --list-ui-actions, or --help.");
        if (help) return arguments.Count == 1 ? new(CommandLineStartupMode.Help) : Invalid("--help does not accept additional arguments.");
        if (list) return arguments.Count == 1 ? new(CommandLineStartupMode.ListUiActions) : Invalid("--list-ui-actions does not accept additional arguments.");

        var actionIndex = IndexOf(arguments, "--ui-action");
        if (actionIndex < 0) return Invalid("Use --ui-action <name>, --list-ui-actions, or --help.");
        if (actionIndex != 0) return Invalid("--ui-action must be the first command-line argument.");
        if (actionIndex == arguments.Count - 1) return Invalid("Missing UI action name after --ui-action.");

        var name = arguments[actionIndex + 1];
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("-", StringComparison.Ordinal)) return Invalid("Missing UI action name after --ui-action.");

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = actionIndex + 2; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) return Invalid($"Unexpected positional argument '{argument}'.");

            var trimmed = argument[2..];
            if (string.IsNullOrWhiteSpace(trimmed)) return Invalid("Empty option names are not supported.");

            var separator = trimmed.IndexOf('=');
            if (separator >= 0)
            {
                var key = trimmed[..separator];
                if (string.IsNullOrWhiteSpace(key)) return Invalid("Empty option names are not supported.");
                options[key] = trimmed[(separator + 1)..];
                continue;
            }

            if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                options[trimmed] = arguments[index + 1];
                index++;
            }
            else options[trimmed] = "true";
        }

        return new(CommandLineStartupMode.UiAction, new CommandLineUiActionRequest(name, options));
    }

    private static bool IsAamlCommandLineOption(string argument) => argument is "--ui-action" or "--list-ui-actions" or "--help" or "-h";

    private static int IndexOf(IReadOnlyList<string> arguments, string value)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] == value) return index;
        }

        return -1;
    }

    private static CommandLineStartupRequest Invalid(string error) => new(CommandLineStartupMode.Invalid, Error: error);
}

internal sealed class CommandLineUiActionDispatcher(IEnumerable<ICommandLineUiAction> actions)
{
    private readonly IReadOnlyDictionary<string, ICommandLineUiAction> actions = actions.ToDictionary(action => action.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ICommandLineUiAction> Actions => actions.Values.OrderBy(action => action.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async ValueTask<int> RunAsync(CommandLineStartupRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        switch (request.Mode)
        {
            case CommandLineStartupMode.Help:
                WriteHelp(output);
                return 0;
            case CommandLineStartupMode.ListUiActions:
                WriteActions(output);
                return 0;
            case CommandLineStartupMode.UiAction:
                return await RunActionAsync(request.Action!, output, error, cancellationToken).ConfigureAwait(false);
            case CommandLineStartupMode.Invalid:
                error.WriteLine(request.Error);
                WriteHelp(error);
                return 2;
            default:
                throw new InvalidOperationException($"Command-line mode {request.Mode} cannot be handled by the UI action dispatcher.");
        }
    }

    private async ValueTask<int> RunActionAsync(CommandLineUiActionRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!actions.TryGetValue(request.Name, out var action))
        {
            error.WriteLine($"Unknown UI action '{request.Name}'.");
            WriteActions(error);
            return 2;
        }

        return await action.ExecuteAsync(request, output, error, cancellationToken).ConfigureAwait(false);
    }

    private void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AAML --list-ui-actions");
        writer.WriteLine("  AAML --ui-action <name> [--option value] [--flag]");
        writer.WriteLine();
        WriteActions(writer);
    }

    private void WriteActions(TextWriter writer)
    {
        writer.WriteLine("Available UI actions:");
        foreach (var action in Actions) writer.WriteLine($"  {action.Name} - {action.Description}");
    }
}

internal sealed class StartupSmokeUiAction : ICommandLineUiAction
{
    public string Name => "startup-smoke";
    public string Description => "Verifies the command-line UI action dispatcher without starting the desktop shell.";

    public ValueTask<int> ExecuteAsync(CommandLineUiActionRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        cancellationToken.ThrowIfCancellationRequested();

        output.WriteLine("AAML UI action dispatcher is available.");
        return ValueTask.FromResult(0);
    }
}

internal sealed class LinuxEnvironmentUiAction(ILinuxEnvironmentDiagnosticService service) : ICommandLineUiAction
{
    private static readonly HashSet<string> AllowedOptions = new(["variant", "installation", "steam-root", "pretty", "output"], StringComparer.OrdinalIgnoreCase);
    public string Name => "linux-environment";
    public string Description => "Reports read-only production Steam, Proton, game-path, and casing diagnostics as JSON.";

    public async ValueTask<int> ExecuteAsync(CommandLineUiActionRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var unknown = request.Options.Keys.Where(option => !AllowedOptions.Contains(option)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknown.Length > 0) return Invalid(error, $"Unknown linux-environment option(s): {string.Join(", ", unknown)}");
        if (request.Options.GetValueOrDefault("output") is { } outputFormat && !outputFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
            return Invalid(error, "linux-environment supports --output json only.");
        if (!TryParseBoolean(request.Options.GetValueOrDefault("pretty"), out var pretty))
            return Invalid(error, "--pretty must be true or false.");
        if (!TryParseVariants(request.Options.GetValueOrDefault("variant"), out var variants, out var variantError))
            return Invalid(error, variantError!);

        if (!TryRequiredValue(request.Options, "installation", out var installation, out var valueError) ||
            !TryRequiredValue(request.Options, "steam-root", out var steamRoot, out valueError) ||
            !TryRequiredValue(request.Options, "output", out _, out valueError))
            return Invalid(error, valueError!);
        if (installation is not null && !IsLinuxAbsolutePath(installation)) return Invalid(error, "--installation must be an absolute Linux path.");
        if (steamRoot is not null && !IsLinuxAbsolutePath(steamRoot)) return Invalid(error, "--steam-root must be an absolute Linux path.");
        Result<LinuxEnvironmentDiagnostic> result;
        try
        {
            result = await service.InspectAsync(new LinuxEnvironmentDiagnosticRequest(variants, installation, steamRoot is null ? null : [steamRoot]), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("linux_environment.cancelled: Linux environment diagnostics were cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            error.WriteLine($"linux_environment.inspection_failed: {exception.Message}");
            return 1;
        }
        if (!result.IsSuccess)
        {
            error.WriteLine($"{result.Error!.Code}: {result.Error.Message}");
            if (result.Error.Code == "linux_environment.platform_unsupported") return 3;
            return result.Error.Kind switch
            {
                ErrorKind.Validation => 2,
                ErrorKind.NotFound or ErrorKind.Conflict => 4,
                ErrorKind.Cancelled => 130,
                _ => 1
            };
        }

        output.WriteLine(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = pretty }));
        if (result.Value!.Success) return 0;
        foreach (var variant in result.Value.Variants.Where(variant => !variant.Success))
            error.WriteLine($"{variant.Variant}: {variant.ErrorCode}: {variant.ErrorMessage}");
        return 5;
    }

    private static int Invalid(TextWriter error, string message) { error.WriteLine(message); return 2; }
    private static bool IsLinuxAbsolutePath(string value) => value.StartsWith("/", StringComparison.Ordinal) && !value.Contains('\0');
    private static bool TryRequiredValue(IReadOnlyDictionary<string, string> options, string name, out string? value, out string? error)
    {
        if (!options.TryGetValue(name, out value)) { error = null; return true; }
        if (!string.IsNullOrWhiteSpace(value) && !value.Equals("true", StringComparison.OrdinalIgnoreCase)) { error = null; return true; }
        value = null;
        error = $"--{name} requires an explicit value.";
        return false;
    }
    private static bool TryParseBoolean(string? value, out bool parsed)
    {
        if (value is null) { parsed = false; return true; }
        return bool.TryParse(value, out parsed);
    }
    private static bool TryParseVariants(string? value, out IReadOnlyList<GameVariant> variants, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            variants = [GameVariant.XCom2, GameVariant.XCom2WarOfTheChosen];
            error = null;
            return true;
        }
        if (value.Equals("xcom2", StringComparison.OrdinalIgnoreCase) || value.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
        {
            variants = [GameVariant.XCom2]; error = null; return true;
        }
        if (value.Equals("xcom2warofthechosen", StringComparison.OrdinalIgnoreCase) || value.Equals("wotc", StringComparison.OrdinalIgnoreCase))
        {
            variants = [GameVariant.XCom2WarOfTheChosen]; error = null; return true;
        }
        variants = [];
        error = "--variant must be all, vanilla, xcom2, wotc, or xcom2warofthechosen.";
        return false;
    }
}

internal sealed class UnavailableLinuxEnvironmentDiagnosticService : ILinuxEnvironmentDiagnosticService
{
    public Task<Result<LinuxEnvironmentDiagnostic>> InspectAsync(LinuxEnvironmentDiagnosticRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result<LinuxEnvironmentDiagnostic>.Failure(new Error("linux_environment.platform_unsupported", "Linux environment diagnostics require Linux.", ErrorKind.Unavailable)));
}

internal static class CommandLineUiActionRegistry
{
    public static CommandLineUiActionDispatcher CreateDispatcher() => new(
    [
        new StartupSmokeUiAction(),
        new LinuxEnvironmentUiAction(CreateLinuxEnvironmentService())
    ]);

    private static ILinuxEnvironmentDiagnosticService CreateLinuxEnvironmentService()
    {
#if LINUX_RID
        return new LinuxEnvironmentDiagnosticService(new LinuxSteamFilesystemDiscovery(new LinuxPhysicalPathResolver()));
#else
        return new UnavailableLinuxEnvironmentDiagnosticService();
#endif
    }
}
