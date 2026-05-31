using System.Runtime.CompilerServices;

namespace BlackIce.Server.Common;

/// <summary>
/// Domain-neutral failure taxonomy for <see cref="Result"/> / <see cref="Result{T}"/>. An expected,
/// recoverable failure is represented by one of these codes instead of a thrown exception — exceptions
/// are reserved for genuinely exceptional conditions and framework boundaries. Inspired by DuetOS's
/// kernel ErrorCode, trimmed to what a game server actually produces.
/// </summary>
public enum ErrorCode : byte
{
    Ok = 0,             // sentinel; never the error of a failed result
    InvalidArgument,    // caller-supplied value out of the documented range
    NotFound,           // lookup hit a non-existent entry
    AlreadyExists,      // create when the target was already claimed
    PermissionDenied,   // auth / ban / capability check refused
    Timeout,            // wait hit its deadline
    Unsupported,        // operation not implemented in this configuration
    BadState,           // object is not in a state that permits this op
    IoError,            // socket / file / device reported a failure
    Corrupt,            // on-disk / on-wire data failed a sanity check
    NotReady,           // dependency still initialising
    Busy,               // resource is in use by another owner
    Conflict,           // optimistic/uniqueness conflict
    Cancelled,          // operation was cancelled before completion
    Unknown,
}

/// <summary>Where a failure was raised — captured at the <see cref="Result.Fail"/> call site via CallerInfo.</summary>
public readonly record struct SourceLocation(string? File, string? Member, int Line)
{
    public bool IsKnown => File is not null;
    public override string ToString() =>
        IsKnown ? $"{System.IO.Path.GetFileName(File)}:{Line} ({Member})" : "<unknown>";
}

/// <summary>
/// An error in flight, carrying its <see cref="ErrorCode"/> and origin. Implicitly converts to a
/// <see cref="Result"/> or any <see cref="Result{T}"/>, so a fallible method can simply
/// <c>return Result.Fail(ErrorCode.NotFound);</c> regardless of its return type.
/// </summary>
public readonly record struct Err(ErrorCode Code, SourceLocation Location)
{
    public override string ToString() =>
        Location.IsKnown ? $"{Code} @ {Location}" : Code.ToString();
}

/// <summary>
/// A status-only outcome (the "Unit" / <c>Result&lt;void&gt;</c> form): success, or a failure with a
/// code and origin. Returned by fallible operations that have no value to yield.
/// </summary>
public readonly struct Result
{
    public bool IsOk { get; }
    public ErrorCode Error { get; }
    public SourceLocation Location { get; }

    private Result(bool ok, ErrorCode error, SourceLocation location)
    {
        IsOk = ok;
        Error = error;
        Location = location;
    }

    /// <summary>A successful status result.</summary>
    public static readonly Result Ok = new(true, ErrorCode.Ok, default);

    public bool IsFail => !IsOk;
    public Err AsErr() => new(Error, Location);

    /// <summary>
    /// Builds a failure, capturing the call site (file / member / line) — the C# analogue of DuetOS's
    /// <c>Err{code}</c> with <c>__builtin_FILE/LINE</c>. The result is implicitly convertible to any
    /// <see cref="Result"/> or <see cref="Result{T}"/>.
    /// </summary>
    public static Err Fail(ErrorCode code,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? member = null,
        [CallerLineNumber] int line = 0)
        => new(code, new SourceLocation(file, member, line));

    public static implicit operator Result(Err e) => new(false, e.Code, e.Location);

    /// <summary>Re-wraps this failure as a value result of <typeparamref name="T"/> (no-op on success is a programming error).</summary>
    public Result<T> ToError<T>() => Result<T>.FromError(AsErr());

    public TR Match<TR>(Func<TR> ok, Func<Err, TR> fail) => IsOk ? ok() : fail(AsErr());

    public override string ToString() => IsOk ? "Ok" : $"Fail({AsErr()})";
}

/// <summary>
/// An outcome that yields a <typeparamref name="T"/> on success or a coded failure with origin. A
/// plain value implicitly becomes a success (<c>return value;</c>) and an <see cref="Err"/> implicitly
/// becomes a failure (<c>return Result.Fail(...);</c>).
/// </summary>
public readonly struct Result<T>
{
    private readonly T _value;
    public bool IsOk { get; }
    public ErrorCode Error { get; }
    public SourceLocation Location { get; }

    private Result(bool ok, T value, ErrorCode error, SourceLocation location)
    {
        IsOk = ok;
        _value = value;
        Error = error;
        Location = location;
    }

    public static Result<T> Ok(T value) => new(true, value, ErrorCode.Ok, default);
    public static Result<T> FromError(Err e) => new(false, default!, e.Code, e.Location);

    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(Err e) => FromError(e);

    public bool IsFail => !IsOk;
    public Err AsErr() => new(Error, Location);

    /// <summary>The value, or an <see cref="InvalidOperationException"/> if accessed on the error path (a caller bug).</summary>
    public T Value => IsOk
        ? _value
        : throw new InvalidOperationException($"Result<{typeof(T).Name}>.Value read on error state ({AsErr()})");

    /// <summary>The value on success, or <paramref name="fallback"/> on failure.</summary>
    public T ValueOr(T fallback) => IsOk ? _value : fallback;

    /// <summary>Try-pattern bridge: true with the value on success, false (default) on failure.</summary>
    public bool TryGet(out T value)
    {
        value = IsOk ? _value : default!;
        return IsOk;
    }

    /// <summary>Maps the success value, propagating any failure unchanged (origin preserved).</summary>
    public Result<TU> Map<TU>(Func<T, TU> f) => IsOk ? Result<TU>.Ok(f(_value)) : Result<TU>.FromError(AsErr());

    /// <summary>Chains another fallible step (railway propagation), propagating any failure unchanged.</summary>
    public Result<TU> Bind<TU>(Func<T, Result<TU>> f) => IsOk ? f(_value) : Result<TU>.FromError(AsErr());

    /// <summary>Drops the value, keeping just the success/failure status.</summary>
    public Result ToStatus() => IsOk ? Result.Ok : AsErr();

    public TR Match<TR>(Func<T, TR> ok, Func<Err, TR> fail) => IsOk ? ok(_value) : fail(AsErr());

    public override string ToString() => IsOk ? $"Ok({_value})" : $"Fail({AsErr()})";
}
