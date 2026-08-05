namespace AAML.Application.Common;

/// <summary>A structured expected failure.</summary>
public sealed record Error(string Code, string Message, ErrorKind Kind, IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Classifies expected application failures.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Unavailable,
    Timeout,
    Cancelled,
    InvalidData,
    Io,
    Network,
    ExternalService,
    Unexpected
}

/// <summary>Represents success or one structured expected failure.</summary>
public readonly record struct Result
{
    private Result(bool isSuccess, Error? error) => (IsSuccess, Error) = (isSuccess, error);

    public bool IsSuccess { get; }
    public Error? Error { get; }
    public static Result Success() => new(true, null);
    public static Result Failure(Error error) => new(false, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Represents a successful value or one structured expected failure.</summary>
public readonly record struct Result<T>
{
    private Result(bool isSuccess, T? value, Error? error) => (IsSuccess, Value, Error) = (isSuccess, value, error);

    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(Error error) => new(false, default, error ?? throw new ArgumentNullException(nameof(error)));
}
