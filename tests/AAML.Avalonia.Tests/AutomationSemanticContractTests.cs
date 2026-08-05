using System.Xml.Linq;
using FluentAssertions;

namespace AAML.Avalonia.Tests;

[TestClass]
public sealed class AutomationSemanticContractTests
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredIds = new Dictionary<string, string[]>
    {
        ["DashboardView.axaml"] = ["DashboardPage", "DashboardGamePathTextBox", "DashboardLaunchArgumentsTextBox", "DashboardDetectSteamButton", "DashboardSavePreferencesButton", "DashboardLaunchButton", "DashboardStatus"],
        ["ModsView.axaml"] = ["ModsPage", "ModsRefreshButton", "ModsSearchTextBox", "ModsGrid", "ModsActiveCheckBox", "ModsDangerZone", "ModsStatus"],
        ["ConflictsView.axaml"] = ["ConflictsPage", "ConflictsRefreshButton", "ConflictsSearchTextBox", "ConflictsStatus"],
        ["ConfigurationsView.axaml"] = ["ConfigurationsPage", "ConfigurationsOpenButton", "ConfigurationsEditor", "ConfigurationsRefreshButton", "ConfigurationsStatus"],
        ["ProfilesView.axaml"] = ["ProfilesPage", "ProfilesNameTextBox", "ProfilesCreateButton", "ProfilesApplyButton", "ProfilesConfirmLegacyButton", "ProfilesStatus"],
        ["MigrationView.axaml"] = ["MigrationPage", "MigrationPreviewActiveModsButton", "MigrationConfirmActiveModsButton", "MigrationReport"],
        ["SupportView.axaml"] = ["SupportPage", "SupportCheckUpdatesButton", "SupportCopyReportButton", "SupportUpdateStatus"],
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

    private static Dictionary<string, XDocument> LoadViews()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Views");
        return Directory.GetFiles(directory, "*View.axaml")
            .Where(path => RequiredIds.ContainsKey(Path.GetFileName(path)))
            .ToDictionary(path => Path.GetFileName(path), XDocument.Load, StringComparer.Ordinal);
    }

    private static string? Id(XElement element) => element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName.EndsWith(".AutomationId", StringComparison.Ordinal))?.Value;
}
