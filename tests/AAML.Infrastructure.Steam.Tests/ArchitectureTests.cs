using FluentAssertions;

namespace AAML.Infrastructure.Steam.Tests;

[TestClass]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void PublicApi_ExposesNoSteamworksTypes()
    {
        var assembly = typeof(SteamWorkshopService).Assembly;
        var publicTypes = assembly.GetExportedTypes();
        var exposed = publicTypes
            .SelectMany(type => type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)))
            .Where(type => (type.Namespace ?? string.Empty).StartsWith("Steamworks", StringComparison.Ordinal))
            .ToArray();

        exposed.Should().BeEmpty();
        typeof(AAML.Application.Ports.IWorkshopService).Assembly.GetReferencedAssemblies().Should().NotContain(reference => reference.Name == "Steamworks.NET");
    }

    [TestMethod]
    public async Task ProductionFactory_CanBeCreatedAndDisposedWithoutInitializingSteam()
    {
        var client = SteamWorkshopClient.Create();

        client.Workshop.Should().NotBeNull();
        await client.DisposeAsync();
    }

    [TestMethod]
    public void BindingTypes_RemainInternal()
    {
        var exportedNames = typeof(SteamWorkshopClient).Assembly.GetExportedTypes().Select(type => type.Name);

        exportedNames.Should().NotContain("SteamworksClientApi");
        exportedNames.Should().NotContain("SteamworksUgcApi");
        exportedNames.Should().NotContain("SteamworksCallbacks");
    }
}
