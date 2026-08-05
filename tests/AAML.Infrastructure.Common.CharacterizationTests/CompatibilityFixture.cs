namespace AAML.Infrastructure.Common.CharacterizationTests;

internal static class CompatibilityFixture
{
    public static string Read(params string[] relativePath)
    {
        return File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "TestAssets", "Compatibility", .. relativePath]));
    }
}
