using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AAML.Application.Common;
using AAML.Application.Configurations;

namespace AAML.Infrastructure.Common.Configurations;

public sealed partial class FilesystemConfigurationFileRepository : IConfigurationFileRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<Result<ConfigurationFileVersion>> LoadAsync(ConfigurationDocumentId id, ConfigurationFileLimits limits, CancellationToken cancellationToken)
    {
        var resolved = Resolve(id);
        if (!resolved.IsSuccess) return Result<ConfigurationFileVersion>.Failure(resolved.Error!);
        try
        {
            var bytes = await File.ReadAllBytesAsync(resolved.Value!, cancellationToken).ConfigureAwait(false);
            var decoded = ConfigurationTextCodec.Decode(bytes, limits);
            return decoded.IsSuccess
                ? Result<ConfigurationFileVersion>.Success(new ConfigurationFileVersion(id, decoded.Value!.Text, decoded.Value.Format, Revision(bytes)))
                : Result<ConfigurationFileVersion>.Failure(decoded.Error!);
        }
        catch (OperationCanceledException) { return Failure("configuration.load_cancelled", "Configuration load was cancelled.", ErrorKind.Cancelled); }
        catch (FileNotFoundException) { return Failure("configuration.not_found", $"Configuration file was not found: {id.RelativePath}", ErrorKind.NotFound); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Failure("configuration.load_failed", exception.Message, ErrorKind.Io); }
    }

    public async Task<Result<ConfigurationSaveReceipt>> SaveAsync(ConfigurationDocumentId id, string text, ConfigurationTextFormat format, string expectedRevision, CancellationToken cancellationToken)
    {
        var resolved = Resolve(id);
        if (!resolved.IsSuccess) return Result<ConfigurationSaveReceipt>.Failure(resolved.Error!);
        var effectiveFormat = ConfigurationTextCodec.ResolveFormat(text, format);
        var encoded = ConfigurationTextCodec.Encode(text, effectiveFormat);
        if (!encoded.IsSuccess) return Result<ConfigurationSaveReceipt>.Failure(encoded.Error!);
        var path = resolved.Value!;
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return SaveFailure("configuration.save_cancelled", "Configuration save was cancelled.", ErrorKind.Cancelled); }
        string? temporary = null;
        try
        {
            if (!File.Exists(path)) return SaveFailure("configuration.not_found", $"Configuration file was not found: {id.RelativePath}", ErrorKind.NotFound);
            var current = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Revision(current), expectedRevision, StringComparison.Ordinal))
                return SaveFailure("configuration.external_change", "The configuration file changed outside AAML. Reload before saving.", ErrorKind.Conflict);

            temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.aaml.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encoded.Value!, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            current = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Revision(current), expectedRevision, StringComparison.Ordinal))
                return SaveFailure("configuration.external_change", "The configuration file changed outside AAML. Reload before saving.", ErrorKind.Conflict);
            File.Replace(temporary, path, path + ".bak", true);
            temporary = null;
            return Result<ConfigurationSaveReceipt>.Success(new ConfigurationSaveReceipt(Revision(encoded.Value!), true, effectiveFormat));
        }
        catch (OperationCanceledException) { return SaveFailure("configuration.save_cancelled", "Configuration save was cancelled.", ErrorKind.Cancelled); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return SaveFailure("configuration.save_failed", exception.Message, ErrorKind.Io); }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (Exception) { }
            gate.Release();
        }
    }

    public async Task<Result<SavedConfigurationSnapshot?>> LoadRecoveryAsync(ConfigurationDocumentId id, ConfigurationFileLimits limits, CancellationToken cancellationToken)
    {
        var resolved = Resolve(id);
        if (!resolved.IsSuccess) return Result<SavedConfigurationSnapshot?>.Failure(resolved.Error!);
        var backup = resolved.Value! + ".bak";
        if (!File.Exists(backup)) return Result<SavedConfigurationSnapshot?>.Success(null);
        try
        {
            var bytes = await File.ReadAllBytesAsync(backup, cancellationToken).ConfigureAwait(false);
            var decoded = ConfigurationTextCodec.Decode(bytes, limits);
            return decoded.IsSuccess
                ? Result<SavedConfigurationSnapshot?>.Success(new SavedConfigurationSnapshot(id, decoded.Value!.Text, decoded.Value.Format))
                : Result<SavedConfigurationSnapshot?>.Failure(decoded.Error!);
        }
        catch (OperationCanceledException) { return Result<SavedConfigurationSnapshot?>.Failure(new Error("configuration.load_cancelled", "Recovery load was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Result<SavedConfigurationSnapshot?>.Failure(new Error("configuration.load_failed", exception.Message, ErrorKind.Io)); }
    }

    internal static Result<string> Resolve(ConfigurationDocumentId id)
    {
        if (id is null || string.IsNullOrWhiteSpace(id.RelativePath) || id.RelativePath.IndexOf('\0') >= 0)
            return PathFailure("Configuration path is required.");
        var canonical = id.RelativePath.Replace('\\', '/');
        var components = canonical.Split('/');
        if (Path.IsPathRooted(canonical) || components.Length < 2 || components.Any(component => component is "" or "." or "..") || !components[0].Equals("Config", StringComparison.OrdinalIgnoreCase) || !Path.GetExtension(components[^1]).Equals(".ini", StringComparison.OrdinalIgnoreCase))
            return PathFailure("Only existing Config/**/*.ini files can be edited.");
        try
        {
            var root = Path.GetFullPath(id.Mod.LocationIdentity);
            var path = Path.GetFullPath(Path.Combine([root, .. components]));
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return PathFailure("Configuration path escapes the mod directory.");
            var current = root;
            foreach (var component in components)
            {
                current = Path.Combine(current, component);
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return PathFailure("Configuration paths through links or reparse points are not editable.");
            }
            return Result<string>.Success(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Failure(new Error("configuration.path_invalid", exception.Message, ErrorKind.Validation));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(new Error("configuration.path_unavailable", exception.Message, ErrorKind.Io));
        }
    }

    private static string Revision(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Result<ConfigurationFileVersion> Failure(string code, string message, ErrorKind kind) => Result<ConfigurationFileVersion>.Failure(new Error(code, message, kind));
    private static Result<ConfigurationSaveReceipt> SaveFailure(string code, string message, ErrorKind kind) => Result<ConfigurationSaveReceipt>.Failure(new Error(code, message, kind));
    private static Result<string> PathFailure(string message) => Result<string>.Failure(new Error("configuration.path_invalid", message, ErrorKind.Validation));
}

internal sealed record DecodedConfiguration(string Text, ConfigurationTextFormat Format);

internal static partial class ConfigurationTextCodec
{
    static ConfigurationTextCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Result<DecodedConfiguration> Decode(byte[] bytes, ConfigurationFileLimits limits)
    {
        if (bytes.LongLength > limits.MaxBytes) return DecodeFailure("configuration.file_too_large", "Configuration exceeds the byte limit.");
        try
        {
            var (encoding, preamble, kind) = Detect(bytes);
            var text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
            if (text.Length > limits.MaxCharacters) return DecodeFailure("configuration.file_too_large", "Configuration exceeds the character limit.");
            if (LineCount(text) > limits.MaxLines) return DecodeFailure("configuration.file_too_large", "Configuration exceeds the line limit.");
            return Result<DecodedConfiguration>.Success(new DecodedConfiguration(text, new ConfigurationTextFormat(kind, DetectNewLines(text))));
        }
        catch (DecoderFallbackException exception) { return DecodeFailure("configuration.encoding_invalid", exception.Message); }
    }

    public static Result<byte[]> Encode(string text, ConfigurationTextFormat format)
    {
        try
        {
            var normalized = NormalizeNewLines(text, format.NewLines);
            if (!normalized.IsSuccess) return Result<byte[]>.Failure(normalized.Error!);
            var encoding = EncodingFor(format.Encoding);
            var body = encoding.GetBytes(normalized.Value!);
            var preamble = encoding.GetPreamble();
            return Result<byte[]>.Success(preamble.Length == 0 ? body : [.. preamble, .. body]);
        }
        catch (EncoderFallbackException exception) { return Result<byte[]>.Failure(new Error("configuration.encoding_invalid", exception.Message, ErrorKind.InvalidData)); }
    }

    public static ConfigurationTextFormat ResolveFormat(string text, ConfigurationTextFormat format) =>
        format.NewLines == NewLineStyle.None && text.IndexOfAny(['\r', '\n']) >= 0 ? format with { NewLines = DetectNewLines(text) } : format;

    private static (Encoding Encoding, int Preamble, ConfigurationEncoding Kind) Detect(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(false, true), 3, ConfigurationEncoding.Utf8Bom);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, false, true), 2, ConfigurationEncoding.Utf16LittleEndian);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, false, true), 2, ConfigurationEncoding.Utf16BigEndian);
        var utf8 = new UTF8Encoding(false, true);
        try { _ = utf8.GetString(bytes); return (utf8, 0, ConfigurationEncoding.Utf8); }
        catch (DecoderFallbackException) { return (Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), 0, ConfigurationEncoding.Windows1252); }
    }

    private static Encoding EncodingFor(ConfigurationEncoding encoding) => encoding switch
    {
        ConfigurationEncoding.Utf8 => new UTF8Encoding(false, true),
        ConfigurationEncoding.Utf8Bom => new UTF8Encoding(true, true),
        ConfigurationEncoding.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
        ConfigurationEncoding.Utf16BigEndian => new UnicodeEncoding(true, true, true),
        ConfigurationEncoding.Windows1252 => Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    private static Result<string> NormalizeNewLines(string text, NewLineStyle style)
    {
        if (style == NewLineStyle.Mixed) return Result<string>.Success(text);
        if (style == NewLineStyle.None) return Result<string>.Success(text);
        var separator = style switch { NewLineStyle.Lf => "\n", NewLineStyle.CrLf => "\r\n", NewLineStyle.Cr => "\r", _ => throw new ArgumentOutOfRangeException(nameof(style)) };
        return Result<string>.Success(NewLineRegex().Replace(text, separator));
    }

    private static NewLineStyle DetectNewLines(string text)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var withoutCrLf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        var lf = withoutCrLf.Contains('\n');
        var cr = withoutCrLf.Contains('\r');
        var count = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0);
        if (count > 1) return NewLineStyle.Mixed;
        if (crlf) return NewLineStyle.CrLf;
        if (lf) return NewLineStyle.Lf;
        return cr ? NewLineStyle.Cr : NewLineStyle.None;
    }

    private static int LineCount(string text) => text.Length == 0 ? 0 : NewLineRegex().Matches(text).Count + 1;
    private static Result<DecodedConfiguration> DecodeFailure(string code, string message) => Result<DecodedConfiguration>.Failure(new Error(code, message, ErrorKind.InvalidData));
    [GeneratedRegex("\\r\\n|\\r|\\n")]
    private static partial Regex NewLineRegex();
}
