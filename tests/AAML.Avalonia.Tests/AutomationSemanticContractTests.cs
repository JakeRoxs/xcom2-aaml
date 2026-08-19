using System.Xml.Linq;
using FluentAssertions;

namespace AAML.Avalonia.Tests;

[TestClass]
public sealed class AutomationSemanticContractTests
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredIds = new Dictionary<string, string[]>
    {
        ["DashboardView.axaml"] = ["DashboardPage", "DashboardGamePathTextBox", "DashboardLaunchArgumentsTextBox", "DashboardLaunchPresetPicker", "DashboardLaunchPresetDiagnostics", "DashboardWorkshopPolicyCombo", "DashboardTextScale", "DashboardIconScale", "DashboardResetAccessibilitySizing", "DashboardAutoSaveToggle", "DashboardDetectSteamButton", "DashboardSavePreferencesButton", "DashboardLaunchButton", "DashboardStatus"],
        ["ModsView.axaml"] = ["ModsPage", "ModsRefreshButton", "ModsSearchTextBox", "ModsGrid", "ModsActiveCheckBox", "ModsGroupToggle", "ModsCopySelected", "ModsCopyNamesButton", "ModsCopyPathsButton", "ModsCopyWorkshopButton", "ModsCopyReportButton", "ModsDangerZone", "ModsStatus"],
        ["ConflictsView.axaml"] = ["ConflictsPage", "ConflictsRefreshButton", "ConflictsSearchTextBox", "ConflictsStatus"],
        ["ConfigurationsView.axaml"] = ["ConfigurationsPage", "ConfigurationsOpenButton", "ConfigurationsEditor", "ConfigurationsRefreshButton", "ConfigurationsStatus"],
        ["ProfilesView.axaml"] = ["ProfilesPage", "ProfilesNameTextBox", "ProfilesCreateButton", "ProfilesApplyButton", "ProfilesConfirmLegacyButton", "ProfilesStatus"],
        ["MigrationView.axaml"] = ["MigrationPage", "MigrationPreviewActiveModsButton", "MigrationConfirmActiveModsButton", "MigrationPreviewModRootsButton", "MigrationConfirmModRootsButton", "MigrationModRootList", "MigrationReport"],
        ["SupportView.axaml"] = ["SupportPage", "SupportCheckUpdatesButton", "SupportCopyReportButton", "SupportUpdateStatus", "SupportOpenGameInstallationButton", "SupportOpenGameUserDataButton", "SupportOpenGameConfigurationButton", "SupportOpenGameLogButton"],
        ["ModCleanupView.axaml"] = ["CleanupPage", "CleanupPreviewButton", "CleanupConfirmButton", "CleanupReport"]
    };

    [TestMethod]
    public void PageRootsAndSafeAutomationTargetsHaveStableIds()
    {
        var documents = LoadViews();

        documents.Keys.Should().BeEquivalentTo(RequiredIds.Keys);
        foreach (var (file, requiredIds) in RequiredIds)
        {
            var document = documents[file];
            Id(document.Root!).Should().Be(requiredIds[0], $"{file} must expose its stable page-root ID");
            var actualIds = document.Root!.DescendantsAndSelf().Select(Id).Where(id => id is not null).ToArray();
            actualIds.Should().Contain(requiredIds, $"{file} must retain its safe automation contract");
        }
    }

    [TestMethod]
    public void AutomationIdsAreUniqueAcrossAllPages()
    {
        var ids = LoadViews().Values
            .SelectMany(document => document.Root!.DescendantsAndSelf())
            .Select(Id)
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray();

        ids.Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public void WorkshopAndShellControlsDoNotGenerateUnusedNamedFields()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var views = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Views"), "*View.axaml")
            .ToDictionary(path => Path.GetFileName(path)!, XDocument.Load, StringComparer.Ordinal);

        views["ModsView.axaml"].Descendants().Select(element => (string?)element.Attribute(x + "Name"))
            .Should().NotContain(["ModUpdateSummary", "WorkshopAggregateProgress", "RefreshWorkshopButton", "UpdateSelectedModsButton", "StopWorkshopMonitoringButton"]);
        views["ModsView.axaml"].Descendants().Select(element => (string?)element.Attribute(x + "Name"))
            .Should().Contain("ModsGrid", "the selection-preservation code-behind still uses this field");
        views["AamlShellView.axaml"].Descendants().Select(element => (string?)element.Attribute(x + "Name"))
            .Should().NotContain("ShellSectionStrip");
    }

    [TestMethod]
    public void ModsPageKeepsRareAndDestructiveActionsCollapsedAndConfirmationGated()
    {
        var document = LoadViews()["ModsView.axaml"];
        var expanders = document.Descendants().Where(element => element.Name.LocalName == "Expander").ToArray();

        expanders.Should().Contain(element => (string?)element.Attribute("Header") == "More actions" && (string?)element.Attribute("IsExpanded") == "False");
        expanders.Should().Contain(element => (string?)element.Attribute("Header") == "Manage categories and tags" && (string?)element.Attribute("IsExpanded") == "False");
        expanders.Should().Contain(element => (string?)element.Attribute("Header") == "Danger zone" && (string?)element.Attribute("IsExpanded") == "False");

        var dangerZone = expanders.Single(element => (string?)element.Attribute("Header") == "Danger zone");
        dangerZone.Descendants().Where(element => element.Name.LocalName == "CheckBox").Should().HaveCount(3);
        dangerZone.Descendants().Where(element => element.Name.LocalName == "Button")
            .All(button => button.Attribute("IsVisible") != null || button.Attribute("IsEnabled") != null)
            .Should().BeTrue("every destructive action must be applicability or confirmation gated");
    }

    [TestMethod]
    public void GlobalAutoSaveToggleBelongsOnlyToDashboardPreferences()
    {
        var documents = LoadViews();
        documents["DashboardView.axaml"].Descendants().Single(element => Id(element) == "DashboardAutoSaveToggle")
            .Attribute("Content")!.Value.Should().Be("Auto-save changes");
        documents["ModsView.axaml"].Descendants().Select(Id).Should().NotContain("DashboardAutoSaveToggle");
    }

    [TestMethod]
    public void DashboardPresetPickerIsCollapsedAndUsesTouchSizedRowsWithoutAButtonWall()
    {
        var document = LoadViews()["DashboardView.axaml"];
        var picker = document.Descendants().Single(element => Id(element) == "DashboardLaunchPresetPicker");

        picker.Attribute("IsExpanded")!.Value.Should().Be("False");
        picker.Descendants().Where(element => element.Name.LocalName == "Grid" && (string?)element.Attribute("MinHeight") == "48").Should().NotBeEmpty();
        picker.Descendants().Where(element => element.Name.LocalName == "CheckBox").Should().OnlyContain(element => (string?)element.Attribute("MinHeight") == "48");
        picker.Descendants().Should().NotContain(element => element.Name.LocalName == "Button");
    }

    [TestMethod]
    public void PreviewConfirmationsAndPlatformActionsAreStateGated()
    {
        var documents = LoadViews();
        var profiles = documents["ProfilesView.axaml"];
        var migration = documents["MigrationView.axaml"];
        var cleanup = documents["ModCleanupView.axaml"];

        profiles.Descendants().Single(element => Id(element) == "ProfilesConfirmLegacyButton")
            .Attribute("IsEnabled")!.Value.Should().Be("{Binding CanConfirmLegacy}");
        migration.Descendants().Single(element => Id(element) == "MigrationConfirmActiveModsButton")
            .Attribute("IsEnabled")!.Value.Should().Be("{Binding CanApplyActiveMods}");
        migration.Descendants().Single(element => Id(element) == "MigrationConfirmModRootsButton")
            .Attribute("IsEnabled")!.Value.Should().Be("{Binding CanApplyModRoots}");
        cleanup.Descendants().Single(element => Id(element) == "CleanupConfirmButton")
            .Attribute("IsEnabled")!.Value.Should().Be("{Binding CanConfirm}");
        migration.Descendants().Should().Contain(element => (string?)element.Attribute("IsVisible") == "{Binding CanPreviewActiveMods}");
        migration.Descendants().Should().Contain(element => (string?)element.Attribute("IsVisible") == "{Binding CanPreviewOverrideCleanup}");
        migration.Descendants().Should().Contain(element => (string?)element.Attribute("Text") == "{Binding CapabilityGuidance}");
    }

    [TestMethod]
    public void ModGroupsExposeRowLocalRenderedExpansionState()
    {
        var toggle = LoadViews()["ModsView.axaml"].Descendants().Single(element => Id(element) == "ModsGroupToggle");

        toggle.Attribute("IsVisible")!.Value.Should().Be("{Binding IsGroup}");
        toggle.Attribute("Content")!.Value.Should().Be("{Binding GroupToggleLabel}");
        toggle.Attribute("CommandParameter")!.Value.Should().Be("{Binding GroupKey}");
        toggle.Attribute("Command")!.Value.Should().Contain("ToggleGroup");
    }

    [TestMethod]
    public void SelectedModCopyActionsRemainContextualCollapsedAndTouchSized()
    {
        var document = LoadViews()["ModsView.axaml"];
        var copy = document.Descendants().Single(element => Id(element) == "ModsCopySelected");
        var buttons = copy.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();

        copy.Attribute("IsExpanded")!.Value.Should().Be("False");
        copy.Ancestors().Should().Contain(element => element.Name.LocalName == "Expander" && (string?)element.Attribute("Header") == "More actions");
        buttons.Should().HaveCount(4);
        buttons.Should().OnlyContain(button => (string?)button.Attribute("MinHeight") == "48");
        copy.Ancestors().Should().Contain(element => (string?)element.Attribute("IsVisible") == "{Binding HasSelection}");
    }

    [TestMethod]
    public void SupportDistinguishesEverySelectedGameLocationWithTouchSizedActions()
    {
        var document = LoadViews()["SupportView.axaml"];
        var expected = new Dictionary<string, string>
        {
            ["SupportOpenGameInstallationButton"] = "Installation",
            ["SupportOpenGameUserDataButton"] = "User data",
            ["SupportOpenGameConfigurationButton"] = "Generated configuration",
            ["SupportOpenGameLogButton"] = "Current log"
        };

        foreach (var (id, label) in expected)
        {
            var button = document.Descendants().Single(element => Id(element) == id);
            button.Attribute("Content")!.Value.Should().Be(label);
            button.Attribute("MinHeight")!.Value.Should().Be("48");
        }
        document.Descendants().Should().Contain(element => (string?)element.Attribute("Text") == "Selected game locations");
        document.Descendants().Any(element => (string?)element.Attribute("Text") is { } text && text.Contains("never creates", StringComparison.Ordinal)).Should().BeTrue();
    }

    [TestMethod]
    public void AccessibilitySizingUsesBoundedControlsAndSharedSemanticResources()
    {
        var dashboard = LoadViews()["DashboardView.axaml"];
        var text = dashboard.Descendants().Single(element => Id(element) == "DashboardTextScale");
        var icon = dashboard.Descendants().Single(element => Id(element) == "DashboardIconScale");
        text.Attribute("Minimum")!.Value.Should().Be("0.80");
        text.Attribute("Maximum")!.Value.Should().Be("1.50");
        icon.Attribute("Minimum")!.Value.Should().Be("0.75");
        icon.Attribute("Maximum")!.Value.Should().Be("1.50");
        dashboard.Descendants().Single(element => Id(element) == "DashboardResetAccessibilitySizing").Attribute("MinHeight")!.Value.Should().Be("48");

        var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Contracts", "App.axaml"));
        var resourceKeys = app.Descendants().SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "Key").Select(attribute => attribute.Value).ToArray();
        resourceKeys.Should().Contain(["AamlBodyFontSize", "AamlSmallFontSize", "AamlBadgeFontSize", "AamlSectionTitleFontSize", "AamlPageTitleFontSize", "AamlGridRowHeight", "AamlShellIconSize"]);

        var shell = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Views", "AamlShellView.axaml"));
        shell.ToString().Should().Contain("{DynamicResource AamlShellIconSize}");
        foreach (var document in LoadViews().Values)
            document.Descendants().Attributes("FontSize").Any(attribute => decimal.TryParse(attribute.Value, out _)).Should().BeFalse("common views must use semantic font resources");
    }

    private static Dictionary<string, XDocument> LoadViews()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Views");
        return Directory.GetFiles(directory, "*View.axaml")
            .Where(path => RequiredIds.ContainsKey(Path.GetFileName(path)))
            .ToDictionary(path => Path.GetFileName(path), XDocument.Load, StringComparer.Ordinal);
    }

    private static string? Id(XElement element) => element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName.EndsWith(".AutomationId", StringComparison.Ordinal))?.Value;
}
