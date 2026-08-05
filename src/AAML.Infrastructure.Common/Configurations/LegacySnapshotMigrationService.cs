using System.Security.Cryptography;
using System.Text;
using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using Newtonsoft.Json.Linq;

namespace AAML.Infrastructure.Common.Configurations;

public sealed class LegacySnapshotMigrationService(IConfigurationSnapshotRepository snapshots, IPathSemantics paths) : ILegacySnapshotMigrationService
{
    public async Task<Result<LegacySnapshotMigrationPreview>> PreviewAsync(string sourcePath, string contents, CancellationToken cancellationToken)
    {
        try
        {
            var root = JObject.Parse(contents);
            var items = new List<LegacySnapshotPreviewItem>();
            var index = 0;
            foreach (var mod in root.SelectTokens("$..Mods..*[?(@.Settings)]").OfType<JObject>())
            {
                var modName = (string?)mod["Name"] ?? (string?)mod["ID"] ?? "Unknown mod";
                var source = ((int?)mod["Source"]) switch { 1 => ModSource.SteamWorkshop, 4 => ModSource.Manual, _ => ModSource.Unknown };
                var location = paths.NormalizeIdentity((string?)mod["Path"] ?? string.Empty);
                foreach (var entry in mod["Settings"]?.OfType<JObject>() ?? [])
                {
                    index++;
                    var rawPath = (string?)entry["FilePath"] ?? string.Empty;
                    var diagnostics = new List<string> { "legacy_snapshot.encoding_assumed_utf8: Legacy snapshots do not retain encoding metadata." };
                    if (!location.IsSuccess || !TryNormalizeRelativePath(rawPath, out var relativePath))
                    {
                        diagnostics.Add("legacy_snapshot.path_invalid: The mod location or configuration path is invalid.");
                        items.Add(new(index, modName, rawPath, null, LegacySnapshotAction.Invalid, diagnostics));
                        continue;
                    }
                    var text = (string?)entry["Contents"];
                    if (text is null)
                    {
                        diagnostics.Add("legacy_snapshot.contents_missing: Snapshot contents are missing.");
                        items.Add(new(index, modName, rawPath, null, LegacySnapshotAction.Invalid, diagnostics));
                        continue;
                    }
                    var candidate = new SavedConfigurationSnapshot(new ConfigurationDocumentId(new ModKey(source, location.Value!), relativePath), text, new ConfigurationTextFormat(ConfigurationEncoding.Utf8, DetectNewLines(text)));
                    if (!Directory.Exists(location.Value!)) diagnostics.Add("legacy_snapshot.mod_missing: The original mod installation is missing; the snapshot will still be retained.");
                    else if (!File.Exists(Path.Combine(location.Value!, relativePath.Replace('/', Path.DirectorySeparatorChar)))) diagnostics.Add("legacy_snapshot.file_missing: The original configuration file is missing; the snapshot will still be retained.");
                    var existing = await snapshots.FindAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
                    if (!existing.IsSuccess) return Result<LegacySnapshotMigrationPreview>.Failure(existing.Error!);
                    var action = existing.Value is null ? LegacySnapshotAction.Import : existing.Value == candidate ? LegacySnapshotAction.AlreadyImported : LegacySnapshotAction.Conflict;
                    if (action == LegacySnapshotAction.Conflict) diagnostics.Add("legacy_snapshot.existing_conflict: A different modern snapshot already exists and will not be overwritten.");
                    items.Add(new(index, modName, rawPath, candidate, action, diagnostics));
                }
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();
            var report = $"Legacy configuration snapshot preview\nSource: {sourcePath}\nSource SHA-256: {fingerprint}\nSource preserved: yes\n" + string.Join('\n', items.Select(item => $"{item.Index}. {item.LegacyModName} | {item.RawPath} | {item.Action} | {string.Join(" ", item.Diagnostics)}"));
            return Result<LegacySnapshotMigrationPreview>.Success(new(sourcePath, fingerprint, items, report));
        }
        catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or ArgumentException)
        {
            return Result<LegacySnapshotMigrationPreview>.Failure(new Error("legacy_snapshot.source_invalid", exception.Message, ErrorKind.InvalidData));
        }
    }

    public Task<Result> ApplyAsync(LegacySnapshotMigrationPreview preview, CancellationToken cancellationToken) =>
        snapshots.ImportAsync(preview.Items.Where(item => item.Action == LegacySnapshotAction.Import && item.Snapshot is not null).Select(item => item.Snapshot!).ToArray(), cancellationToken);

    private static bool TryNormalizeRelativePath(string value, out string normalized)
    {
        normalized = value.Replace('\\', '/');
        var parts = normalized.Split('/');
        return parts.Length >= 2 && parts[0].Equals("Config", StringComparison.OrdinalIgnoreCase) && parts.All(part => part.Length > 0 && part is not "." and not "..") && Path.GetExtension(parts[^1]).Equals(".ini", StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(value) && !value.Contains(':');
    }

    private static NewLineStyle DetectNewLines(string text)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var remainder = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        var lf = remainder.Contains('\n');
        var cr = remainder.Contains('\r');
        return (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1 ? NewLineStyle.Mixed : crlf ? NewLineStyle.CrLf : lf ? NewLineStyle.Lf : cr ? NewLineStyle.Cr : NewLineStyle.None;
    }
}
