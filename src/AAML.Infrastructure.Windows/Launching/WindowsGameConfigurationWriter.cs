using System.Text;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Common.Configurations;

namespace AAML.Infrastructure.Windows.Launching;

/// <summary>Updates only AAML-owned Unreal INI keys in the exact Windows game user configuration directory.</summary>
public sealed class WindowsGameConfigurationWriter : IGameConfigurationWriter
{
    private readonly IAtomicTextWriter writer;
    private readonly string documentsDirectory;

    public WindowsGameConfigurationWriter(IAtomicTextWriter writer)
        : this(writer, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) { }

    internal WindowsGameConfigurationWriter(IAtomicTextWriter writer, string documentsDirectory)
    {
        this.writer = writer;
        this.documentsDirectory = documentsDirectory;
    }

    public async Task<Result<GameConfigurationReceipt>> ApplyAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Variant == GameVariant.XCom2WarOfTheChosenChallengeMode)
            return Result<GameConfigurationReceipt>.Failure(new Error("configuration.variant_unsupported", "Challenge-mode configuration writing is not supported.", ErrorKind.Validation));
        var gameFolder = request.Variant switch
        {
            GameVariant.XCom2 => "XCOM2",
            GameVariant.XCom2WarOfTheChosen => "XCOM2 War of the Chosen",
            GameVariant.ChimeraSquad => "XCOM Chimera Squad",
            _ => throw new ArgumentOutOfRangeException(nameof(request.Variant))
        };
        var configDirectory = Path.Combine(documentsDirectory, "My Games", gameFolder, "XComGame", "Config");
        var modOptionsPath = Path.Combine(configDirectory, "XComModOptions.ini");
        var enginePath = Path.Combine(configDirectory, "XComEngine.ini");
        try
        {
            var modOptions = await ReadOrEmptyAsync(modOptionsPath, cancellationToken).ConfigureAwait(false);
            modOptions = UnrealIniUpdater.ReplaceValues(modOptions, "Engine.XComModOptions", "ActiveMods", request.ActiveMods.OrderBy(mod => mod.Order).Select(mod => mod.PackageId.Value));
            var engine = await ReadOrEmptyAsync(enginePath, cancellationToken).ConfigureAwait(false);
            engine = UnrealIniUpdater.ReplaceValues(engine, "Engine.DownloadableContentEnumerator", "ModRootDirs", request.ModRootLocations.Select(ToEngineModRoot));
            var modWrite = await writer.WriteAsync(modOptionsPath, modOptions, cancellationToken).ConfigureAwait(false);
            if (!modWrite.IsSuccess) return Result<GameConfigurationReceipt>.Failure(modWrite.Error!);
            var engineWrite = await writer.WriteAsync(enginePath, engine, cancellationToken).ConfigureAwait(false);
            return engineWrite.IsSuccess
                ? Result<GameConfigurationReceipt>.Success(new GameConfigurationReceipt(
                    [modOptionsPath, enginePath],
                    request.ActiveMods.OrderBy(mod => mod.Order).Select(mod => mod.PackageId).ToArray(),
                    request.ModRootLocations.ToArray()))
                : Result<GameConfigurationReceipt>.Failure(engineWrite.Error!);
        }
        catch (OperationCanceledException)
        {
            return Result<GameConfigurationReceipt>.Failure(new Error("configuration.cancelled", "Game configuration was cancelled.", ErrorKind.Cancelled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<GameConfigurationReceipt>.Failure(new Error("configuration.read_failed", exception.Message, ErrorKind.Io));
        }
    }

    private static Task<string> ReadOrEmptyAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? File.ReadAllTextAsync(path, cancellationToken) : Task.FromResult(string.Empty);

    private static string ToEngineModRoot(string root)
    {
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
