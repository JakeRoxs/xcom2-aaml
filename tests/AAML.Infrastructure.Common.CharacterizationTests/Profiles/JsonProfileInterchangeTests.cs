using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using AAML.Infrastructure.Common.Files;
using AAML.Infrastructure.Common.Profiles;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Profiles;

[TestClass]
public sealed class JsonProfileInterchangeTests
{
    [TestMethod]
    public async Task ExportThenImport_IsPortableStrictAndConflictSafe()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "AAML Export", Guid.NewGuid().ToString("N"));
        var targetRoot = Path.Combine(Path.GetTempPath(), "AAML Import", Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new ModProfile(new ProfileId(Guid.NewGuid()), "Portable", GameVariant.XCom2WarOfTheChosen,
                [new ProfileModEntry(ModSource.SteamWorkshop, new PackageId("AllRegionLinks"), new WorkshopId(630044970), 0)],
                [new LaunchArgument("-review")], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var source = new JsonProfileRepository(new TestPaths(sourceRoot), new AtomicTextWriter());
            await source.SaveAsync(profile, TestContext.CancellationToken);
            var exported = await new JsonProfileInterchange(source).ExportAsync(profile.Id, TestContext.CancellationToken);
            var target = new JsonProfileRepository(new TestPaths(targetRoot), new AtomicTextWriter());

            var imported = await new JsonProfileInterchange(target).ImportAsync(exported.Value!, TestContext.CancellationToken);
            var conflict = await new JsonProfileInterchange(target).ImportAsync(exported.Value!, TestContext.CancellationToken);

            imported.Value.Should().BeEquivalentTo(profile);
            exported.Value.Should().NotContain("SteamLibrary").And.NotContainEquivalentOf("telemetry");
            conflict.Error!.Code.Should().Be("profile.id_conflict");
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, true);
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true);
        }
    }

    [TestMethod]
    public async Task UnknownField_IsRejectedWithoutPersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Import", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new JsonProfileRepository(new TestPaths(root), new AtomicTextWriter());
            var result = await new JsonProfileInterchange(repository).ImportAsync("""{"schemaVersion":1,"id":"00000000-0000-0000-0000-000000000001","name":"Bad","gameVariant":"XCom2","mods":[],"launchArguments":[],"createdAt":"2026-07-18T00:00:00Z","updatedAt":"2026-07-18T00:00:00Z","localPath":"C:\\Secret"}""", TestContext.CancellationToken);

            result.Error!.Code.Should().Be("profile.import_invalid");
            (await repository.ListAsync(TestContext.CancellationToken)).Value.Should().BeEmpty();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
    private sealed class TestPaths(string root) : AAML.Application.Ports.IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
