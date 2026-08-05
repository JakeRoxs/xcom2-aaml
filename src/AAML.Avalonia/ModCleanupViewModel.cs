using AAML.Application.Mods.Cleanup;
using CSharpFunctionalExtensions;
using ReactiveUI;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell.Utils;

namespace AAML.Avalonia;

[Section("cleanup", "fa-broom", 7, FriendlyName = "Cleanup")]
public sealed class ModCleanupViewModel : ReactiveObject, IDisposable
{
    private readonly ApplicationSession session;
    private readonly IModCleanupService cleanup;
    private ModCleanupPreview? preview;
    private CancellationTokenSource? operation;
    private SourceCleanupPolicy sourcePolicy = SourceCleanupPolicy.XComGameOnly;
    private ShaderCleanupPolicy shaderPolicy = ShaderCleanupPolicy.EmptyLegacyCacheOnly;
    private bool includeWorkshop;
    private string report = "Choose policies and preview. No files are changed until confirmation.";

    public ModCleanupViewModel(ApplicationSession session, IModCleanupService cleanup)
    {
        this.session = session; this.cleanup = cleanup;
        Preview = ReactiveCommand.CreateFromTask(PreviewAsync).Enhance(text: "Preview cleanup", name: "PreviewModCleanup");
        Confirm = ReactiveCommand.CreateFromTask(ConfirmAsync).Enhance(text: "Confirm cleanup", name: "ConfirmModCleanup");
        Cancel = ReactiveCommand.Create(() => { operation?.Cancel(); return Result.Success(); }).Enhance(text: "Cancel cleanup", name: "CancelModCleanup");
    }
    public IReadOnlyList<SourceCleanupPolicy> SourcePolicies { get; } = Enum.GetValues<SourceCleanupPolicy>();
    public IReadOnlyList<ShaderCleanupPolicy> ShaderPolicies { get; } = Enum.GetValues<ShaderCleanupPolicy>();
    public SourceCleanupPolicy SourcePolicy { get => sourcePolicy; set { this.RaiseAndSetIfChanged(ref sourcePolicy, value); Invalidate(); } }
    public ShaderCleanupPolicy ShaderPolicy { get => shaderPolicy; set { this.RaiseAndSetIfChanged(ref shaderPolicy, value); Invalidate(); } }
    public bool IncludeWorkshop { get => includeWorkshop; set { this.RaiseAndSetIfChanged(ref includeWorkshop, value); Invalidate(); } }
    public string Report { get => report; private set => this.RaiseAndSetIfChanged(ref report, value); }
    public IEnhancedCommand<Result> Preview { get; } public IEnhancedCommand<Result> Confirm { get; } public IEnhancedCommand<Result> Cancel { get; }

    private async Task<Result> PreviewAsync()
    {
        if (session.Settings is null) return Result.Failure("AAML is not initialized.");
        operation?.Dispose(); operation = new CancellationTokenSource();
        var result = await cleanup.PreviewAsync(new(session.DiscoveredMods, SourcePolicy, ShaderPolicy, IncludeWorkshop, session.Settings.ModRootLocations), operation.Token);
        if (!result.IsSuccess) return Result.Failure(result.Error!.Message);
        preview = result.Value; Report = Format(preview!); return Result.Success();
    }
    private async Task<Result> ConfirmAsync()
    {
        if (preview is null) return Result.Failure("Preview cleanup before confirming.");
        operation?.Dispose(); operation = new CancellationTokenSource();
        var result = await cleanup.ExecuteAsync(preview, operation.Token); preview = null;
        if (!result.IsSuccess) return Result.Failure(result.Error!.Message);
        Report = string.Join(Environment.NewLine, result.Value!.Items.Select(item => $"{item.Outcome}: {item.Message}"));
        await session.RefreshModsAsync(CancellationToken.None); return Result.Success();
    }
    private void Invalidate() { preview = null; Report = "Policies changed. Preview again before confirmation."; }
    private static string Format(ModCleanupPreview preview) => $"Cleanup preview expires {preview.ExpiresAt.LocalDateTime:g}\nReady: {preview.Items.Count(item => item.Disposition == CleanupDisposition.Ready)}, skipped/rejected: {preview.Items.Count(item => item.Disposition != CleanupDisposition.Ready)}\nFiles: {preview.Items.Sum(item => item.FileCount)}, directories: {preview.Items.Sum(item => item.DirectoryCount)}, bytes: {preview.Items.Sum(item => item.TotalBytes):N0}\n" + string.Join('\n', preview.Items.Select(item => $"{item.ModName} | {item.Kind} | {item.RelativePath} | {item.Disposition} | {item.Message}"));
    public void Dispose() { operation?.Cancel(); operation?.Dispose(); }
}
