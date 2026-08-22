using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using AAML.Application.Mods.Duplicates;
using AAML.Application.Mods.Grid;
using AAML.Application.Settings;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Avalonia.Tests;

[TestClass]
public sealed class ModProjectionStoreTests
{
    [TestMethod]
    public void KeyedPipeline_PreservesIdentityAcrossIncrementalFilterAndRefresh()
    {
        var rows = new ObservableCollection<SessionModRow>();
        using var store = new ModProjectionStore(rows, (_, _, _) => { });
        var entries = Enumerable.Range(0, 2_000).Select(Entry).ToArray();
        var context = Context();
        var stopwatch = Stopwatch.StartNew();

        store.Apply(entries, context);

        stopwatch.Stop();
        rows.Should().HaveCount(2_000);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        var first = rows.Single(row => row.Key == entries[0].Installation.Key);
        var changed = rows.Single(row => row.Key == entries[1_000].Installation.Key);
        var actions = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => actions.Add(args.Action);

        var updated = entries.ToArray();
        updated[1_000] = updated[1_000] with { Item = updated[1_000].Item with { DisplayName = "Updated name" } };
        stopwatch.Restart();
        store.Apply(updated, context);
        stopwatch.Stop();

        rows.Single(row => row.Key == updated[1_000].Installation.Key).Should().BeSameAs(changed);
        changed.Name.Should().Be("Updated name");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        actions.Should().BeEmpty("an in-place keyed refresh must not churn the collection");

        store.Apply(updated, context with { Query = context.Query with { SearchText = "Mod 0000" } });
        rows.Should().ContainSingle().Which.Should().BeSameAs(first);
        store.Apply(updated, context);
        rows.Single(row => row.Key == entries[0].Installation.Key).Should().BeSameAs(first);
        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
    }

    [TestMethod]
    public void RetainedRow_ReplacesIdentityWhenLastKnownPhysicalKeyChanges()
    {
        var rows = new ObservableCollection<SessionModRow>();
        using var store = new ModProjectionStore(rows, (_, _, _) => { });
        var id = new WorkshopId(42);
        var first = new RetainedWorkshopItem(id, new PackageId("Package"), "Retained", new(ModSource.SteamWorkshop, "/old/location"));
        var second = first with { LastKnownKey = new(ModSource.SteamWorkshop, "/new/location") };

        store.Apply([], Context() with { RetainedItems = [first] });
        var firstRow = rows.Single();
        store.Apply([], Context() with { RetainedItems = [second] });

        rows.Should().ContainSingle();
        rows[0].Should().NotBeSameAs(firstRow);
        rows[0].Location.Should().Be("/new/location");
    }

    private static ModProjectionEntry Entry(int index)
    {
        var key = new ModKey(ModSource.Manual, $"/mods/{index:D4}");
        var installation = new ModInstallation(key, new PackageId($"Package.{index:D4}"), $"Mod {index:D4}", null, false, DescriptorState.Enabled, DateTimeOffset.UnixEpoch);
        var item = new ModGridItem(key, installation.PackageId, null, installation.Name, false, false, index, null, new HashSet<TagId>(),
            new ModStatus(InstallationStatus.Installed, DuplicateStatus.None, DependencyStatus.Satisfied, ConflictStatus.None, UpdateStatus.Current), false, DateTimeOffset.UnixEpoch);
        return new(installation, item, null, null);
    }

    private static ModProjectionContext Context() => new(
        ModGridQuery.Default with { IncludeHidden = true, Sort = new(ModGridSortColumn.Order, SortDirection.Ascending) },
        new([], []), new HashSet<ModGridGroupKey>(), new HashSet<ModKey>(), [], new Dictionary<WorkshopId, string>());
}
