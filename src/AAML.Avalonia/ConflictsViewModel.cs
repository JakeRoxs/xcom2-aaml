using System.Collections.ObjectModel;
using CSharpFunctionalExtensions;
using ReactiveUI;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell;
using Zafiro.UI.Shell.Utils;

namespace AAML.Avalonia;

[Section("conflicts", "fa-triangle-exclamation", 2, FriendlyName = "Conflicts")]
public sealed class ConflictsViewModel : ReactiveObject
{
    private readonly ApplicationSession session;
    private readonly IHierarchicalShell shell;
    private string searchText = string.Empty;
    private SessionConflict? selectedConflict;

    public ConflictsViewModel(ApplicationSession session, IHierarchicalShell shell)
    {
        this.session = session;
        this.shell = shell;
        session.Conflicts.CollectionChanged += (_, _) => RefreshRows();
        session.PropertyChanged += (_, _) => this.RaisePropertyChanged(nameof(Status));
        Refresh = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.RefreshModsAsync(CancellationToken.None))).Enhance(text: "Refresh conflicts", name: "RefreshConflicts");
        ShowInMods = ReactiveCommand.Create(() =>
        {
            if (SelectedConflict is null) return Result.Failure("Select a conflict.");
            session.FocusMods(SelectedConflict.Participants.ToHashSet());
            shell.RootLevel.SelectedSection.Value = shell.RootLevel.Sections.Single(section => section.Id == "mods");
            return Result.Success();
        }).Enhance(text: "Show involved mods", name: "ShowConflictMods");
        RefreshRows();
    }

    public ObservableCollection<SessionConflict> Rows { get; } = [];
    public ObservableCollection<AAML.Application.Mods.Conflicts.ModConflictFact> Facts { get; } = [];
    public IEnhancedCommand<Result> Refresh { get; }
    public IEnhancedCommand<Result> ShowInMods { get; }
    public string Status => session.Status;
    public string SearchText { get => searchText; set { this.RaiseAndSetIfChanged(ref searchText, value); RefreshRows(); } }
    public SessionConflict? SelectedConflict
    {
        get => selectedConflict;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedConflict, value);
            Facts.Clear();
            if (value is not null) foreach (var fact in value.Facts) Facts.Add(fact);
        }
    }

    private void RefreshRows()
    {
        Rows.Clear();
        foreach (var conflict in session.Conflicts.Where(conflict => string.IsNullOrWhiteSpace(SearchText) || conflict.Subject.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) || conflict.ParticipantsText.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)))
            Rows.Add(conflict);
        this.RaisePropertyChanged(nameof(Status));
    }

    private static Result ToCommand(AAML.Application.Common.Result result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message);
}
