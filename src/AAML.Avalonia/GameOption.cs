using Avalonia.Media;
using AAML.Domain.Games;

namespace AAML.Avalonia;

/// <summary>A selectable game entry for the shell rail game picker.</summary>
public sealed record GameOption(
    GameVariant Variant,
    string DisplayName,
    IImage? Icon,
    bool IsActive);
