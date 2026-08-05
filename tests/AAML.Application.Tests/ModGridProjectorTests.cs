using System.Diagnostics;
using AAML.Application.Mods.Grid;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ModGridProjectorTests
{
    private static readonly Category Gameplay = new(new CategoryId("gameplay"), "Gameplay", 0);
    private static readonly Category Fixes = new(new CategoryId("fixes"), "Fixes", 1);
    private static readonly Tag Stable = new(new TagId("stable"), "Stable");
    private static readonly ModGridLookups Lookups = new([Gameplay, Fixes], [Stable]);

    [TestMethod]
    public void DuplicatePackageIds_RemainDistinctByModKey()
    {
        var items = new[]
        {
            Item("manual", "Shared", category: Gameplay.Id),
            Item("workshop", "Shared", source: ModSource.SteamWorkshop, category: Gameplay.Id)
        };

        var rows = ModGridProjector.Project(items, Lookups, ModGridQuery.Default);

        rows.OfType<ModGridModRow>().Select(row => row.Key).Should().OnlyHaveUniqueItems().And.HaveCount(2);
    }

    [TestMethod]
    public void CategoryGrouping_UsesCategoryOrderAndStableUncategorizedBucket()
    {
        var items = new[]
        {
            Item("none", "No category"),
            Item("fix", "Fix", category: Fixes.Id),
            Item("game", "Game", category: Gameplay.Id)
        };

        var rows = ModGridProjector.Project(items, Lookups, ModGridQuery.Default with { Grouping = ModGridGrouping.Category });

        rows.OfType<ModGridGroupRow>().Select(row => row.Label).Should().Equal("Gameplay", "Fixes", "Uncategorized");
        rows.OfType<ModGridModRow>().Should().HaveCount(3);
    }

    [TestMethod]
    public void CollapsedGroup_HidesChildrenWithoutChangingSourceOrOtherGroups()
    {
        var items = new[] { Item("game", "Game", category: Gameplay.Id), Item("fix", "Fix", category: Fixes.Id) };
        var collapsed = new HashSet<ModGridGroupKey> { new("category", Gameplay.Id.Value) };

        var rows = ModGridProjector.Project(items, Lookups, ModGridQuery.Default with { Grouping = ModGridGrouping.Category }, collapsed);

        rows.OfType<ModGridGroupRow>().Single(row => row.Key.Bucket == Gameplay.Id.Value).IsExpanded.Should().BeFalse();
        rows.OfType<ModGridModRow>().Should().ContainSingle(row => row.Item.CategoryId == Fixes.Id);
        items.Should().HaveCount(2);
    }

    [TestMethod]
    public void SearchAndStateFilters_AreAndedWhileStatesAreOrBuckets()
    {
        var items = new[]
        {
            Item("conflict", "Campaign Fix", conflict: true),
            Item("missing", "Campaign Dependency", missingDependency: true),
            Item("other", "Cosmetic")
        };
        var query = ModGridQuery.Default with
        {
            SearchText = "Campaign",
            StateFilters = new HashSet<ModGridSemanticState> { ModGridSemanticState.Conflict, ModGridSemanticState.MissingDependencies }
        };

        var rows = ModGridProjector.Project(items, Lookups, query);

        rows.OfType<ModGridModRow>().Select(row => row.Key.LocationIdentity).Should().BeEquivalentTo("conflict", "missing");
    }

    [TestMethod]
    public void TagAndStateBuckets_ContainEveryVisibleModExactlyOnce()
    {
        var items = new[]
        {
            Item("tagged", "Tagged", tags: new HashSet<TagId> { Stable.Id }),
            Item("conflict", "Conflict", conflict: true),
            Item("plain", "Plain")
        };

        foreach (var grouping in new[] { ModGridGrouping.ForTag(Stable.Id), ModGridGrouping.ForState(ModGridSemanticState.Conflict) })
        {
            var rows = ModGridProjector.Project(items, Lookups, ModGridQuery.Default with { Grouping = grouping });
            rows.OfType<ModGridModRow>().Select(row => row.Key).Should().BeEquivalentTo(items.Select(item => item.Key));
            rows.OfType<ModGridGroupRow>().Should().HaveCount(2);
        }
    }

    [TestMethod]
    public void SelectionAndCheckState_CanRestoreByKeyAcrossCompatibleRefresh()
    {
        var original = new[] { Item("a", "A", active: true), Item("b", "B") };
        var selected = new HashSet<ModKey> { original[1].Key };
        var refreshed = new[] { original[0] with { DisplayName = "A updated" }, original[1] with { IsActive = true } };

        var rows = ModGridProjector.Project(refreshed, Lookups, ModGridQuery.Default with { Sort = new ModGridSort(ModGridSortColumn.Name, SortDirection.Descending) });
        var visible = rows.OfType<ModGridModRow>().ToDictionary(row => row.Key);

        selected.Where(visible.ContainsKey).Should().Equal(original[1].Key);
        visible[original[1].Key].Item.IsActive.Should().BeTrue();
    }

    [TestMethod]
    public void Sorting_IsDeterministicForEqualVisibleValues()
    {
        var items = new[] { Item("z", "Same"), Item("a", "Same"), Item("m", "Same") };

        var first = ModGridProjector.Project(items, Lookups, ModGridQuery.Default).OfType<ModGridModRow>().Select(row => row.Key).ToArray();
        var second = ModGridProjector.Project(items.Reverse(), Lookups, ModGridQuery.Default).OfType<ModGridModRow>().Select(row => row.Key).ToArray();

        second.Should().Equal(first);
        first.Select(key => key.LocationIdentity).Should().Equal("a", "m", "z");
    }

    [TestMethod]
    public void TwoThousandRows_FilterSortGroupWithinResponsiveCeiling()
    {
        var items = Enumerable.Range(0, 2_000).Select(index => Item(
            $"location-{index:D4}",
            $"Synthetic Mod {index:D4}",
            source: index % 2 == 0 ? ModSource.Manual : ModSource.SteamWorkshop,
            category: index % 3 == 0 ? Gameplay.Id : Fixes.Id,
            tags: index % 5 == 0 ? new HashSet<TagId> { Stable.Id } : null,
            conflict: index % 17 == 0)).ToArray();
        var query = ModGridQuery.Default with { SearchText = "Synthetic", Grouping = ModGridGrouping.Category };
        _ = ModGridProjector.Project(items, Lookups, query);

        var timer = Stopwatch.StartNew();
        var rows = ModGridProjector.Project(items, Lookups, query);
        timer.Stop();

        rows.OfType<ModGridModRow>().Should().HaveCount(2_000);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    private static ModGridItem Item(
        string location,
        string name,
        ModSource source = ModSource.Manual,
        CategoryId? category = null,
        IReadOnlySet<TagId>? tags = null,
        bool active = false,
        bool conflict = false,
        bool missingDependency = false)
    {
        var status = new ModStatus(
            InstallationStatus.Installed,
            DuplicateStatus.None,
            missingDependency ? DependencyStatus.Missing : DependencyStatus.Satisfied,
            conflict ? ConflictStatus.Conflicting : ConflictStatus.None,
            UpdateStatus.Current);
        return new ModGridItem(
            new ModKey(source, location),
            new PackageId("SharedPackage"),
            source == ModSource.SteamWorkshop ? new WorkshopId((ulong)(900_000_000 + location.GetHashCode(StringComparison.Ordinal) & int.MaxValue)) : null,
            name,
            active || missingDependency,
            false,
            null,
            category,
            tags ?? new HashSet<TagId>(),
            status,
            false,
            null);
    }
}
