namespace Helldivers2ModManager.Core.GameData;

public enum GameCompanionKind
{
    GpuResources,
    Stream,
}

public sealed record GameCompanionSegment(
    ulong TargetOffset,
    uint Size,
    string PackageName,
    byte[]? Payload);

public sealed record GameCompanionRecipe(
    string Description,
    long Length,
    IReadOnlyList<GameCompanionSegment> Segments);

public sealed record GameCompanionRecipeResult(
    GameCompanionRecipe? Recipe,
    string? ErrorMessage)
{
    public static GameCompanionRecipeResult Failure(string message) => new(null, message);
}
