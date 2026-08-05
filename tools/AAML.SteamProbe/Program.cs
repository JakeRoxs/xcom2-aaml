using System.Diagnostics;
using System.Text.Json;
using AAML.Domain.Mods;
using AAML.Infrastructure.Steam;

const uint xcom2AppId = 268500;
var appIdPath = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
await File.WriteAllTextAsync(appIdPath, xcom2AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));

try
{
    await using var client = SteamWorkshopClient.Create(new SteamOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20)));
    var subscriptions = await client.Workshop.GetSubscribedItemsAsync(CancellationToken.None);
    if (!subscriptions.IsSuccess)
    {
        Print(new { success = false, stage = "initialize/subscriptions", error = subscriptions.Error, nativeModules = NativeModules() });
        return 2;
    }

    var requested = ParseRequestedId(args) ?? subscriptions.Value!.FirstOrDefault();
    object? item = null;
    if (requested.Value != 0)
    {
        var details = await client.Workshop.GetItemAsync(requested, CancellationToken.None);
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
        var details = await client.Workshop.GetItemsAsync(subscriptions.Value!, null, CancellationToken.None);
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
        appId = xcom2AppId,
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

static WorkshopId? ParseRequestedId(string[] arguments)
{
    var value = arguments.FirstOrDefault(argument => argument.StartsWith("--workshop-id=", StringComparison.Ordinal));
    return value is not null && ulong.TryParse(value["--workshop-id=".Length..], out var id) && id != 0 ? new WorkshopId(id) : null;
}

static IReadOnlyList<string> NativeModules()
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

static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
