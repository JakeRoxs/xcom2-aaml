using AAML.Domain.Mods;

namespace AAML.Application.Common;

/// <summary>Structured, presentation-neutral progress for a long operation.</summary>
public sealed record OperationProgress(string Operation, int Completed, int? Total, ModKey? CurrentMod = null);
