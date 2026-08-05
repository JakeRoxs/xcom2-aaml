using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Domain.Tests;

[TestClass]
public sealed class ModIdentityTests
{
    [TestMethod]
    public void SamePackageIdAtDifferentLocations_HasDistinctKeys()
    {
        var manual = new ModKey(ModSource.Manual, "C:/Mods/Synthetic");
        var workshop = new ModKey(ModSource.SteamWorkshop, "C:/Steam/workshop/900000001");

        manual.Should().NotBe(workshop);
        new PackageId("DuplicatePackage").Should().Be(new PackageId("DuplicatePackage"));
    }

    [TestMethod]
    public void Key_DoesNotNormalizeAdapterOwnedLocationIdentity()
    {
        var key = new ModKey(ModSource.Manual, "Case/And\\Separators");

        key.LocationIdentity.Should().Be("Case/And\\Separators");
    }
}
