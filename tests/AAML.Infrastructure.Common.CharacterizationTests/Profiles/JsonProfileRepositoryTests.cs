using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using AAML.Infrastructure.Common.Files;
using AAML.Infrastructure.Common.Profiles;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Profiles;

[TestClass]
public sealed class JsonProfileRepositoryTests
{
    [TestMethod]
    public async Task SaveListGetDelete_AreAtomicPortableAndTelemetryFree()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Profiles Ω", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        var repository = new JsonProfileRepository(paths, new AtomicTextWriter());
        var profile = Profile("Campaign");
        try
        {
            (await repository.SaveAsync(profile, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.GetAsync(profile.Id, TestContext.CancellationToken)).Value.Should().BeEquivalentTo(profile);
            (await repository.ListAsync(TestContext.CancellationToken)).Value.Should().ContainSingle();
            var path = Path.Combine(paths.DataDirectory, "Profiles", "profiles.json");
            var json = await File.ReadAllTextAsync(path, TestContext.CancellationToken);
            json.Should().Contain("630044970").And.NotContain("G:\\\\SteamLibrary").And.NotContainEquivalentOf("telemetry").And.NotContainEquivalentOf("sentry");
            (await repository.DeleteAsync(profile.Id, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.ListAsync(TestContext.CancellationToken)).Value.Should().BeEmpty();
            File.Exists(path + ".bak").Should().BeTrue();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DuplicateName_IsRejectedWithoutReplacingExistingProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Profiles", Guid.NewGuid().ToString("N"));
        var repository = new JsonProfileRepository(new TestPaths(root), new AtomicTextWriter());
        try
        {
            await repository.SaveAsync(Profile("Campaign"), TestContext.CancellationToken);

            var duplicate = await repository.SaveAsync(Profile("campaign"), TestContext.CancellationToken);

            duplicate.Error!.Code.Should().Be("profile.name_conflict");
            (await repository.ListAsync(TestContext.CancellationToken)).Value.Should().ContainSingle();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
    private static ModProfile Profile(string name) => new(new ProfileId(Guid.NewGuid()), name, GameVariant.XCom2WarOfTheChosen,
        [new ProfileModEntry(ModSource.SteamWorkshop, new PackageId("AllRegionLinks"), new WorkshopId(630044970), 0)],
        [new LaunchArgument("-review"), new LaunchArgument("-noRedScreens")], DateTimeOffset.Parse("2026-07-18T20:00:00Z"), DateTimeOffset.Parse("2026-07-18T20:00:00Z"));

    private sealed class TestPaths(string root) : AAML.Application.Ports.IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
