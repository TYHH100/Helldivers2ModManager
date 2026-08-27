namespace Helldivers2ModManager.Core.PatchKit;

public sealed record PatchKitOptions
{
    public static PatchKitOptions Default { get; } = new();

    public int MaxTypes { get; init; } = 1_000;
    public int MaxFiles { get; init; } = 100_000;
    public int MaxStreamsPerUnit { get; init; } = 100;
    public int MaxComponentsPerStream { get; init; } = 16;
    public long MaxRandomReadBytes { get; init; } = 512L * 1024 * 1024;
}
