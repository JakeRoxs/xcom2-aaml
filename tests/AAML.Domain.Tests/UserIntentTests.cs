using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Domain.Tests;

[TestClass]
public sealed class UserIntentTests
{
    [TestMethod]
    public void CategoryAndIntent_DoNotOwnOrContainInstallationState()
    {
        var key = new ModKey(ModSource.Manual, "normalized/location");
        var category = new Category(new CategoryId("gameplay"), "Gameplay", 0);
        var intent = new ModUserIntent(key, true, false, 1, null, category.Id, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
        var status = new ModStatus(InstallationStatus.Installed, DuplicateStatus.None, DependencyStatus.Satisfied, ConflictStatus.None, UpdateStatus.Current);

        intent.Category.Should().Be(category.Id);
        status.Installation.Should().Be(InstallationStatus.Installed);
        typeof(ModUserIntent).GetProperties().Should().NotContain(property => property.PropertyType == typeof(ModStatus));
    }
}
