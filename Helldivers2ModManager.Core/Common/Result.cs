namespace Helldivers2ModManager.Core.Common;

public class Result
{
    private protected Result(bool succeeded, Error error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }

    public bool Failed => !Succeeded;

    public Error Error { get; }

    public static Result Success() => new(true, default);

    public static Result Failure(Error error) => new(false, error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Fail<T>(Error error) => Result<T>.CreateFailure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value)
        : base(true, default)
    {
        _value = value;
    }

    private Result(Error error)
        : base(false, error)
    {
    }

    public T Value => Succeeded
        ? _value!
        : throw new InvalidOperationException("A failed result does not contain a value.");

    internal static Result<T> Success(T value) => new(value);

    internal static Result<T> CreateFailure(Error error) => new(error);
}
