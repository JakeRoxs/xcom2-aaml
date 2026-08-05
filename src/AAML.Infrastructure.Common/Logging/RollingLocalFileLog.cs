using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AAML.Application.Common;
using AAML.Application.Logging;

namespace AAML.Infrastructure.Common.Logging;

public sealed record RollingLocalFileLogOptions(string Directory, long MaxFileBytes, int RetainedFiles)
{
    public static RollingLocalFileLogOptions Create(string directory) => new(directory, 5 * 1024 * 1024, 5);
}

/// <summary>Asynchronous JSON-lines logger that writes only to rotating local files.</summary>
public sealed class RollingLocalFileLog : ILocalLog
{
    private readonly RollingLocalFileLogOptions options;
    private readonly TimeProvider timeProvider;
    private readonly Channel<Command> channel = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task writer;
    private bool disposed;

    public RollingLocalFileLog(RollingLocalFileLogOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Directory) || options.MaxFileBytes <= 0 || options.RetainedFiles < 1)
            throw new ArgumentException("Log options are invalid.", nameof(options));
        this.options = options;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        writer = WriteLoopAsync();
    }

    public void Write(LocalLogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? properties = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(message);
        if (!channel.Writer.TryWrite(new EntryCommand(new LogEntry(timeProvider.GetUtcNow(), level, eventName, message, properties))))
            throw new InvalidOperationException("The local log writer is unavailable.");
    }

    public async Task<Result> FlushAsync(CancellationToken cancellationToken)
    {
        if (disposed) return Result.Failure(new Error("log.disposed", "The local log is disposed.", ErrorKind.Unavailable));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!channel.Writer.TryWrite(new FlushCommand(completion)))
            return Result.Failure(new Error("log.unavailable", "The local log writer is unavailable.", ErrorKind.Unavailable));
        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(new Error("log.flush_cancelled", "The local log flush was cancelled.", ErrorKind.Cancelled));
        }
        catch (Exception exception)
        {
            return Result.Failure(new Error("log.flush_failed", exception.Message, ErrorKind.Io));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        channel.Writer.TryComplete();
        await writer.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        Directory.CreateDirectory(options.Directory);
        var path = Path.Combine(options.Directory, "aaml.log");
        FileStream? stream = null;
        StreamWriter? text = null;
        try
        {
            (stream, text) = Open(path);
            var currentBytes = stream.Length;
            await foreach (var command in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (command is FlushCommand flush)
                {
                    try { await text.FlushAsync().ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false); flush.Completion.TrySetResult(); }
                    catch (Exception exception) { flush.Completion.TrySetException(exception); }
                    continue;
                }

                var entry = ((EntryCommand)command).Entry;
                var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (currentBytes > 0 && currentBytes + bytes > options.MaxFileBytes)
                {
                    await text.FlushAsync().ConfigureAwait(false);
                    await text.DisposeAsync().ConfigureAwait(false);
                    await stream.DisposeAsync().ConfigureAwait(false);
                    Rotate(path);
                    (stream, text) = Open(path);
                    currentBytes = 0;
                }
                await text.WriteAsync(line).ConfigureAwait(false);
                currentBytes += bytes;
            }
            await text.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            if (text is not null) await text.DisposeAsync().ConfigureAwait(false);
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private (FileStream Stream, StreamWriter Writer) Open(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        return (stream, new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true));
    }

    private void Rotate(string path)
    {
        var oldest = $"{path}.{options.RetainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = options.RetainedFiles - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{path}.{index + 1}");
        }
        if (File.Exists(path)) File.Move(path, path + ".1");
    }

    private abstract record Command;
    private sealed record EntryCommand(LogEntry Entry) : Command;
    private sealed record FlushCommand(TaskCompletionSource Completion) : Command;
    private sealed record LogEntry(DateTimeOffset Timestamp, LocalLogLevel Level, string EventName, string Message, IReadOnlyDictionary<string, string>? Properties);
}
