using System.Diagnostics;
using System.Text.Json;
using AAML.Domain.Mods;

namespace AAML.Infrastructure.Steam;

/// <summary>Executes the live Steam Workshop diagnostic probe for a packaged host.</summary>
public static class SteamProbeRunner
{
    private const uint Xcom2AppId = 268500;

    /// <summary>Runs the probe and returns its process exit code.</summary>
    /// <param name="args">Probe options such as a Workshop ID or dependency listing request.</param>
    /// <param name="cancellationToken">Cancels Steam queries and probe execution.</param>
    /// <returns>The process exit code describing probe success or the failed stage.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(args, out var requestedId, out var listDependencies, out var argumentError))
        {
            Print(new { success = false, stage = "arguments", error = argumentError });
            return 64;
        }

        var appIdPath = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
        try
        {
            await File.WriteAllTextAsync(appIdPath, Xcom2AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            await using var client = SteamWorkshopClient.Create(new SteamOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20)));
            var subscriptions = await client.Workshop.GetSubscribedItemsAsync(cancellationToken).ConfigureAwait(false);
            if (!subscriptions.IsSuccess)
            {
                Print(new { success = false, stage = "initialize/subscriptions", error = subscriptions.Error, nativeModules = NativeModules() });
                return 2;
            }

            var requested = requestedId ?? subscriptions.Value!.FirstOrDefault();
            object? item = null;
            if (requested.Value != 0)
            {
                var details = await client.Workshop.GetItemAsync(requested, cancellationToken).ConfigureAwait(false);
                if (!details.IsSuccess)
                {
                    Print(new { success = false, stage = "details", subscribedCount = subscriptions.Value!.Count, requested = requested.Value, error = details.Error, nativeModules = NativeModules() });
                    return 3;
                }
                item = details.Value;
            }

            IReadOnlyList<object>? dependencyItems = null;
            if (listDependencies)
            {
                var details = await client.Workshop.GetItemsAsync(subscriptions.Value!, null, cancellationToken).ConfigureAwait(false);
                if (!details.IsSuccess)
                {
                    Print(new { success = false, stage = "dependency-details", subscribedCount = subscriptions.Value!.Count, error = details.Error, nativeModules = NativeModules() });
                    return 5;
                }
                dependencyItems = details.Value!
                    .Where(entry => entry.ChildIds.Count > 0)
                    .Select(entry => (object)new { publishedFileId = entry.PublishedFileId.Value, entry.Title, childIds = entry.ChildIds.Select(child => child.Value).ToArray() })
                    .ToArray();
            }

            Print(new
            {
                success = true,
                appId = Xcom2AppId,
                subscribedCount = subscriptions.Value!.Count,
                queriedWorkshopId = requested.Value == 0 ? (ulong?)null : requested.Value,
                item,
                dependencyItems,
                nativeModules = NativeModules()
            });
            return requested.Value == 0 ? 4 : 0;
        }
        finally
        {
            try { File.Delete(appIdPath); } catch (IOException) { }
        }
    }

    private static bool TryParseArguments(string[] arguments, out WorkshopId? requestedId, out bool listDependencies, out string? error)
    {
        requestedId = null;
        listDependencies = false;
        error = null;
        foreach (var argument in arguments)
        {
            if (argument == "--list-dependencies")
            {
                if (listDependencies) { error = "The --list-dependencies option may be specified only once."; return false; }
                listDependencies = true;
                continue;
            }

            if (argument.StartsWith("--workshop-id=", StringComparison.Ordinal))
            {
                if (requestedId is not null) { error = "The --workshop-id option may be specified only once."; return false; }
                if (!ulong.TryParse(argument["--workshop-id=".Length..], out var id) || id == 0)
                {
                    error = "The --workshop-id option requires a nonzero unsigned integer.";
                    return false;
                }
                requestedId = new WorkshopId(id);
                continue;
            }

            error = $"Unknown Steam probe option: {argument}";
            return false;
        }
        return true;
    }

    private static IReadOnlyList<string> NativeModules()
    {
        if (OperatingSystem.IsWindows())
        {
            return Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Select(module => Path.GetFileName(module.FileName))
                .Where(name => name.Contains("steam_api", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/maps"))
        {
            return File.ReadLines("/proc/self/maps")
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
                .Where(path => path is not null && path.Contains("libsteam_api", StringComparison.Ordinal))
                .Select(path => Path.GetFileName(path!))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        return [];
    }

    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
