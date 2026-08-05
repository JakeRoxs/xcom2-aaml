using System.Text;

namespace AAML.Infrastructure.Common.Compatibility.Files;

/// <summary>Writes UTF-8 text using the legacy one-generation backup algorithm.</summary>
public static class LegacyAtomicTextWriter
{
    /// <summary>Writes content through a deterministic temporary file and rotates one backup.</summary>
    public static void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var backupPath = path + ".bak";
        var temporaryPath = path + ".tmp";
        var data = Encoding.UTF8.GetBytes(content);
        using (var stream = File.Create(temporaryPath, 4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
        }

        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, backupPath);
        }
        else
        {
            File.Move(temporaryPath, path);
        }
    }
}
