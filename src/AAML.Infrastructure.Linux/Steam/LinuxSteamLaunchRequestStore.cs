using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AAML.Application.Common;
using AAML.Application.Steam;

namespace AAML.Infrastructure.Linux.Steam;

/// <summary>One-pending-request-per-app store with atomic publication and one-shot claim.</summary>
public sealed class LinuxSteamLaunchRequestStore : ISteamLaunchRequestStore
{
    private const int MaximumRequestBytes = 256 * 1024;
    private readonly string directory;
    private readonly TimeProvider timeProvider;
    private readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public LinuxSteamLaunchRequestStore(string runtimeDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        directory = Path.Combine(runtimeDirectory, "steam-launch");
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<SteamLaunchTicket>> PublishAsync(SteamLaunchRequest request, CancellationToken cancellationToken)
    {
        var validation = SteamLaunchRequestPolicy.Validate(request, timeProvider.GetUtcNow());
        if (!validation.IsSuccess) return Result<SteamLaunchTicket>.Failure(validation.Error!);
        try
        {
            EnsureDirectory();
            var pending = PendingPath(request.AppId);
            if (File.Exists(pending)) return Result<SteamLaunchTicket>.Failure(new Error("steam.launch.request_pending", "A launch request is already pending.", ErrorKind.Conflict));
            var temporary = Path.Combine(directory, $".request-{request.AppId.Value}-{request.RequestId:N}.tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, json);
            if (bytes.Length > MaximumRequestBytes) return Result<SteamLaunchTicket>.Failure(new Error("steam.launch.request_malformed", "The launch request is too large.", ErrorKind.InvalidData));
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, pending, overwrite: false);
            return Result<SteamLaunchTicket>.Success(new SteamLaunchTicket(request.RequestId, request.AppId, request.ExpiresAtUtc));
        }
        catch (OperationCanceledException) { return Result<SteamLaunchTicket>.Failure(new Error("steam.launch.cancelled", "Launch publication was cancelled.", ErrorKind.Cancelled)); }
        catch (IOException exception) { return Result<SteamLaunchTicket>.Failure(new Error("steam.launch.request_publish_failed", exception.Message, ErrorKind.Io)); }
        catch (UnauthorizedAccessException exception) { return Result<SteamLaunchTicket>.Failure(new Error("steam.launch.runtime_untrusted", exception.Message, ErrorKind.Unauthorized)); }
    }

    public async Task<Result<ClaimedSteamLaunchRequest?>> TryClaimAsync(SteamAppId invokedAppId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            EnsureDirectory();
            var pending = PendingPath(invokedAppId);
            if (!File.Exists(pending)) return Result<ClaimedSteamLaunchRequest?>.Success(null);
            if ((File.GetAttributes(pending) & FileAttributes.ReparsePoint) != 0) return Failure("steam.launch.runtime_untrusted", "The pending request is a symbolic link.", ErrorKind.Unauthorized);
            var claimed = Path.Combine(directory, $"claimed-{invokedAppId.Value}-{Guid.NewGuid():N}-{Environment.ProcessId}.json");
            File.Move(pending, claimed, overwrite: false);
            try
            {
                var info = new FileInfo(claimed);
                if (info.Length > MaximumRequestBytes) return Failure("steam.launch.request_malformed", "The launch request is too large.", ErrorKind.InvalidData);
                var bytes = await File.ReadAllBytesAsync(claimed, cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<SteamLaunchRequest>(bytes, json);
                if (request is null) return Failure("steam.launch.request_malformed", "The launch request was null.", ErrorKind.InvalidData);
                if (request.AppId != invokedAppId) return Failure("steam.launch.wrong_app_id", "The invoked Steam app does not match the request.", ErrorKind.Conflict);
                var validation = SteamLaunchRequestPolicy.Validate(request, now);
                return validation.IsSuccess
                    ? Result<ClaimedSteamLaunchRequest?>.Success(new ClaimedSteamLaunchRequest(request, claimed))
                    : Result<ClaimedSteamLaunchRequest?>.Failure(validation.Error!);
            }
            catch (JsonException exception) { return Failure("steam.launch.request_malformed", exception.Message, ErrorKind.InvalidData); }
            finally { try { File.Delete(claimed); } catch (IOException) { } }
        }
        catch (OperationCanceledException) { return Failure("steam.launch.cancelled", "Launch claim was cancelled.", ErrorKind.Cancelled); }
        catch (IOException exception) { return Failure("steam.launch.request_claim_failed", exception.Message, ErrorKind.Io); }
        catch (UnauthorizedAccessException exception) { return Failure("steam.launch.runtime_untrusted", exception.Message, ErrorKind.Unauthorized); }
    }

    private string PendingPath(SteamAppId appId) => Path.Combine(directory, $"request-{appId.Value}.json");

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var mode = File.GetUnixFileMode(directory);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new UnauthorizedAccessException("The launch directory is accessible to another user.");
        }
    }

    private static Result<ClaimedSteamLaunchRequest?> Failure(string code, string message, ErrorKind kind) => Result<ClaimedSteamLaunchRequest?>.Failure(new Error(code, message, kind));
}
