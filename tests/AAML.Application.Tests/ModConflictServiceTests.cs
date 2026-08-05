using AAML.Application.Common;
using AAML.Application.Mods.Conflicts;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ModConflictServiceTests
{
    [TestMethod]
    public async Task Analysis_IsDeterministicAndIncludesEveryParticipant()
    {
        var mods = new[] { Installation("C:\\C", "C"), Installation("C:\\A", "A"), Installation("C:\\B", "B") };
        var indexer = new RecordingIndexer(mods.ToDictionary(mod => mod.Key, Manifest));
        var service = new ModConflictService(indexer);

        var first = await service.AnalyzeAsync(mods, mods.Select(mod => mod.Key).ToHashSet(), TestContext.CancellationToken);
        service.InvalidateContent(mods[0].Key);
        service.InvalidateContent(mods[1].Key);
        service.InvalidateContent(mods[2].Key);
        var second = await service.AnalyzeAsync(mods.Reverse().ToArray(), mods.Select(mod => mod.Key).ToHashSet(), TestContext.CancellationToken);

        first.Value!.Conflicts.Select(conflict => conflict.Key).Should().Equal("file:COOKED/SHARED.UPK", "class:FIXTUREBASE");
        first.Value.Conflicts.Should().OnlyContain(conflict => conflict.Participants.Count == 3);
        second.Value!.Conflicts.Should().BeEquivalentTo(first.Value.Conflicts, options => options.WithStrictOrdering());
    }

    [TestMethod]
    public async Task ActiveChange_IndexesNewModAndReprojectsOnlyItsKeys()
    {
        var a = Installation("C:\\A", "A"); var b = Installation("C:\\B", "B"); var c = Installation("C:\\C", "C");
        var d = Installation("C:\\D", "D"); var e = Installation("C:\\E", "E");
        var manifests = new Dictionary<ModKey, ModContentManifest>
        {
            [a.Key] = Manifest(a), [b.Key] = Manifest(b), [c.Key] = Manifest(c),
            [d.Key] = OtherManifest(d), [e.Key] = OtherManifest(e)
        };
        var indexer = new RecordingIndexer(manifests);
        var service = new ModConflictService(indexer);
        var initial = await service.AnalyzeAsync([a, b, c, d, e], new HashSet<ModKey> { a.Key, b.Key, d.Key, e.Key }, TestContext.CancellationToken);
        var unrelated = initial.Value!.Conflicts.Single(conflict => conflict.Key == "file:OTHER/SHARED.INI");

        var updated = await service.SetActiveAsync(new HashSet<ModKey> { a.Key, c.Key, d.Key, e.Key }, TestContext.CancellationToken);

        updated.Value!.AffectedKeys.Should().BeEquivalentTo("file:COOKED/SHARED.UPK", "class:FIXTUREBASE");
        updated.Value.Conflicts.Single(conflict => conflict.Key == "file:OTHER/SHARED.INI").Should().BeSameAs(unrelated);
        indexer.Calls[c.Key].Should().Be(1);
        indexer.Calls[a.Key].Should().Be(1);
        indexer.Calls[b.Key].Should().Be(1);
    }

    [TestMethod]
    public async Task PreCancelledAnalysis_ReturnsCancelledWithoutIndexing()
    {
        var mod = Installation("C:\\A", "A");
        var indexer = new RecordingIndexer(new Dictionary<ModKey, ModContentManifest> { [mod.Key] = Manifest(mod) });
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await new ModConflictService(indexer).AnalyzeAsync([mod], new HashSet<ModKey> { mod.Key }, source.Token);

        result.Error!.Code.Should().Be("conflicts.cancelled");
        indexer.Calls.Should().BeEmpty();
    }

    public TestContext TestContext { get; set; }

    private static ModInstallation Installation(string path, string package) => new(new ModKey(ModSource.Manual, path), new PackageId(package), package, null, true, DescriptorState.Enabled, null);
    private static ModContentManifest Manifest(ModInstallation mod) => new(mod.Key, mod.PackageId,
        [new ModFileFact(mod.Key, "Cooked/Shared.upk")],
        [new ModOverrideFact(mod.Key, mod.PackageId, ModOverrideKind.Class, "FixtureBase", "Replacement" + mod.PackageId.Value, "Config/XComEngine.ini", 2, mod.PackageId.Value)]);
    private static ModContentManifest OtherManifest(ModInstallation mod) => new(mod.Key, mod.PackageId, [new ModFileFact(mod.Key, "Other/Shared.ini")], []);

    private sealed class RecordingIndexer(IReadOnlyDictionary<ModKey, ModContentManifest> manifests) : IModContentIndexer
    {
        public Dictionary<ModKey, int> Calls { get; } = [];
        public Task<Result<ModContentManifest>> IndexAsync(ModInstallation installation, CancellationToken cancellationToken)
        {
            Calls[installation.Key] = Calls.GetValueOrDefault(installation.Key) + 1;
            return Task.FromResult(Result<ModContentManifest>.Success(manifests[installation.Key]));
        }
    }
}
