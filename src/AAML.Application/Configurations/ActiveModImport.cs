using System.Security.Cryptography;
using System.Text;
using AAML.Application.Common;
using AAML.Application.Mods;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Mods;

namespace AAML.Application.Configurations;

public enum ActiveModImportMode { Replace, Merge }
public enum ActiveModResolution { Resolved, Unknown, Ambiguous, Duplicate }
public sealed record ActiveModSource(string Path, string Contents, bool IsGenerated, bool Exists = true);
public sealed record ActiveModImportRow(int Order, string PackageId, string SourcePath, int LineNumber, ActiveModResolution Resolution, ModKey? SelectedMod, IReadOnlyList<ModKey> Candidates);
public sealed record ActiveModImportPreview(GameVariant Variant, ActiveModImportMode Mode, string Fingerprint, IReadOnlyList<ActiveModImportRow> Rows, string Report);

public interface IActiveModImportService
{
    Result<ActiveModImportPreview> Preview(GameVariant variant, ActiveModImportMode mode, IReadOnlyList<ActiveModSource> sources, IReadOnlyList<ModInstallation> installations, ApplicationSettings settings);
    Task<Result<ApplicationSettings>> ApplyAsync(ActiveModImportPreview preview, IReadOnlyList<ModInstallation> installations, ApplicationSettings settings, CancellationToken cancellationToken);
}

public sealed class ActiveModImportService(IModIntentService intents) : IActiveModImportService
{
    public Result<ActiveModImportPreview> Preview(GameVariant variant, ActiveModImportMode mode, IReadOnlyList<ActiveModSource> sources, IReadOnlyList<ModInstallation> installations, ApplicationSettings settings)
    {
        var parsed = sources.OrderByDescending(source => source.IsGenerated).SelectMany(Parse).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ActiveModImportRow>();
        foreach (var item in parsed)
        {
            var candidates = installations.Where(mod => mod.PackageId.Value.Equals(item.PackageId, StringComparison.OrdinalIgnoreCase)).Select(mod => mod.Key).ToArray();
            var duplicate = !seen.Add(item.PackageId);
            ModKey? selected = null;
            if (!duplicate && candidates.Length == 1) selected = candidates[0];
            else if (!duplicate && candidates.Length > 1)
                selected = settings.DuplicatePreferences?.SingleOrDefault(preference => preference.PackageId.Value.Equals(item.PackageId, StringComparison.OrdinalIgnoreCase) && candidates.Contains(preference.PreferredInstallation))?.PreferredInstallation;
            var resolution = duplicate ? ActiveModResolution.Duplicate : selected is not null ? ActiveModResolution.Resolved : candidates.Length == 0 ? ActiveModResolution.Unknown : ActiveModResolution.Ambiguous;
            rows.Add(new(rows.Count, item.PackageId, item.Path, item.Line, resolution, selected, candidates));
        }
        var canonical = $"{variant}|{mode}|" + string.Join('|', rows.Select(row => $"{row.PackageId.ToUpperInvariant()}:{row.SourcePath}:{row.LineNumber}"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var report = $"ActiveMods migration preview\nVariant: {variant}\nMode: {mode}\nSource preserved: yes\nFingerprint: {fingerprint}\n" + string.Join('\n', sources.Select(source => $"Source: {source.Path} | {(source.Exists ? "loaded" : "missing")}")) + "\n" + string.Join('\n', rows.Select(row => $"{row.Order + 1}. {row.PackageId} | {row.Resolution} | {row.SourcePath}:{row.LineNumber}"));
        return Result<ActiveModImportPreview>.Success(new(variant, mode, fingerprint, rows, report));
    }

    public Task<Result<ApplicationSettings>> ApplyAsync(ActiveModImportPreview preview, IReadOnlyList<ModInstallation> installations, ApplicationSettings settings, CancellationToken cancellationToken)
    {
        if (settings.SelectedGame != preview.Variant) return Task.FromResult(Result<ApplicationSettings>.Failure(new Error("active_mods.variant_changed", "The selected game changed after preview.", ErrorKind.Conflict)));
        var resolved = preview.Rows.Where(row => row.Resolution == ActiveModResolution.Resolved && row.SelectedMod is not null).Select(row => row.SelectedMod!.Value).ToArray();
        var edits = new List<ModIntentEdit>();
        if (preview.Mode == ActiveModImportMode.Replace)
            edits.AddRange(installations.Where(mod => !resolved.Contains(mod.Key)).Select(mod => new ModIntentEdit(mod.Key, false, null)));
        edits.AddRange(resolved.Select((mod, order) => new ModIntentEdit(mod, true, order)));
        return intents.SaveAsync(settings, edits.GroupBy(edit => edit.Mod).Select(group => group.Last()).ToArray(), cancellationToken);
    }

    private static IEnumerable<(string PackageId, string Path, int Line)> Parse(ActiveModSource source)
    {
        var section = false;
        var lineNumber = 0;
        using var reader = new StringReader(source.Contents);
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line.Equals("[Engine.XComModOptions]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!section || line.StartsWith(';') || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator < 0 || !line[..separator].Trim().Equals("ActiveMods", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (!string.IsNullOrWhiteSpace(value)) yield return (value.Trim(), source.Path, lineNumber);
        }
    }
}
