using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void CoreAssemblies_ReferenceNoUiSteamworksOrWindowsDesktopAssemblies()
    {
        var forbiddenPrefixes = new[] { "Avalonia", "Zafiro", "Steamworks", "System.Windows.Forms", "PresentationFramework" };
        var references = typeof(AAML.Application.Common.Result).Assembly.GetReferencedAssemblies()
            .Concat(typeof(AAML.Domain.Mods.ModKey).Assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name ?? string.Empty);

        references.Should().NotContain(reference => forbiddenPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Application_DependsOnDomainButDomainDoesNotDependOnApplication()
    {
        typeof(AAML.Application.Common.Result).Assembly.GetReferencedAssemblies().Should().Contain(reference => reference.Name == "AAML.Domain");
        typeof(AAML.Domain.Mods.ModKey).Assembly.GetReferencedAssemblies().Should().NotContain(reference => reference.Name == "AAML.Application");
    }

    [TestMethod]
    public void ModernProductionSource_ContainsNoLegacyRuntimeApis()
    {
        var root = FindRepositoryRoot();
        var source = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Select(File.ReadAllText)
            .ToArray();

        source.Should().NotContain(text => text.Contains("System.Net.WebClient", StringComparison.Ordinal));
        source.Should().NotContain(text => text.Contains("Assembly.CodeBase", StringComparison.Ordinal));
        source.Should().NotContain(text => text.Contains("System.Windows.Forms", StringComparison.Ordinal));
        source.Should().NotContain(text => text.Contains("using Sentry", StringComparison.OrdinalIgnoreCase));
        source.Should().NotContain(text => text.Contains("SentrySdk", StringComparison.OrdinalIgnoreCase));
        source.Should().NotContain(text => text.Contains("TelemetryClient", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AAML.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate AAML.slnx.");
    }
}
