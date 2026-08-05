using System.Text;
using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Common.Files;

/// <summary>Persists UTF-8 text through a same-directory temporary file and one backup.</summary>
public sealed class AtomicTextWriter : IAtomicTextWriter
{
    public async Task<Result> WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        var temporary = path + ".tmp";
        var backup = path + ".bak";
        try
        {
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The target has no parent directory.");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            if (File.Exists(backup)) File.Delete(backup);
            if (File.Exists(path)) File.Replace(temporary, path, backup);
            else File.Move(temporary, path);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            return Result.Failure(new Error("file.write_cancelled", "The atomic write was cancelled.", ErrorKind.Cancelled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryDelete(temporary);
            return Result.Failure(new Error("file.write_failed", exception.Message, ErrorKind.Io));
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
