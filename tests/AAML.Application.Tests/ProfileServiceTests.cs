using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Profiles;
using AAML.Application.Settings;
using AAML.Application.Mods.Dependencies;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ProfileServiceTests
{
    [TestMethod]
    public async Task Create_StoresPortableOrderedSnapshotWithoutInstallationPaths()
    {
        var first = Installation("G:\\Workshop\\630044970", "AllRegionLinks", 630044970);
        var second = Installation("D:\\Manual Mods\\Second", "Second", null);
        var settings = Settings([Intent(second, true, 0), Intent(first, true, 1)]);
        var profiles = new RecordingProfiles();

        var result = await new ProfileService(profiles, new RecordingSettings(), new SatisfiedDependencies()).CreateAsync(" Campaign ", settings, [first, second], TestContext.CancellationToken);

        result.Value!.Name.Should().Be("Campaign");
        result.Value.Mods.Select(mod => mod.PackageId.Value).Should().Equal("Second", "AllRegionLinks");
        result.Value.Mods.Should().OnlyContain(mod => !mod.PackageId.Value.Contains(':'));
        profiles.Saved.Should().Be(result.Value);
    }

    [TestMethod]
    public async Task Apply_UsesWorkshopIdentityAndPreservesNonProfileIntentMetadata()
    {
        var target = Installation("G:\\Moved Workshop\\630044970", "AllRegionLinks", 630044970);
        var previous = Installation("D:\\Manual\\Previous", "Previous", null);
        var previousIntent = Intent(previous, true, 0) with { IsHidden = true, Note = "Keep", Tags = new HashSet<TagId> { new("stable") } };
        var settings = Settings([previousIntent]);
        var profile = Profile([new ProfileModEntry(ModSource.SteamWorkshop, target.PackageId, target.WorkshopId, 0)]);
        var settingsRepository = new RecordingSettings();

        var result = await new ProfileService(new RecordingProfiles(profile), settingsRepository, new SatisfiedDependencies()).ApplyAsync(profile.Id, settings, [target, previous], TestContext.CancellationToken);

        result.Value!.Applied.Should().BeTrue();
        result.Value.Settings.ModIntents.Single(intent => intent.Mod == target.Key).IsActive.Should().BeTrue();
        result.Value.Settings.ModIntents.Single(intent => intent.Mod == previous.Key).Should().BeEquivalentTo(previousIntent with { IsActive = false, ExplicitOrder = null });
        settingsRepository.Saved.Should().Be(result.Value.Settings);
    }

    [TestMethod]
    public async Task ApplyMissingMod_ReturnsDiagnosticsWithoutChangingSettings()
    {
        var profile = Profile([new ProfileModEntry(ModSource.SteamWorkshop, new PackageId("Missing"), new WorkshopId(999), 0)]);
        var settings = Settings([]);
        var repository = new RecordingSettings();

        var result = await new ProfileService(new RecordingProfiles(profile), repository, new SatisfiedDependencies()).ApplyAsync(profile.Id, settings, [], TestContext.CancellationToken);

        result.Value!.Applied.Should().BeFalse();
        result.Value.Settings.Should().BeSameAs(settings);
        result.Value.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "profile.mod_missing");
        repository.Saved.Should().BeNull();
    }

    [TestMethod]
    public async Task RenameAndDuplicate_PreserveContentsWithDistinctIdentity()
    {
        var original = Profile([new ProfileModEntry(ModSource.SteamWorkshop, new PackageId("One"), new WorkshopId(1), 0)]);
        var profiles = new RecordingProfiles(original);
        var service = new ProfileService(profiles, new RecordingSettings(), new SatisfiedDependencies());

        var renamed = await service.RenameAsync(original.Id, "Renamed", TestContext.CancellationToken);
        var duplicate = await service.DuplicateAsync(original.Id, "Copy", TestContext.CancellationToken);

        renamed.Value!.Name.Should().Be("Renamed");
        duplicate.Value!.Id.Should().NotBe(original.Id);
        duplicate.Value.Mods.Should().BeEquivalentTo(original.Mods);
    }

    [TestMethod]
    public async Task ApplyDependencyFailure_ReturnsDiagnosticsWithoutSavingSettings()
    {
        var target = Installation("G:\\Workshop\\1", "Parent", 1);
        var entry = new ProfileModEntry(ModSource.SteamWorkshop, target.PackageId, target.WorkshopId, 0);
        var profile = Profile([entry]);
        var repository = new RecordingSettings();
        var issue = new ModDependencyIssue(new WorkshopId(1), new WorkshopId(2), ModDependencyIssueKind.Missing, [new WorkshopId(1), new WorkshopId(2)], "Required item 2 is missing.");

        var result = await new ProfileService(new RecordingProfiles(profile), repository, new SatisfiedDependencies(new ModDependencyReport([issue], new Dictionary<WorkshopId, IReadOnlyList<WorkshopId>>())))
            .ApplyAsync(profile.Id, Settings([]), [target], TestContext.CancellationToken);

        result.Value!.Applied.Should().BeFalse();
        result.Value.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "profile.dependency_missing");
        repository.Saved.Should().BeNull();
    }

    [TestMethod]
    public async Task ChimeraZeroModProfile_CreatesAndAppliesWithoutXcomContent()
    {
        var settings = Settings([]) with { SelectedGame = GameVariant.ChimeraSquad, GameInstallationLocation = "I:\\SteamLibrary\\steamapps\\common\\XCOM-Chimera-Squad", ModRootLocations = [] };
        var profiles = new RecordingProfiles();
        var service = new ProfileService(profiles, new RecordingSettings(), new SatisfiedDependencies());

        var created = await service.CreateAsync("Chimera Clean", settings, [], TestContext.CancellationToken);
        var applied = await service.ApplyAsync(created.Value!.Id, settings, [], TestContext.CancellationToken);

        created.Value.GameVariant.Should().Be(GameVariant.ChimeraSquad);
        created.Value.Mods.Should().BeEmpty();
        applied.Value!.Applied.Should().BeTrue();
        applied.Value.Settings.ModIntents.Should().BeEmpty();
    }

    public TestContext TestContext { get; set; }

    private static ApplicationSettings Settings(IReadOnlyList<ModUserIntent> intents) => new(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2WarOfTheChosen, "G:\\Game", ["G:\\Workshop"], ApplicationSettingsDefaults.LaunchArguments, intents, [], []);
    private static ModInstallation Installation(string path, string packageId, ulong? workshopId) => new(new ModKey(workshopId.HasValue ? ModSource.SteamWorkshop : ModSource.Manual, path), new PackageId(packageId), packageId, workshopId.HasValue ? new WorkshopId(workshopId.Value) : null, false, DescriptorState.Enabled, null);
    private static ModUserIntent Intent(ModInstallation mod, bool active, int? order) => new(mod.Key, active, false, order, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
    private static ModProfile Profile(IReadOnlyList<ProfileModEntry> mods) => new(new ProfileId(Guid.NewGuid()), "Profile", GameVariant.XCom2WarOfTheChosen, mods, ApplicationSettingsDefaults.LaunchArguments, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class RecordingProfiles(params ModProfile[] initial) : IProfileRepository
    {
        private readonly Dictionary<ProfileId, ModProfile> profiles = initial.ToDictionary(profile => profile.Id);
        public ModProfile? Saved { get; private set; }
        public Task<Result<IReadOnlyList<ModProfile>>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Result<IReadOnlyList<ModProfile>>.Success(profiles.Values.ToArray()));
        public Task<Result<ModProfile>> GetAsync(ProfileId id, CancellationToken cancellationToken) => Task.FromResult(profiles.TryGetValue(id, out var profile) ? Result<ModProfile>.Success(profile) : Result<ModProfile>.Failure(new Error("profile.not_found", "Missing.", ErrorKind.NotFound)));
        public Task<Result> AddAsync(ModProfile profile, CancellationToken cancellationToken) => SaveAsync(profile, cancellationToken);
        public Task<Result> SaveAsync(ModProfile profile, CancellationToken cancellationToken) { profiles[profile.Id] = profile; Saved = profile; return Task.FromResult(Result.Success()); }
        public Task<Result> DeleteAsync(ProfileId id, CancellationToken cancellationToken) { profiles.Remove(id); return Task.FromResult(Result.Success()); }
    }

    private sealed class RecordingSettings : ISettingsRepository
    {
        public ApplicationSettings? Saved { get; private set; }
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) { Saved = settings; return Task.FromResult(Result.Success()); }
    }

    private sealed class SatisfiedDependencies(ModDependencyReport? report = null) : IModDependencyService
    {
        public Task<Result<ModDependencyReport>> EvaluateAsync(IReadOnlyCollection<WorkshopId> roots, IReadOnlyCollection<WorkshopId> installed, IReadOnlyCollection<WorkshopId> active, IReadOnlyDictionary<WorkshopId, IReadOnlySet<WorkshopId>> ignored, CancellationToken cancellationToken) =>
            Task.FromResult(Result<ModDependencyReport>.Success(report ?? new ModDependencyReport([], new Dictionary<WorkshopId, IReadOnlyList<WorkshopId>>())));
    }
}
