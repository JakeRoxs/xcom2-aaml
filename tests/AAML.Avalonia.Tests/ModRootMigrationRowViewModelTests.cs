using AAML.Application.Configurations;
using FluentAssertions;

namespace AAML.Avalonia.Tests;

[TestClass]
public sealed class ModRootMigrationRowViewModelTests
{
    [TestMethod]
    public void SelectionIsExplicitAndRestrictedToValidRows()
    {
        var valid = new ModRootMigrationRowViewModel(new ExistingModRootRow(0, "valid", "resolved", 1, ExistingModRootResolution.Valid));
        var missing = new ModRootMigrationRowViewModel(new ExistingModRootRow(1, "missing", null, 2, ExistingModRootResolution.Missing));
        valid.IsSelected.Should().BeFalse();
        valid.IsSelected = true;
        missing.IsSelected = true;
        valid.IsSelected.Should().BeTrue();
        missing.IsSelected.Should().BeFalse();
        missing.CanSelect.Should().BeFalse();
    }
}
