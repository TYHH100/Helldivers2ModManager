namespace Helldivers2ModManager.Core.Operations;

public sealed record OperationProgress(
    string Stage,
    long Completed,
    long Total,
    string? CurrentItem = null,
    string? Message = null);
