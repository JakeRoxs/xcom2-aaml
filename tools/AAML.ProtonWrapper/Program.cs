using System.Diagnostics;
using System.Text.Json;
using AAML.Application.Common;
using AAML.Application.Steam;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Files;
using AAML.Infrastructure.Linux.Launching;
using AAML.Infrastructure.Linux.Paths;
using AAML.Infrastructure.Linux.Steam;
using AAML.Infrastructure.Steam;

if (!OperatingSystem.IsLinux()) return Fail("steam.launch.platform_unsupported", "The Proton wrapper requires Linux.", 78);
if (args is ["--steam-probe", .. var probeArgs]) return await SteamProbeRunner.RunAsync(probeArgs);
if (args.Length == 0) return Fail("steam.launch.command_empty", "Steam supplied an empty command.", 64);
if (Environment.GetEnvironmentVariable(ProtonCommandPlanner.RecursionMarker) == "1") return Fail("steam.launch.recursive_invocation", "The wrapper was invoked recursively.", 78);

var appIdText = Environment.GetEnvironmentVariable("SteamAppId") ?? Environment.GetEnvironmentVariable("SteamGameId");
if (!uint.TryParse(appIdText, out var appIdValue) || appIdValue is not (268500 or 882100))
    return Fail("steam.launch.app_id_missing", "Steam did not provide a supported app ID.", 78);

var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
var explicitRuntime = Environment.GetEnvironmentVariable("AAML_RUNTIME_DIR");
var runtime = !string.IsNullOrWhiteSpace(explicitRuntime) ? explicitRuntime :
    !string.IsNullOrWhiteSpace(xdgRuntime) ? xdgRuntime.TrimEnd('/') + "/aaml" : null;
if (runtime is null) return Fail("steam.launch.runtime_unavailable", "No trusted runtime directory is available.", 75);

var store = new LinuxSteamLaunchRequestStore(runtime);
var claim = await store.TryClaimAsync(new SteamAppId(appIdValue), DateTimeOffset.UtcNow, CancellationToken.None);
if (!claim.IsSuccess) return Fail(claim.Error!.Code, claim.Error.Message, 65);
var request = claim.Value?.Request;

if (request is not null)
{
    if (!File.Exists(request.TargetExecutablePath) || !Directory.Exists(request.GameInstallPath))
        return Fail("steam.launch.target_not_found", "The requested executable or installation no longer exists.", 66);
    var physical = new LinuxPhysicalPathResolver();
    var install = physical.ResolveExisting(request.GameInstallPath);
    var target = physical.ResolveExisting(request.TargetExecutablePath);
    if (!install.IsSuccess || !target.IsSuccess || new LinuxPathSemantics().IsContainedBy(target.Value!, install.Value!).Value != true)
        return Fail("steam.launch.target_outside_install", "The requested executable is outside the selected installation.", 65);
    var activeMods = request.ActivePackageIds.Select((package, order) => new GameLaunchMod(
        new ModKey(ModSource.Manual, request.TargetExecutablePath + "#" + package), new PackageId(package), order, false)).ToArray();
    var configurationRequest = new GameLaunchRequest(request.Variant, request.GameInstallPath, request.ModRootLocations, activeMods,
        request.AdditionalArguments.Select(argument => new LaunchArgument(argument)).ToArray());
    var configured = await new LinuxGameConfigurationWriter(new AtomicTextWriter()).ApplyAsync(configurationRequest, CancellationToken.None);
    if (!configured.IsSuccess) return Fail(configured.Error!.Code, configured.Error.Message, 74);
}

var environment = Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .Where(entry => entry.Key is string && entry.Value is string)
    .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
var plan = ProtonCommandPlanner.Plan(request, args, environment, Environment.ProcessPath ?? "aaml-proton-wrapper");
if (!plan.IsSuccess) return Fail(plan.Error!.Code, plan.Error.Message, 65);

try
{
    var start = new ProcessStartInfo { FileName = plan.Value!.Tokens[0], UseShellExecute = false };
    foreach (var token in plan.Value.Tokens.Skip(1)) start.ArgumentList.Add(token);
    start.Environment.Clear();
    foreach (var (key, value) in plan.Value.Environment) start.Environment[key] = value;
    using var process = Process.Start(start);
    if (process is null) return Fail("steam.launch.exec_failed", "The expanded Steam command did not start.", 126);
    await process.WaitForExitAsync();
    return process.ExitCode;
}
catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
{
    return Fail("steam.launch.exec_failed", exception.Message, 126);
}

static int Fail(string code, string message, int exitCode)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = new { code, message } }));
    return exitCode;
}
