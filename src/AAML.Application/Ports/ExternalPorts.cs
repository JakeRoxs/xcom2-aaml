using AAML.Application.Common;
using AAML.Domain.Launching;
using AAML.Domain.Mods;

namespace AAML.Application.Ports;

/// <summary>Reads Steam Workshop information without exposing Steamworks types.</summary>
public interface IWorkshopService
{
    Task<Result<WorkshopItem?>> GetItemAsync(WorkshopId publishedFileId, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<WorkshopItem>>> GetItemsAsync(IReadOnlyList<WorkshopId> publishedFileIds, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<WorkshopId>>> GetSubscribedItemsAsync(CancellationToken cancellationToken);
    Task<Result<WorkshopLocalState>> GetLocalStateAsync(WorkshopId publishedFileId, CancellationToken cancellationToken);
    Task<Result> RequestDownloadAsync(WorkshopId publishedFileId, bool highPriority, CancellationToken cancellationToken);
    Task<Result> SubscribeAsync(WorkshopId publishedFileId, CancellationToken cancellationToken) => Task.FromResult(Result.Failure(new Error("steam.subscription_unsupported", "Workshop subscription is not supported by this adapter.", ErrorKind.Unavailable)));
    Task<Result> UnsubscribeAsync(WorkshopId publishedFileId, CancellationToken cancellationToken) => Task.FromResult(Result.Failure(new Error("steam.subscription_unsupported", "Workshop unsubscription is not supported by this adapter.", ErrorKind.Unavailable)));
    Task<Result<string?>> GetPersonaNameAsync(ulong steamId, CancellationToken cancellationToken);
}

/// <summary>Downloads and caches trusted Workshop preview images.</summary>
public interface IWorkshopPreviewCache
{
    Task<Result<string?>> GetAsync(WorkshopId workshopId, string? previewUrl, CancellationToken cancellationToken);
}

/// <summary>Application-owned Workshop information.</summary>
public sealed record WorkshopItem(WorkshopId PublishedFileId, string Title, IReadOnlyList<WorkshopId> ChildIds, string? Description = null, ulong? OwnerSteamId = null, IReadOnlyList<string>? Tags = null, bool TagsTruncated = false, DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null, DateTimeOffset? AddedAt = null, string? PreviewUrl = null);

/// <summary>Local Steam state for one Workshop item.</summary>
public sealed record WorkshopLocalState(
    WorkshopId PublishedFileId,
    WorkshopItemState State,
    WorkshopInstallInfo? Install,
    WorkshopDownloadInfo? Download);

[Flags]
public enum WorkshopItemState
{
    None = 0,
    Subscribed = 1,
    LegacyItem = 2,
    Installed = 4,
    NeedsUpdate = 8,
    Downloading = 16,
    DownloadPending = 32,
    DisabledLocally = 64
}

public sealed record WorkshopInstallInfo(ulong SizeOnDisk, string Folder, DateTimeOffset InstalledAt);
public sealed record WorkshopDownloadInfo(ulong BytesDownloaded, ulong BytesTotal, double? Fraction);

/// <summary>Executes a platform-resolved process request.</summary>
public interface IProcessRunner
{
    Task<Result<ProcessStartResult>> StartAsync(ProcessLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>A validated platform-resolved process request with immutable structured arguments.</summary>
public sealed record ProcessLaunchRequest
{
    public ProcessLaunchRequest(string executablePath, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (Uri.TryCreate(executablePath, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            throw new ArgumentException("Executable targets cannot be non-file URIs.", nameof(executablePath));
        }

        var argumentSnapshot = arguments.ToArray();
        if (argumentSnapshot.Any(argument => argument is null))
        {
            throw new ArgumentException("Process arguments cannot contain null values.", nameof(arguments));
        }

        ExecutablePath = executablePath;
        Arguments = argumentSnapshot;
        WorkingDirectory = workingDirectory;
    }

    public string ExecutablePath { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string? WorkingDirectory { get; }
}

/// <summary>Non-owning information about a started process.</summary>
public sealed record ProcessStartResult(int? ProcessId);

/// <summary>Launches a game from platform-neutral intent.</summary>
public interface IGameLauncher
{
    Task<Result> ValidateAsync(GameLaunchRequest request, CancellationToken cancellationToken);
    Task<Result<GameLaunchReceipt>> LaunchAsync(GameLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>Confirms that launch was requested without exposing process handles.</summary>
public sealed record GameLaunchReceipt(DateTimeOffset RequestedAt, int? ProcessId, string ExecutablePath);

/// <summary>Writes the game-owned configuration required by one launch request.</summary>
public interface IGameConfigurationWriter
{
    Task<Result<GameConfigurationReceipt>> ApplyAsync(GameLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>Identifies the exact configuration files and mod intent prepared for launch.</summary>
public sealed record GameConfigurationReceipt(
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<PackageId> ActivePackageIds,
    IReadOnlyList<string> ModRootLocations);

/// <summary>Writes one text document atomically while retaining one rollback generation.</summary>
public interface IAtomicTextWriter
{
    Task<Result> WriteAsync(string path, string content, CancellationToken cancellationToken);
}

/// <summary>Opens shell targets separately from executable launching.</summary>
public interface IExternalLauncher
{
    Task<Result> OpenUriAsync(Uri uri, CancellationToken cancellationToken);
    Task<Result> OpenFileAsync(string path, CancellationToken cancellationToken);
    Task<Result> OpenDirectoryAsync(string path, CancellationToken cancellationToken);
}

public sealed record ModRemovalPreview(string ConfirmationToken, ModKey Mod, int FileCount, long TotalBytes, IReadOnlyList<string> SampleFiles);
public interface IModRemovalFilesystem
{
    Task<Result<ModRemovalPreview>> PreviewAsync(ModKey mod, IReadOnlyList<string> configuredRoots, CancellationToken cancellationToken);
    Task<Result> DeleteConfirmedAsync(ModRemovalPreview preview, CancellationToken cancellationToken);
}
