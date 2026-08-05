namespace Helldivers2ModManager.Models;

/// <summary>
/// Guards a single user-requested texture decode against model switches and page
/// disposal. Source-resolution images can be hundreds of MiB, so stale work must never
/// become visible or reattach its bitmap after the preview has released it.
/// </summary>
internal static class ModelPreviewTextureRequestState
{
    public static bool IsCurrent(int requestGeneration, int currentGeneration, bool cancellationRequested) =>
        !cancellationRequested && requestGeneration == currentGeneration;
}
