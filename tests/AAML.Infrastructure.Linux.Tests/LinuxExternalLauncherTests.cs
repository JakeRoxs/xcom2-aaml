using System.ComponentModel;
using System.Diagnostics;
using AAML.Infrastructure.Linux.Processes;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxExternalLauncherTests
{
    [TestMethod]
    public async Task KdeDirectory_UsesDolphinWithOneLiteralArgument()
    {
        var directory = TemporaryDirectory("AAML & folder Ω");
        try
        {
            var starter = new RecordingStarter();
            var launcher = new LinuxExternalLauncher(starter, name => name == "XDG_CURRENT_DESKTOP" ? "KDE" : null);

            var result = await launcher.OpenDirectoryAsync(directory, TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            starter.Starts.Should().ContainSingle();
            starter.Starts[0].FileName.Should().Be("dolphin");
            starter.Starts[0].UseShellExecute.Should().BeFalse();
            starter.Starts[0].ArgumentList.Should().Equal(directory);
        }
        finally { Directory.Delete(directory); }
    }

    [TestMethod]
    public async Task MissingDolphin_FallsBackToStructuredXdgOpen()
    {
        var directory = TemporaryDirectory("AAML folder");
        try
        {
            var starter = new RecordingStarter(failFirst: true);
            var launcher = new LinuxExternalLauncher(starter, name => name == "DESKTOP_SESSION" ? "plasma" : null);

            var result = await launcher.OpenDirectoryAsync(directory, TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            starter.Starts.Select(start => start.FileName).Should().Equal("dolphin", "xdg-open");
            starter.Starts[1].ArgumentList.Should().Equal(directory);
        }
        finally { Directory.Delete(directory); }
    }

    [TestMethod]
    public async Task NonKdeDirectory_UsesXdgOpen()
    {
        var directory = TemporaryDirectory("AAML folder");
        try
        {
            var starter = new RecordingStarter();

            var result = await new LinuxExternalLauncher(starter, _ => null).OpenDirectoryAsync(directory, TestContext.CancellationToken);

            result.IsSuccess.Should().BeTrue();
            starter.Starts.Should().ContainSingle(start => start.FileName == "xdg-open" && start.ArgumentList.SequenceEqual(new[] { directory }));
        }
        finally { Directory.Delete(directory); }
    }

    public TestContext TestContext { get; set; }

    private static string TemporaryDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingStarter(bool failFirst = false) : ILinuxProcessStarter
    {
        public List<ProcessStartInfo> Starts { get; } = [];
        public int? Start(ProcessStartInfo startInfo)
        {
            Starts.Add(startInfo);
            if (failFirst && Starts.Count == 1) throw new Win32Exception("Synthetic missing Dolphin.");
            return 42;
        }
    }
}
