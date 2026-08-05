using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Mods;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Mods;

[TestClass]
public sealed class FilesystemModCatalogSourceTests
{
    [TestMethod]
    public async Task ExplicitRoots_DiscoverManualWorkshopDisabledAndDuplicatePackageIdsWithoutMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Catalog Ω", Guid.NewGuid().ToString("N"));
        var manual = Path.Combine(root, "Manual", "Shared");
        var workshop = Path.Combine(root, "Steam", "workshop", "content", "268500", "900000001");
        try
        {
            Directory.CreateDirectory(manual);
            Directory.CreateDirectory(workshop);
            await File.WriteAllTextAsync(Path.Combine(manual, "Shared.XComMod-disabled"), "[mod]\ntitle=Manual Shared\nrequiresxpack=true", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(workshop, "Shared.XComMod"), "[mod]\npublishedfileid=900000001\ntitle=Workshop Shared", TestContext.CancellationToken);
            var source = new FilesystemModCatalogSource(new HostPathSemantics());

            var result = await source.DiscoverAsync([Path.Combine(root, "Manual"), Path.Combine(root, "Steam", "workshop", "content", "268500")], null, TestContext.CancellationToken);

            result.Value.Should().HaveCount(2);
            result.Value!.Select(mod => mod.PackageId.Value).Should().OnlyContain(id => id == "Shared");
            result.Value.Select(mod => mod.Key).Should().OnlyHaveUniqueItems();
            result.Value.Should().Contain(mod => mod.Key.Source == ModSource.Manual && mod.DescriptorState == DescriptorState.Disabled && mod.RequiresWarOfTheChosen);
            result.Value.Should().Contain(mod => mod.Key.Source == ModSource.SteamWorkshop && mod.WorkshopId == new WorkshopId(900000001));
            File.Exists(Path.Combine(manual, "Shared.XComMod-disabled")).Should().BeTrue();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MissingRoot_IsAnEmptySuccessfulCatalog()
    {
        var source = new FilesystemModCatalogSource(new HostPathSemantics());

        var result = await source.DiscoverAsync([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], null, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [TestMethod]
    public async Task MultipleDescriptorsInOneLocation_ReturnStructuredAmbiguity()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Ambiguous Catalog", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Mod"));
            await File.WriteAllTextAsync(Path.Combine(root, "Mod", "A.XComMod"), "title=A", TestContext.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "Mod", "B.XComMod-disabled"), "title=B", TestContext.CancellationToken);

            var result = await new FilesystemModCatalogSource(new HostPathSemantics()).DiscoverAsync([root], null, TestContext.CancellationToken);

            result.Error!.Code.Should().Be("catalog.multiple_descriptors");
            result.Error.Metadata!["descriptors"].Should().Contain("A.XComMod").And.Contain("B.XComMod-disabled");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }

    private sealed class HostPathSemantics : AAML.Application.Ports.IPathSemantics
    {
        public AAML.Application.Common.Result<string> NormalizeIdentity(string path) => AAML.Application.Common.Result<string>.Success(Path.GetFullPath(path));
        public bool AreEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        public AAML.Application.Common.Result<bool> IsContainedBy(string candidate, string parent) => AAML.Application.Common.Result<bool>.Success(Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(parent), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }
}
