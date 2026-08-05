using global::Avalonia.Controls;
using global::Avalonia.Platform.Storage;
using AAML.Application.Common;

namespace AAML.Avalonia;

public interface IProfileDocumentTransfer
{
    Task<Result<string?>> OpenAsync(CancellationToken cancellationToken);
    Task<Result<string?>> OpenLegacyAsync(CancellationToken cancellationToken);
    Task<Result> SaveAsync(string suggestedName, string document, CancellationToken cancellationToken);
    Task<Result<(string Path, string Contents)?>> OpenLegacySettingsAsync(CancellationToken cancellationToken);
    Task<Result> SaveLegacyAsync(string suggestedName, string document, CancellationToken cancellationToken);
}

public sealed class AvaloniaProfileDocumentTransfer(Func<TopLevel?> topLevel) : IProfileDocumentTransfer
{
    private static readonly FilePickerFileType ProfileType = new("AAML profile") { Patterns = ["*.aamlprofile.json", "*.json"] };
    private static readonly FilePickerFileType LegacyProfileType = new("Legacy AML profile") { Patterns = ["*.txt"] };

    public Task<Result<string?>> OpenAsync(CancellationToken cancellationToken) => OpenAsync("Import AAML profile", ProfileType, cancellationToken);

    public Task<Result<string?>> OpenLegacyAsync(CancellationToken cancellationToken) => OpenAsync("Import legacy AML profile", LegacyProfileType, cancellationToken);

    public async Task<Result<(string Path, string Contents)?>> OpenLegacySettingsAsync(CancellationToken cancellationToken)
    {
        var owner = topLevel();
        if (owner is null) return Result<(string, string)?>.Failure(new Error("migration.window_unavailable", "The application window is unavailable.", ErrorKind.Unavailable));
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import legacy AML settings snapshots", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Legacy AML settings") { Patterns = ["settings.json", "*.json"] }] });
            if (files.Count == 0) return Result<(string, string)?>.Success(null);
            await using var stream = await files[0].OpenReadAsync(); using var reader = new StreamReader(stream);
            return Result<(string, string)?>.Success((files[0].Path.LocalPath, await reader.ReadToEndAsync(cancellationToken)));
        }
        catch (OperationCanceledException) { return Result<(string, string)?>.Failure(new Error("migration.import_cancelled", "Migration import was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Result<(string, string)?>.Failure(new Error("migration.import_failed", exception.Message, ErrorKind.Io)); }
    }

    private async Task<Result<string?>> OpenAsync(string title, FilePickerFileType fileType, CancellationToken cancellationToken)
    {
        var owner = topLevel();
        if (owner is null) return Result<string?>.Failure(new Error("profile.window_unavailable", "The application window is unavailable.", ErrorKind.Unavailable));
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [fileType] });
            if (files.Count == 0) return Result<string?>.Success(null);
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            return Result<string?>.Success(await reader.ReadToEndAsync(cancellationToken));
        }
        catch (OperationCanceledException) { return Result<string?>.Failure(new Error("profile.import_cancelled", "Profile import was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<string?>.Failure(new Error("profile.import_read_failed", exception.Message, ErrorKind.Io));
        }
    }

    public async Task<Result> SaveAsync(string suggestedName, string document, CancellationToken cancellationToken)
    {
        var owner = topLevel();
        if (owner is null) return Result.Failure(new Error("profile.window_unavailable", "The application window is unavailable.", ErrorKind.Unavailable));
        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export AAML profile", SuggestedFileName = suggestedName + ".aamlprofile.json", DefaultExtension = "json", FileTypeChoices = [ProfileType], ShowOverwritePrompt = true });
            if (file is null) return Result.Success();
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(document.AsMemory(), cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException) { return Result.Failure(new Error("profile.export_cancelled", "Profile export was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new Error("profile.export_write_failed", exception.Message, ErrorKind.Io));
        }
    }

    public async Task<Result> SaveLegacyAsync(string suggestedName, string document, CancellationToken cancellationToken)
    {
        var owner = topLevel();
        if (owner is null) return Result.Failure(new Error("profile.window_unavailable", "The application window is unavailable.", ErrorKind.Unavailable));
        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export legacy AML list", SuggestedFileName = suggestedName + ".txt", DefaultExtension = "txt", FileTypeChoices = [LegacyProfileType], ShowOverwritePrompt = true });
            if (file is null) return Result.Success();
            await using var stream = await file.OpenWriteAsync(); await using var writer = new StreamWriter(stream); await writer.WriteAsync(document.AsMemory(), cancellationToken); return Result.Success();
        }
        catch (OperationCanceledException) { return Result.Failure(new Error("profile.export_cancelled", "Profile export was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Result.Failure(new Error("profile.export_write_failed", exception.Message, ErrorKind.Io)); }
    }
}
