using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Domain.Games;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ExistingModRootAdoptionServiceTests
{
    [TestMethod]
    public async Task Apply_RevalidatesSourceAndAtomicallyPersistsOnlySelectedValidRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Adoption", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first-root"); var second = Path.Combine(root, "second-root"); var existing = Path.Combine(root, "existing-root");
        try
        {
            Directory.CreateDirectory(first); Directory.CreateDirectory(second); Directory.CreateDirectory(existing);
            var preview = Preview([new(0, "first", first, 3, ExistingModRootResolution.Valid), new(1, "second", second, 4, ExistingModRootResolution.Valid), new(2, "missing", null, 5, ExistingModRootResolution.Missing)]);
            var source = new FakeSource(preview);
            var repository = new RecordingRepository();
            var bootstrapper = new SettingsBootstrapper(repository, new NoLegacyImporter());
            var settings = Settings(existing);

            var result = await new ExistingModRootAdoptionService(source, bootstrapper).ApplyAsync(preview, new HashSet<int> { 1 }, settings, TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue(result.Error?.Message);
            result.Value!.ModRootLocations.Should().Equal(existing, second);
            repository.Saved.Should().Be(result.Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Apply_RejectsInvalidSelectionAndChangedSource()
    {
        var invalid = Preview([new(0, "missing", null, 3, ExistingModRootResolution.Missing)]);
        var repository = new RecordingRepository();
        var bootstrapper = new SettingsBootstrapper(repository, new NoLegacyImporter());
        var invalidResult = await new ExistingModRootAdoptionService(new FakeSource(invalid), bootstrapper).ApplyAsync(invalid, new HashSet<int> { 0 }, Settings(), TestContext.CancellationToken);
        invalidResult.Error!.Code.Should().Be("mod_roots.selection_invalid");

        var changed = invalid with { SourceFingerprint = "changed" };
        var staleResult = await new ExistingModRootAdoptionService(new FakeSource(changed), bootstrapper).ApplyAsync(invalid, new HashSet<int>(), Settings(), TestContext.CancellationToken);
        staleResult.Error!.Code.Should().Be("mod_roots.preview_stale");
        repository.Saved.Should().BeNull();
    }

    [TestMethod]
    public void Guard_BlocksOnlyPreviewedVariantUntilExplicitlyCleared()
    {
        var guard = new ExistingModRootPreviewGuard();
        guard.Register(Preview([new(0, "missing", null, 3, ExistingModRootResolution.Missing)]));
        guard.EnsureConfigurationSafe(GameVariant.XCom2).Error!.Code.Should().Be("mod_roots.preview_unconfirmed");
        guard.EnsureConfigurationSafe(GameVariant.ChimeraSquad).IsSuccess.Should().BeTrue();
        guard.Clear();
        guard.EnsureConfigurationSafe(GameVariant.XCom2).IsSuccess.Should().BeTrue();
    }

    public TestContext TestContext { get; set; }
    private static ApplicationSettings Settings(params string[] roots) => new(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, "game", roots, [], [], [], []);
    private static ExistingModRootPreview Preview(IReadOnlyList<ExistingModRootRow> rows) => new(GameVariant.XCom2, "game", "XComEngine.ini", "same", "test", rows, "report");

    private sealed class FakeSource(ExistingModRootPreview preview) : ILegacyGameConfigurationSource
    {
        public Task<Result<ExistingModRootPreview>> ReadModRootsAsync(GameVariant variant, string? installationLocation, IReadOnlyList<string> configuredRoots, CancellationToken cancellationToken) => Task.FromResult(Result<ExistingModRootPreview>.Success(preview));
        public Task<Result<IReadOnlyList<ActiveModSource>>> ReadActiveModsAsync(GameVariant variant, string? installationLocation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<ObsoleteOverridePreview>> PreviewOverrideCleanupAsync(GameVariant variant, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> ApplyOverrideCleanupAsync(ObsoleteOverridePreview preview, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRepository : ISettingsRepository
    {
        public ApplicationSettings? Saved { get; private set; }
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) { Saved = settings; return Task.FromResult(Result.Success()); }
    }
    private sealed class NoLegacyImporter : ILegacySettingsImporter { public Task<Result<ApplicationSettings?>> TryImportAsync(CancellationToken cancellationToken) => Task.FromResult(Result<ApplicationSettings?>.Success(null)); }
}
