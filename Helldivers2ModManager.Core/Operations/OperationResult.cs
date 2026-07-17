namespace Helldivers2ModManager.Core.Operations;

public record OperationResult(bool IsSuccess, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static OperationResult Success() => new(true);

    public static OperationResult<T> Success<T>(T value) => new(true, value);

    public static OperationResult Failure(string errorCode, string? errorMessage = null) =>
        new(false, errorCode, errorMessage);

    public static OperationResult<T> Failure<T>(string errorCode, string? errorMessage = null) =>
        new(false, default, errorCode, errorMessage);
}

public sealed record OperationResult<T>(
    bool IsSuccess,
    T? Value = default,
    string? ErrorCode = null,
    string? ErrorMessage = null);
