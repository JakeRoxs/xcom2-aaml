using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;

namespace AAML.Application.Updates;

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, NoEligibleRelease }
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string CurrentVersion, ReleaseInfo? Release, string Message);

public interface IUpdateCheckService
{
    Task<Result<UpdateCheckResult>> CheckAsync(UpdateChannelPreference preference, CancellationToken cancellationToken);
}

public sealed class UpdateCheckService(IReleaseService releases, IApplicationVersionProvider versionProvider) : IUpdateCheckService
{
    public async Task<Result<UpdateCheckResult>> CheckAsync(UpdateChannelPreference preference, CancellationToken cancellationToken)
    {
        var currentText = versionProvider.GetCurrentVersion();
        if (!currentText.IsSuccess) return Result<UpdateCheckResult>.Failure(currentText.Error!);
        if (!TryParse(currentText.Value!, out var current)) return Result<UpdateCheckResult>.Failure(new Error("update.current_version_invalid", $"The current application version is not semantic: {currentText.Value}.", ErrorKind.InvalidData));
        var loaded = await releases.GetLatestAsync(preference == UpdateChannelPreference.Stable ? ReleaseChannel.Stable : ReleaseChannel.IncludePrerelease, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Result<UpdateCheckResult>.Failure(loaded.Error!);
        if (loaded.Value is null) return Result<UpdateCheckResult>.Success(new(UpdateCheckStatus.NoEligibleRelease, currentText.Value!, null, "No eligible published release was found."));
        if (!TryParse(loaded.Value.TagName, out var remote)) return Result<UpdateCheckResult>.Failure(new Error("update.release_version_invalid", "The published release tag is not a valid semantic version.", ErrorKind.InvalidData));
        if (preference == UpdateChannelPreference.Stable && remote.IsPrerelease) return Result<UpdateCheckResult>.Success(new(UpdateCheckStatus.NoEligibleRelease, currentText.Value!, null, "No stable release was found."));
        if (preference == UpdateChannelPreference.Prerelease && remote.Prerelease.Any(label => label.Contains("alpha", StringComparison.OrdinalIgnoreCase))) return Result<UpdateCheckResult>.Success(new(UpdateCheckStatus.NoEligibleRelease, currentText.Value!, null, "The latest release is outside the selected channel."));
        return remote.CompareTo(current) > 0
            ? Result<UpdateCheckResult>.Success(new(UpdateCheckStatus.UpdateAvailable, currentText.Value!, loaded.Value, $"AAML {remote} is available."))
            : Result<UpdateCheckResult>.Success(new(UpdateCheckStatus.UpToDate, currentText.Value!, loaded.Value, "AAML is up to date."));
    }

    private static bool TryParse(string value, out SemanticVersion version)
    {
        var candidate = value.Trim(); if (candidate.StartsWith('v') || candidate.StartsWith('V')) candidate = candidate[1..];
        var metadataParts = candidate.Split('+');
        if (metadataParts.Length > 2) { version = default; return false; }
        var versionParts = metadataParts[0].Split('-', 2);
        var core = versionParts[0].Split('.');
        if (core.Length != 3 || core.Any(part => !ValidNumber(part))) { version = default; return false; }
        var prerelease = versionParts.Length == 1 ? [] : versionParts[1].Split('.');
        if (prerelease.Any(part => part.Length == 0 || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') || char.IsDigit(part[0]) && part.All(char.IsDigit) && part.Length > 1 && part[0] == '0')) { version = default; return false; }
        version = new(int.Parse(core[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(core[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(core[2], System.Globalization.CultureInfo.InvariantCulture), prerelease);
        return true;
    }

    private static bool ValidNumber(string value) => value.Length > 0 && value.All(char.IsDigit) && (value.Length == 1 || value[0] != '0') && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, IReadOnlyList<string> Prerelease) : IComparable<SemanticVersion>
    {
        public bool IsPrerelease => Prerelease.Count > 0;
        public int CompareTo(SemanticVersion other)
        {
            var core = Major.CompareTo(other.Major); if (core != 0) return core;
            core = Minor.CompareTo(other.Minor); if (core != 0) return core;
            core = Patch.CompareTo(other.Patch); if (core != 0) return core;
            if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
            for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
            {
                var leftNumeric = int.TryParse(Prerelease[index], out var left); var rightNumeric = int.TryParse(other.Prerelease[index], out var right);
                var comparison = leftNumeric && rightNumeric ? left.CompareTo(right) : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(Prerelease[index], other.Prerelease[index]);
                if (comparison != 0) return comparison;
            }
            return Prerelease.Count.CompareTo(other.Prerelease.Count);
        }
        public override string ToString() => $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + string.Join('.', Prerelease) : string.Empty);
    }
}
