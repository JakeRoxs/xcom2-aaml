using AAML.Domain.Launching;

namespace AAML.Application.Settings;

/// <summary>Defines current durable settings semantics independently of a serializer.</summary>
public static class ApplicationSettingsDefaults
{
    public const int CurrentSchemaVersion = 9;
    public static IReadOnlyList<LaunchArgument> LaunchArguments { get; } = [new("-review"), new("-noRedScreens")];
}
