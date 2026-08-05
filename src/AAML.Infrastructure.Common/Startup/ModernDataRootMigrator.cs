using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Common.Startup;

public enum DataRootMigrationStatus { Completed, CompletedWithConflicts, Failed }
public enum DataRootMigrationOutcome { Copied, SourceMissing, DestinationOnly, AlreadyPresent, Conflict, Failed }
public sealed record DataRootMigrationItem(string Id, string Source, string Destination, bool Durable, DataRootMigrationOutcome Outcome, string? Sha256, long? Length, string? Message);
public sealed record DataRootMigrationReceipt(int SchemaVersion, string MigrationId, DataRootMigrationStatus Status, DateTimeOffset UpdatedAtUtc, IReadOnlyList<DataRootMigrationItem> Items);

public static class ModernDataRootMigrator
{
    private static readonly JsonSerializerSettings SerializerSettings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver(), Formatting = Formatting.Indented, Converters = { new StringEnumConverter() } };
    private static readonly (string Id, Func<IApplicationPaths, string> Root, Func<IApplicationPaths, string> Path, bool Durable)[] Manifest =
    [
        ("settings", paths => paths.ConfigurationDirectory, paths => Path.Combine(paths.ConfigurationDirectory, "settings.json"), true),
        ("settings-backup", paths => paths.ConfigurationDirectory, paths => Path.Combine(paths.ConfigurationDirectory, "settings.json.bak"), true),
        ("profiles", paths => paths.DataDirectory, paths => Path.Combine(paths.DataDirectory, "Profiles", "profiles.json"), true),
        ("profiles-backup", paths => paths.DataDirectory, paths => Path.Combine(paths.DataDirectory, "Profiles", "profiles.json.bak"), true),
        ("snapshots", paths => paths.DataDirectory, paths => Path.Combine(paths.DataDirectory, "ConfigurationSnapshots", "snapshots.json"), true),
        ("snapshots-backup", paths => paths.DataDirectory, paths => Path.Combine(paths.DataDirectory, "ConfigurationSnapshots", "snapshots.json.bak"), true),
        ("log", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log"), false),
        ("log-1", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.1"), false),
        ("log-2", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.2"), false),
        ("log-3", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.3"), false),
        ("log-4", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.4"), false),
        ("log-5", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.5"), false)
    ];

    public static DataRootMigrationReceipt Migrate(IApplicationPaths former, IApplicationPaths current, CancellationToken cancellationToken)
    {
        var migrationDirectory = Path.Combine(current.StateDirectory, "Migrations");
        Directory.CreateDirectory(migrationDirectory);
        EnsureSafeDirectory(current.StateDirectory, current.StateDirectory); EnsureSafeDirectory(migrationDirectory, current.StateDirectory);
        var lockPath = Path.Combine(migrationDirectory, "modern-data-root-v1.lock");
        using var migrationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var items = new List<DataRootMigrationItem>();
        foreach (var entry in Manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = entry.Path(former); var destination = entry.Path(current);
            try { items.Add(Process(entry.Id, source, entry.Root(former), destination, entry.Root(current), entry.Durable, cancellationToken)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { items.Add(new(entry.Id, source, destination, entry.Durable, DataRootMigrationOutcome.Failed, null, null, exception.Message)); }
            WriteReceipt(migrationDirectory, Build(items));
        }
        var receipt = Build(items); WriteReceipt(migrationDirectory, receipt); return receipt;
    }

    private static DataRootMigrationItem Process(string id, string source, string sourceRoot, string destination, string destinationRoot, bool durable, CancellationToken cancellationToken)
    {
        var sourceExists = File.Exists(source); var destinationExists = File.Exists(destination);
        if (!sourceExists && !destinationExists) return new(id, source, destination, durable, DataRootMigrationOutcome.SourceMissing, null, null, null);
        if (!sourceExists) { EnsureRegularFile(destination, destinationRoot); var target = Hash(destination, cancellationToken); return new(id, source, destination, durable, DataRootMigrationOutcome.DestinationOnly, target.Hash, target.Length, null); }
        EnsureRegularFile(source, sourceRoot); var sourceHash = Hash(source, cancellationToken);
        if (destinationExists)
        {
            EnsureRegularFile(destination, destinationRoot); var destinationHash = Hash(destination, cancellationToken);
            return sourceHash == destinationHash
                ? new(id, source, destination, durable, DataRootMigrationOutcome.AlreadyPresent, destinationHash.Hash, destinationHash.Length, null)
                : new(id, source, destination, durable, DataRootMigrationOutcome.Conflict, destinationHash.Hash, destinationHash.Length, "Destination retained; former-root file differs.");
        }
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Migration destination has no parent directory.");
        Directory.CreateDirectory(parent); EnsureSafeDirectory(parent, destinationRoot);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.aaml-migrate-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough)) { input.CopyTo(output); output.Flush(true); }
            var copied = Hash(temporary, cancellationToken);
            if (copied != sourceHash) throw new IOException("Copied migration file failed hash verification.");
            File.Move(temporary, destination, false);
            return new(id, source, destination, durable, DataRootMigrationOutcome.Copied, copied.Hash, copied.Length, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static DataRootMigrationReceipt Build(IReadOnlyList<DataRootMigrationItem> items)
    {
        var blocking = items.Any(item => item.Durable && item.Outcome == DataRootMigrationOutcome.Failed);
        var conflicts = items.Any(item => item.Outcome == DataRootMigrationOutcome.Conflict);
        return new(1, "modern-data-root-v1", blocking ? DataRootMigrationStatus.Failed : conflicts ? DataRootMigrationStatus.CompletedWithConflicts : DataRootMigrationStatus.Completed, DateTimeOffset.UtcNow, items.ToArray());
    }
    private static void WriteReceipt(string directory, DataRootMigrationReceipt receipt)
    {
        var destination = Path.Combine(directory, "modern-data-root-v1.json"); var temporary = Path.Combine(directory, $".modern-data-root-v1-{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(temporary, JsonConvert.SerializeObject(receipt, SerializerSettings) + Environment.NewLine); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static (string Hash, long Length) Hash(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; int read; long length = 0;
        while ((read = stream.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); length += read; }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }
    private static void EnsureRegularFile(string path, string root) { if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(path).HasFlag(FileAttributes.Directory)) throw new InvalidDataException("Migration source or destination must be a regular file."); EnsureSafeDirectory(Path.GetDirectoryName(path)!, root); }
    private static void EnsureSafeDirectory(string path, string root)
    {
        for (var current = new DirectoryInfo(path); current is not null && current.Exists; current = current.Parent)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Application-owned migration paths cannot traverse reparse points.");
            if (Path.GetFullPath(current.FullName).Equals(Path.GetFullPath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
        }
        throw new InvalidDataException("Migration path is outside its application-owned root.");
    }
}
