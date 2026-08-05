using System.Text;
using AAML.Application.Configurations;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Configurations;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Configurations;

[TestClass]
public sealed class FilesystemConfigurationFileRepositoryTests
{
    private static readonly ConfigurationFileLimits Limits = new(1_000_000, 500_000, 50_000);

    [TestMethod]
    public async Task LoadAndSave_PreserveUtf16CrLfAndCreateExactRecoveryBackup()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "Config", "XComFixture.ini");
        var original = "[Section]\r\nValue=Ω\r\n";
        var encoding = new UnicodeEncoding(false, true, true);
        var originalBytes = encoding.GetPreamble().Concat(encoding.GetBytes(original)).ToArray();
        await File.WriteAllBytesAsync(path, originalBytes, TestContext.CancellationToken);
        var id = Id(root);
        var repository = new FilesystemConfigurationFileRepository();
        try
        {
            var loaded = await repository.LoadAsync(id, Limits, TestContext.CancellationToken);
            var saved = await repository.SaveAsync(id, loaded.Value!.Text.Replace("Value=Ω", "Value=Changed", StringComparison.Ordinal), loaded.Value.Format, loaded.Value.Revision, TestContext.CancellationToken);

            loaded.Value.Format.Should().Be(new ConfigurationTextFormat(ConfigurationEncoding.Utf16LittleEndian, NewLineStyle.CrLf));
            saved.Value!.RecoveryBackupCreated.Should().BeTrue();
            (await File.ReadAllBytesAsync(path + ".bak", TestContext.CancellationToken)).Should().Equal(originalBytes);
            var reloaded = await repository.LoadAsync(id, Limits, TestContext.CancellationToken);
            reloaded.Value!.Text.Should().Be("[Section]\r\nValue=Changed\r\n");
            reloaded.Value.Format.Should().Be(loaded.Value.Format);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ExternalByteChange_IsRejectedWithoutReplacingOrBackingUp()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "Config", "XComFixture.ini");
        await File.WriteAllTextAsync(path, "Value=A\n", new UTF8Encoding(false), TestContext.CancellationToken);
        var repository = new FilesystemConfigurationFileRepository();
        try
        {
            var loaded = await repository.LoadAsync(Id(root), Limits, TestContext.CancellationToken);
            await File.WriteAllTextAsync(path, "Value=B\n", new UTF8Encoding(false), TestContext.CancellationToken);

            var saved = await repository.SaveAsync(Id(root), "Value=C\n", loaded.Value!.Format, loaded.Value.Revision, TestContext.CancellationToken);

            saved.Error!.Code.Should().Be("configuration.external_change");
            (await File.ReadAllTextAsync(path, TestContext.CancellationToken)).Should().Be("Value=B\n");
            File.Exists(path + ".bak").Should().BeFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TraversalAndNonIniPaths_AreRejected()
    {
        var root = CreateRoot();
        var repository = new FilesystemConfigurationFileRepository();
        try
        {
            var traversal = await repository.LoadAsync(new ConfigurationDocumentId(new ModKey(ModSource.Manual, root), "Config/../../outside.ini"), Limits, TestContext.CancellationToken);
            var wrongExtension = await repository.LoadAsync(new ConfigurationDocumentId(new ModKey(ModSource.Manual, root), "Config/file.txt"), Limits, TestContext.CancellationToken);
            traversal.Error!.Code.Should().Be("configuration.path_invalid");
            wrongExtension.Error!.Code.Should().Be("configuration.path_invalid");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Load_DetectsUtf8BomWindows1252AndMixedNewlines()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "Config", "XComFixture.ini");
        var repository = new FilesystemConfigurationFileRepository();
        try
        {
            await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("A\r\nB\nC\r")], TestContext.CancellationToken);
            var utf8 = await repository.LoadAsync(Id(root), Limits, TestContext.CancellationToken);
            utf8.Value!.Format.Should().Be(new ConfigurationTextFormat(ConfigurationEncoding.Utf8Bom, NewLineStyle.Mixed));

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            await File.WriteAllBytesAsync(path, Encoding.GetEncoding(1252).GetBytes("Value=€\r\n"), TestContext.CancellationToken);
            var ansi = await repository.LoadAsync(Id(root), Limits, TestContext.CancellationToken);
            ansi.Value!.Format.Encoding.Should().Be(ConfigurationEncoding.Windows1252);
            ansi.Value.Text.Should().Contain("€");
        }
        finally { Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }
    private static ConfigurationDocumentId Id(string root) => new(new ModKey(ModSource.Manual, root), "Config/XComFixture.ini");
    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Config Repository", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        return root;
    }
}
