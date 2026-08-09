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
    /// <returns>The process exit code describing probe success or the failed stage.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        var appIdPath = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
        await File.WriteAllTextAsync(appIdPath, Xcom2AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);

        try
        {
            await using var client = SteamWorkshopClient.Create(new SteamOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20)));
            var subscriptions = await client.Workshop.GetSubscribedItemsAsync(CancellationToken.None).ConfigureAwait(false);
            if (!subscriptions.IsSuccess)
            {
                Print(new { success = false, stage = "initialize/subscriptions", error = subscriptions.Error, nativeModules = NativeModules() });
                return 2;
            }

            var requested = ParseRequestedId(args) ?? subscriptions.Value!.FirstOrDefault();
            object? item = null;
            if (requested.Value != 0)
            {
                var details = await client.Workshop.GetItemAsync(requested, CancellationToken.None).ConfigureAwait(false);
                if (!details.IsSuccess)
                {
                    Print(new { success = false, stage = "details", subscribedCount = subscriptions.Value!.Count, requested = requested.Value, error = details.Error, nativeModules = NativeModules() });
                    return 3;
                }
                item = details.Value;
            }

            IReadOnlyList<object>? dependencyItems = null;
            if (args.Contains("--list-dependencies", StringComparer.Ordinal))
            {
                var details = await client.Workshop.GetItemsAsync(subscriptions.Value!, null, CancellationToken.None).ConfigureAwait(false);
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

    private static WorkshopId? ParseRequestedId(string[] arguments)
    {
        var value = arguments.FirstOrDefault(argument => argument.StartsWith("--workshop-id=", StringComparison.Ordinal));
        return value is not null && ulong.TryParse(value["--workshop-id=".Length..], out var id) && id != 0 ? new WorkshopId(id) : null;
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
