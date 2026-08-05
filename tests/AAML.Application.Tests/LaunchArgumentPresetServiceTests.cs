using AAML.Application.Launching;
using AAML.Domain.Games;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class LaunchArgumentPresetServiceTests
{
    [TestMethod]
    public void BuiltIns_MatchVerifiedLegacyCatalogInCanonicalOrder()
    {
        var service = CreateService();

        service.BuiltIns.Select(preset => (preset.Id, preset.ArgumentTemplate, preset.RequiresValue, preset.IsAdvanced)).Should().Equal(
            ("review", "-review", false, false),
            ("no-red-screens", "-noRedScreens", false, false),
            ("log", "-log", false, false),
            ("crash-dump-watcher", "-crashDumpWatcher", false, true),
            ("skip-startup-movies", "-noStartUpMovies", false, false),
            ("language", "-language=", true, false),
            ("allow-console", "-allowConsole", false, false),
            ("auto-debug", "-autoDebug", false, true),
            ("no-seek-free-loading", "-noSeekFreeLoading", false, true),
            ("regenerate-inis", "-regenerateinis", false, true));
        service.BuiltIns.Should().OnlyContain(preset => !preset.IsImported);
        service.BuiltIns.Single(preset => preset.Id == "regenerate-inis").Description.Should().Contain("Resets generated game configuration");
    }

    [TestMethod]
    public void BuiltIns_ExposeEveryGameAndModernChallengeModeConsoleRestriction()
    {
        var presets = CreateService().BuiltIns;

        foreach (var game in Enum.GetValues<GameVariant>())
        {
            presets.Where(preset => preset.Id != "allow-console").Should().OnlyContain(preset => preset.AppliesTo(game));
        }
        presets.Single(preset => preset.Id == "allow-console").ApplicableGames.Should().BeEquivalentTo(
            new[] { GameVariant.XCom2, GameVariant.XCom2WarOfTheChosen, GameVariant.ChimeraSquad });
    }

    [TestMethod]
    public async Task ValidReport_AddsCustomSuggestionsButDeduplicatesBuiltInsAliasesAndCasing()
    {
        var service = CreateService(Document(["-LOG", "-noRedscreens", "-customFlag", "-CUSTOMFLAG"]));

        var catalog = await service.LoadAsync(TestContext.CancellationToken);

        catalog.Presets.Should().HaveCount(11);
        var imported = catalog.Presets.Single(preset => preset.IsImported);
        imported.ArgumentTemplate.Should().Be("-customFlag");
        imported.FriendlyName.Should().Be("-customFlag");
        imported.Description.Should().Contain("imported");
        imported.ApplicableGames.Should().BeEquivalentTo(Enum.GetValues<GameVariant>());
        catalog.Presets.Should().OnlyHaveUniqueItems(preset => preset.ArgumentTemplate.ToUpperInvariant());
    }

    [TestMethod]
    public async Task InvalidSchemaOrSource_IgnoresEverySuggestionWithDiagnostic()
    {
        var invalidSchema = CreateService(Document(["-custom"], schema: 2));
        var invalidSource = CreateService(Document(["-custom"], sourceSha: "not-a-sha"));

        var schemaCatalog = await invalidSchema.LoadAsync(TestContext.CancellationToken);
        var sourceCatalog = await invalidSource.LoadAsync(TestContext.CancellationToken);

        schemaCatalog.Presets.Should().HaveCount(10);
        schemaCatalog.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "launch_presets.unsupported_report_schema");
        sourceCatalog.Presets.Should().HaveCount(10);
        sourceCatalog.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "launch_presets.invalid_report_source");
    }

    [TestMethod]
    public async Task MalformedCustomSuggestions_AreIgnoredWithoutRejectingValidEntries()
    {
        var service = CreateService(Document(["", "value", "-two words", "-valid"]));

        var catalog = await service.LoadAsync(TestContext.CancellationToken);

        catalog.Presets.Should().ContainSingle(preset => preset.IsImported && preset.ArgumentTemplate == "-valid");
        catalog.Diagnostics.Count(diagnostic => diagnostic.Code == "launch_presets.invalid_custom_suggestion").Should().Be(3);
    }

    [TestMethod]
    public void ParameterizedAndCanonicalEquivalence_AreExactAndCaseInsensitive()
    {
        var presets = CreateService().BuiltIns;
        var language = presets.Single(preset => preset.Id == "language");
        var redScreens = presets.Single(preset => preset.Id == "no-red-screens");

        language.Format().Should().BeNull();
        language.Format(" INT ").Should().Be("-language=INT");
        language.Matches("-LANGUAGE=fr").Should().BeTrue();
        language.Matches("-language=").Should().BeFalse();
        redScreens.Matches("-noRedscreens").Should().BeTrue();
    }

    public TestContext TestContext { get; set; }

    private static LaunchArgumentPresetService CreateService(LegacyLaunchArgumentSuggestionDocument? document = null)
    {
        var repository = new StubRepository(new(document, []));
        return new(repository);
    }

    private static LegacyLaunchArgumentSuggestionDocument Document(IReadOnlyList<string> arguments, int schema = 1, string? sourceSha = null) =>
        new(schema, sourceSha ?? new string('a', 64), true, arguments);

    private sealed class StubRepository(LegacyLaunchArgumentSuggestionReadResult result) : ILegacyLaunchArgumentSuggestionRepository
    {
        public Task<LegacyLaunchArgumentSuggestionReadResult> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
