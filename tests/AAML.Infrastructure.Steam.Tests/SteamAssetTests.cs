using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;

namespace AAML.Infrastructure.Steam.Tests;

[TestClass]
public sealed class SteamAssetTests
{
    [TestMethod]
    public void VendoredSdk164Assets_MatchPinnedManifest()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "AAML.Infrastructure.Steam", "steamworks-manifest.json")));
        var nativeAssets = document.RootElement.GetProperty("nativeAssets");

        AssertAsset(root, nativeAssets.GetProperty("win-x64"), "win-x64");
        AssertAsset(root, nativeAssets.GetProperty("linux-x64"), "linux-x64");
        document.RootElement.GetProperty("steamworksSdkVersion").GetString().Should().Be("1.64");
        document.RootElement.GetProperty("steamworksNetCommit").GetString().Should().Be("cde64110bff012829b59cc16fe2c4fc3a0371e8d");
    }

    [TestMethod]
    public void Adapter_UsesVendoredWrapperInsteadOfNuGetPackage()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "AAML.Infrastructure.Steam", "AAML.Infrastructure.Steam.csproj"));

        project.Should().NotContain("PackageReference Include=\"Steamworks.NET\"");
        project.Should().Contain("ThirdParty/Steamworks.NET/Steamworks.NET.csproj");
        project.Should().Contain("RuntimeIdentifier)' == 'win-x64'");
        project.Should().Contain("RuntimeIdentifier)' == 'linux-x64'");
    }

    private static void AssertAsset(string root, JsonElement manifest, string runtimeIdentifier)
    {
        var file = manifest.GetProperty("file").GetString()!;
        var nativeDirectory = runtimeIdentifier == "win-x64" ? "win64" : "linux64";
        var path = Path.Combine(root, "src", "ThirdParty", "redistributable_bin", nativeDirectory, file);
        var bytes = File.ReadAllBytes(path);

        bytes.LongLength.Should().Be(manifest.GetProperty("size").GetInt64());
        Convert.ToHexString(SHA256.HashData(bytes)).Should().BeEquivalentTo(manifest.GetProperty("sha256").GetString());
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AAML.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate AAML.slnx.");
    }
}
