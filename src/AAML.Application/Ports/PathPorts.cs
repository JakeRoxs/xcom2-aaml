using AAML.Application.Common;
using AAML.Domain.Mods;

namespace AAML.Application.Ports;

/// <summary>Host-specific path identity and comparison semantics.</summary>
public interface IPathSemantics
{
    Result<string> NormalizeIdentity(string path);
    bool AreEqual(string left, string right);
    Result<bool> IsContainedBy(string candidate, string parent);
}

/// <summary>Provides mutable application storage locations.</summary>
public interface IApplicationPaths
{
    string ConfigurationDirectory { get; }
    string DataDirectory { get; }
    string StateDirectory { get; }
    string CacheDirectory { get; }
    string RuntimeDirectory { get; }
}

/// <summary>Resolves an existing host path to its physical filesystem identity.</summary>
public interface IPhysicalPathResolver
{
    Result<string> ResolveExisting(string path);
}

/// <summary>Resolves a known game artifact component-by-component without changing host identity semantics.</summary>
public interface IKnownGameArtifactResolver
{
    Task<Result<ResolvedGameArtifact>> ResolveAsync(KnownGameArtifactRequest request, CancellationToken cancellationToken);
}

public sealed record KnownGameArtifactRequest(string RootDirectory, IReadOnlyList<string> RelativeComponents, ArtifactKind Kind);
public sealed record ResolvedGameArtifact(string Path, IReadOnlyList<string> ActualComponents);
public enum ArtifactKind { File, Directory }

/// <summary>Executes planned descriptor mutations and preserves each outcome.</summary>
public interface IDescriptorMutationExecutor
{
    Task<BatchResult<ModKey>> ApplyAsync(IReadOnlyList<DescriptorMutationRequest> requests, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

public sealed record DescriptorMutationRequest(ModKey Mod, DescriptorMutation Mutation);
public enum DescriptorMutation { Enable, Disable }
