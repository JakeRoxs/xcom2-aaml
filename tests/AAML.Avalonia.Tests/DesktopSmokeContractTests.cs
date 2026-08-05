using FluentAssertions;

namespace AAML.Avalonia.Tests;

[TestClass]
public sealed class DesktopSmokeContractTests
{
    private static readonly string[] Sections =
    [
        "Dashboard", "Mods", "Conflicts", "Configurations", "Profiles", "Migration", "Support", "Cleanup"
    ];

    [TestMethod]
    public void ShellAndHarnessShareStableNavigationIdsForEverySection()
    {
        var app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "App.axaml.cs"));
        var templates = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "App.axaml"));
        var harness = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "run-windows-desktop-smoke.ps1"));

        foreach (var section in Sections)
        {
            var id = $"ShellSection{section}";
            app.Should().Contain($"\"{id}\"");
            harness.Should().Contain($"'{id}'");
            templates.Should().Contain($"DataType=\"local:{(section == "Cleanup" ? "ModCleanup" : section)}ViewModel\"");
        }
    }

    [TestMethod]
    public void HarnessRetainsIsolationIntegrityAndFailureEvidenceGates()
    {
        var harness = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "run-windows-desktop-smoke.ps1"));

        harness.Should().Contain("Assert-Checksums $artifact");
        harness.Should().Contain("Assert-TreeEqual $artifactHashesBefore $artifactHashesAfter 'Staged artifact'");
        harness.Should().Contain("automation-tree-failure.json");
        harness.Should().Contain("artifactHashesBefore = $artifactHashesBefore");
        harness.Should().Contain("$process.CloseMainWindow()");
        harness.Should().NotContain("mouse_event");
        harness.Should().NotContain("SendKeys");
    }
}
