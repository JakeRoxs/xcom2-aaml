using AAML.Application.Common;

namespace AAML.Application.Logging;

/// <summary>Local-only structured diagnostics with no remote transport or identity.</summary>
public interface ILocalLog : IAsyncDisposable
{
    void Write(LocalLogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? properties = null);
    Task<Result> FlushAsync(CancellationToken cancellationToken);
}

public enum LocalLogLevel { Debug, Information, Warning, Error }
