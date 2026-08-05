using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Mods;

namespace AAML.Application.Mods.Dependencies;

public enum ModDependencyIssueKind { Missing, Inactive, Ignored, Cyclic, MetadataUnavailable }

public sealed record ModDependencyIssue(
    WorkshopId Parent,
    WorkshopId Required,
    ModDependencyIssueKind Kind,
    IReadOnlyList<WorkshopId> Path,
    string Message);

public sealed record ModDependencyReport(
    IReadOnlyList<ModDependencyIssue> Issues,
    IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>> Graph)
{
    public bool HasBlockingIssues => Issues.Any(issue => issue.Kind is ModDependencyIssueKind.Missing or ModDependencyIssueKind.Inactive or ModDependencyIssueKind.MetadataUnavailable);
}

public interface IModDependencyService
{
    Task<Result<ModDependencyReport>> EvaluateAsync(
        IReadOnlyCollection<WorkshopId> roots,
        IReadOnlyCollection<WorkshopId> installed,
        IReadOnlyCollection<WorkshopId> active,
        IReadOnlyDictionary<WorkshopId, IReadOnlySet<WorkshopId>> ignored,
        CancellationToken cancellationToken);
}

/// <summary>Resolves and caches Workshop child graphs while retaining explicit unknown states.</summary>
public sealed class ModDependencyService(IWorkshopService workshop) : IModDependencyService
{
    private readonly Dictionary<WorkshopId, WorkshopItem?> cache = [];
    private readonly SemaphoreSlim cacheGate = new(1, 1);

    public async Task<Result<ModDependencyReport>> EvaluateAsync(
        IReadOnlyCollection<WorkshopId> roots,
        IReadOnlyCollection<WorkshopId> installed,
        IReadOnlyCollection<WorkshopId> active,
        IReadOnlyDictionary<WorkshopId, IReadOnlySet<WorkshopId>> ignored,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(ignored);
        var rootSet = roots.Where(id => id.Value != 0).ToHashSet();
        if (rootSet.Count == 0) return Result<ModDependencyReport>.Success(new ModDependencyReport([], new Dictionary<WorkshopId, IReadOnlyList<WorkshopId>>()));

        var graphResult = await ResolveGraphAsync(rootSet, cancellationToken).ConfigureAwait(false);
        if (!graphResult.IsSuccess)
        {
            var unavailable = rootSet.OrderBy(id => id.Value).Select(id => new ModDependencyIssue(id, id, ModDependencyIssueKind.MetadataUnavailable, [id], graphResult.Error!.Message)).ToArray();
            return Result<ModDependencyReport>.Success(new ModDependencyReport(unavailable, new Dictionary<WorkshopId, IReadOnlyList<WorkshopId>>()));
        }

        var graph = graphResult.Value!;
        var installedSet = installed.ToHashSet();
        var activeSet = active.ToHashSet();
        var issues = new List<ModDependencyIssue>();
        foreach (var root in rootSet.OrderBy(id => id.Value)) Walk(root, root, [root], graph, installedSet, activeSet, ignored, issues);
        return Result<ModDependencyReport>.Success(new ModDependencyReport(
            issues.DistinctBy(issue => (issue.Parent, issue.Required, issue.Kind)).ToArray(), graph));
    }

    private async Task<Result<IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>>>> ResolveGraphAsync(IReadOnlyCollection<WorkshopId> roots, CancellationToken cancellationToken)
    {
        await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var graph = new Dictionary<WorkshopId, IReadOnlyList<WorkshopId>>();
            var pending = new Queue<WorkshopId>(roots);
            var visited = new HashSet<WorkshopId>();
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new List<WorkshopId>();
                while (pending.Count > 0 && batch.Count < 100)
                {
                    var id = pending.Dequeue();
                    if (visited.Add(id)) batch.Add(id);
                }
                var unresolved = batch.Where(id => !cache.ContainsKey(id)).ToArray();
                if (unresolved.Length > 0)
                {
                    var queried = await workshop.GetItemsAsync(unresolved, null, cancellationToken).ConfigureAwait(false);
                    if (!queried.IsSuccess) return Result<IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>>>.Failure(queried.Error!);
                    var returned = queried.Value!.ToDictionary(item => item.PublishedFileId);
                    foreach (var id in unresolved) cache[id] = returned.GetValueOrDefault(id);
                }
                foreach (var id in batch)
                {
                    if (cache[id] is null) continue;
                    var children = cache[id]!.ChildIds.Distinct().OrderBy(child => child.Value).ToArray();
                    graph[id] = children;
                    foreach (var child in children)
                        if (!visited.Contains(child)) pending.Enqueue(child);
                }
            }
            return Result<IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>>>.Success(graph);
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>>>.Failure(new Error("dependencies.cancelled", "Dependency resolution was cancelled.", ErrorKind.Cancelled));
        }
        finally { cacheGate.Release(); }
    }

    private static void Walk(
        WorkshopId root,
        WorkshopId parent,
        IReadOnlyList<WorkshopId> path,
        IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>> graph,
        IReadOnlySet<WorkshopId> installed,
        IReadOnlySet<WorkshopId> active,
        IReadOnlyDictionary<WorkshopId, IReadOnlySet<WorkshopId>> ignored,
        ICollection<ModDependencyIssue> issues)
    {
        if (!graph.TryGetValue(parent, out var children))
        {
            issues.Add(new ModDependencyIssue(parent, parent, ModDependencyIssueKind.MetadataUnavailable, path, $"Workshop metadata is unavailable for {parent.Value}."));
            return;
        }
        foreach (var child in children)
        {
            var childPath = path.Append(child).ToArray();
            if (ignored.TryGetValue(parent, out var ignoredChildren) && ignoredChildren.Contains(child))
            {
                issues.Add(new ModDependencyIssue(parent, child, ModDependencyIssueKind.Ignored, childPath, $"Dependency {child.Value} is ignored for {parent.Value}."));
                continue;
            }
            if (path.Contains(child))
            {
                issues.Add(new ModDependencyIssue(parent, child, ModDependencyIssueKind.Cyclic, childPath, $"Dependency cycle detected from {root.Value} through {child.Value}."));
                continue;
            }
            if (!installed.Contains(child))
            {
                issues.Add(new ModDependencyIssue(parent, child, ModDependencyIssueKind.Missing, childPath, $"Required Workshop item {child.Value} is not installed."));
                continue;
            }
            if (!active.Contains(child))
            {
                issues.Add(new ModDependencyIssue(parent, child, ModDependencyIssueKind.Inactive, childPath, $"Required Workshop item {child.Value} is installed but inactive."));
                continue;
            }
            Walk(root, child, childPath, graph, installed, active, ignored, issues);
        }
    }
}
