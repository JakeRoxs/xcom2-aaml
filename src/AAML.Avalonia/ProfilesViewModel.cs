using System.Collections.ObjectModel;
using CSharpFunctionalExtensions;
using ReactiveUI;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell.Utils;
using AAML.Application.Profiles;

namespace AAML.Avalonia;

[Section("profiles", "fa-layer-group", 4, FriendlyName = "Profiles")]
public sealed class ProfilesViewModel : ReactiveObject
{
    private readonly ApplicationSession session;
    private readonly IProfileDocumentTransfer transfer;
    private readonly AAML.Application.Profiles.IProfileRepository profileRepository;
    private readonly ILegacyProfileExportService legacyExport;
    private string profileName = string.Empty;
    private SessionProfile? selectedProfile;
    private LegacyProfilePreview? legacyPreview;
    private string legacyReport = string.Empty;

    public ProfilesViewModel(ApplicationSession session, IProfileDocumentTransfer transfer, AAML.Application.Profiles.IProfileRepository profileRepository, ILegacyProfileExportService legacyExport)
    {
        this.session = session;
        this.transfer = transfer;
        this.profileRepository = profileRepository;
        this.legacyExport = legacyExport;
        session.PropertyChanged += (_, _) => this.RaisePropertyChanged(nameof(Status));
        Refresh = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.RefreshProfilesAsync(CancellationToken.None))).Enhance(text: "Refresh profiles", name: "RefreshProfiles");
        Create = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.CreateProfileAsync(ProfileName, CancellationToken.None))).Enhance(text: "Create profile", name: "CreateProfile");
        Apply = ReactiveCommand.CreateFromTask(async () => SelectedProfile is null ? Result.Failure("Select a profile.") : ToCommand(await session.ApplyProfileAsync(SelectedProfile.Id, CancellationToken.None))).Enhance(text: "Apply profile", name: "ApplyProfile");
        Rename = ReactiveCommand.CreateFromTask(async () => SelectedProfile is null ? Result.Failure("Select a profile.") : ToCommand(await session.RenameProfileAsync(SelectedProfile.Id, ProfileName, CancellationToken.None))).Enhance(text: "Rename profile", name: "RenameProfile");
        Duplicate = ReactiveCommand.CreateFromTask(async () => SelectedProfile is null ? Result.Failure("Select a profile.") : ToCommand(await session.DuplicateProfileAsync(SelectedProfile.Id, ProfileName, CancellationToken.None))).Enhance(text: "Duplicate profile", name: "DuplicateProfile");
        Delete = ReactiveCommand.CreateFromTask(async () => SelectedProfile is null ? Result.Failure("Select a profile.") : ToCommand(await session.DeleteProfileAsync(SelectedProfile.Id, CancellationToken.None))).Enhance(text: "Delete profile", name: "DeleteProfile");
        Import = ReactiveCommand.CreateFromTask(ImportAsync).Enhance(text: "Import profile", name: "ImportProfile");
        ImportLegacy = ReactiveCommand.CreateFromTask(ImportLegacyAsync).Enhance(text: "Import legacy profile", name: "ImportLegacyProfile");
        ConfirmLegacyPortable = ReactiveCommand.CreateFromTask(() => ConfirmLegacyAsync(LegacyTaxonomyDisposition.ProfileMetadata)).Enhance(text: "Import with profile metadata", name: "ConfirmLegacyPortable");
        ConfirmLegacyAdopt = ReactiveCommand.CreateFromTask(() => ConfirmLegacyAsync(LegacyTaxonomyDisposition.AdoptIntoApplication)).Enhance(text: "Import and adopt taxonomy", name: "ConfirmLegacyAdopt");
        ConfirmLegacyIgnore = ReactiveCommand.CreateFromTask(() => ConfirmLegacyAsync(LegacyTaxonomyDisposition.Ignore)).Enhance(text: "Import identities only", name: "ConfirmLegacyIgnore");
        LoadMissing = ReactiveCommand.CreateFromTask(LoadMissingAsync).Enhance(text: "Load missing items", name: "LoadMissingProfileItems");
        SubscribeMissing = ReactiveCommand.CreateFromTask(async () => { var result = await session.SubscribeProfileItemsAsync(MissingItems.Select(item => item.WorkshopId).ToArray(), CancellationToken.None); if (result.IsSuccess) MissingItems.Clear(); return ToCommand(result); }).Enhance(text: "Subscribe missing items", name: "SubscribeMissingProfileItems");
        Export = ReactiveCommand.CreateFromTask(ExportAsync).Enhance(text: "Export profile", name: "ExportProfile");
        ExportLegacy = ReactiveCommand.CreateFromTask(ExportLegacyAsync).Enhance(text: "Export legacy list", name: "ExportLegacyProfile");
    }

    public ObservableCollection<SessionProfile> Profiles => session.Profiles;
    public string Status => session.Status;
    public string ProfileName { get => profileName; set => this.RaiseAndSetIfChanged(ref profileName, value); }
    public SessionProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedProfile, value);
            if (value is not null) ProfileName = value.Name;
        }
    }
    public IEnhancedCommand<Result> Refresh { get; }
    public IEnhancedCommand<Result> Create { get; }
    public IEnhancedCommand<Result> Apply { get; }
    public IEnhancedCommand<Result> Rename { get; }
    public IEnhancedCommand<Result> Duplicate { get; }
    public IEnhancedCommand<Result> Delete { get; }
    public IEnhancedCommand<Result> Import { get; }
    public IEnhancedCommand<Result> ImportLegacy { get; }
    public IEnhancedCommand<Result> ConfirmLegacyPortable { get; }
    public IEnhancedCommand<Result> ConfirmLegacyAdopt { get; }
    public IEnhancedCommand<Result> ConfirmLegacyIgnore { get; }
    public string LegacyReport { get => legacyReport; private set => this.RaiseAndSetIfChanged(ref legacyReport, value); }
    public IEnhancedCommand<Result> LoadMissing { get; }
    public IEnhancedCommand<Result> SubscribeMissing { get; }
    public ObservableCollection<SessionMissingProfileItem> MissingItems { get; } = [];
    public IEnhancedCommand<Result> Export { get; }
    public IEnhancedCommand<Result> ExportLegacy { get; }

    private async Task<Result> ImportAsync()
    {
        var opened = await transfer.OpenAsync(CancellationToken.None);
        if (!opened.IsSuccess) return Result.Failure(opened.Error!.Message);
        return opened.Value is null ? Result.Success() : ToCommand(await session.ImportProfileAsync(opened.Value, CancellationToken.None));
    }

    private async Task<Result> ImportLegacyAsync()
    {
        var opened = await transfer.OpenLegacyAsync(CancellationToken.None);
        if (!opened.IsSuccess) return Result.Failure(opened.Error!.Message);
        if (opened.Value is null) return Result.Success();
        var preview = session.PreviewLegacyProfile(opened.Value);
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        legacyPreview = preview.Value; LegacyReport = legacyPreview!.Report; return Result.Success();
    }

    private async Task<Result> ConfirmLegacyAsync(LegacyTaxonomyDisposition disposition)
    {
        if (legacyPreview is null) return Result.Failure("Preview a legacy profile before importing.");
        var result = await session.ImportLegacyProfileAsync(ProfileName, legacyPreview, disposition, CancellationToken.None);
        if (result.IsSuccess) legacyPreview = null; return ToCommand(result);
    }

    private async Task<Result> LoadMissingAsync()
    {
        if (SelectedProfile is null) return Result.Failure("Select a profile.");
        var result = await session.GetMissingProfileItemsAsync(SelectedProfile.Id, CancellationToken.None); if (!result.IsSuccess) return Result.Failure(result.Error!.Message);
        MissingItems.Clear(); foreach (var item in result.Value!) MissingItems.Add(item); return Result.Success();
    }

    private async Task<Result> ExportAsync()
    {
        if (SelectedProfile is null) return Result.Failure("Select a profile.");
        var exported = await session.ExportProfileAsync(SelectedProfile.Id, CancellationToken.None);
        if (!exported.IsSuccess) return Result.Failure(exported.Error!.Message);
        return ToCommand(await transfer.SaveAsync(SelectedProfile.Name, exported.Value!, CancellationToken.None));
    }

    private async Task<Result> ExportLegacyAsync()
    {
        if (SelectedProfile is null) return Result.Failure("Select a profile.");
        var profile = await profileRepository.GetAsync(SelectedProfile.Id, CancellationToken.None);
        if (!profile.IsSuccess) return Result.Failure(profile.Error!.Message);
        var exported = legacyExport.Export(profile.Value!, new(true, true, LegacyWorkshopIdStyle.Url));
        if (!exported.IsSuccess) return Result.Failure(exported.Error!.Message);
        LegacyReport = string.Join(Environment.NewLine, exported.Value!.Diagnostics);
        return ToCommand(await transfer.SaveLegacyAsync(SelectedProfile.Name, exported.Value.Contents, CancellationToken.None));
    }

    private static Result ToCommand(AAML.Application.Common.Result result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message);
}
