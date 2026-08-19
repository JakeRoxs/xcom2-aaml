using AAML.Domain.Launching;

namespace AAML.Application.Settings;

/// <summary>Defines current durable settings semantics independently of a serializer.</summary>
public static class ApplicationSettingsDefaults
{
    public const int CurrentSchemaVersion = 10;
    public const decimal DefaultTextScale = 1.00m;
    public const decimal MinimumTextScale = 0.80m;
    public const decimal MaximumTextScale = 1.50m;
    public const decimal DefaultIconScale = 1.00m;
    public const decimal MinimumIconScale = 0.75m;
    public const decimal MaximumIconScale = 1.50m;
    public static IReadOnlyList<LaunchArgument> LaunchArguments { get; } = [new("-review"), new("-noRedScreens")];
    public static bool IsTextScaleSupported(decimal value) => value is >= MinimumTextScale and <= MaximumTextScale;
    public static bool IsIconScaleSupported(decimal value) => value is >= MinimumIconScale and <= MaximumIconScale;
}
