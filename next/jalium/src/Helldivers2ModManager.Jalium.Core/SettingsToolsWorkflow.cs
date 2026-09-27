namespace Helldivers2ModManager.Jalium.Core;

internal readonly record struct HardPurgeResult(int Deleted, int Failed);

internal static class SettingsToolsWorkflow
{
    public static Task<HardPurgeResult> HardPurgeAsync(string storageDirectory,
        string gameDirectory, Action<double>? reportProgress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => HardPurge(storageDirectory, gameDirectory,
            reportProgress, cancellationToken), cancellationToken);

    private static HardPurgeResult HardPurge(string storageDirectory, string gameDirectory,
        Action<double>? reportProgress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || string.IsNullOrWhiteSpace(storageDirectory))
            throw new ArgumentException("Game and storage directories are required.");
        cancellationToken.ThrowIfCancellationRequested();
        var gameRoot = Path.GetFullPath(gameDirectory);
        var dataRoot = Path.GetFullPath(Path.Combine(gameRoot, "data"));
        if (!dataRoot.StartsWith(gameRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Game data path escaped the game directory.");
        var data = new DirectoryInfo(dataRoot);
        if (!data.Exists || (data.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("Game data directory is missing or redirected.");
        var storageRoot = Path.GetFullPath(storageDirectory);
        var installed = Path.GetFullPath(Path.Combine(storageRoot, "installed.txt"));
        if (!installed.StartsWith(storageRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installed record escaped the storage directory.");

        var files = data.EnumerateFiles("*.patch_*", SearchOption.TopDirectoryOnly).ToArray();
        var deleted = 0;
        var failed = 0;
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { file.Delete(); deleted++; }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
            reportProgress?.Invoke((double)(index + 1) / files.Length);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(installed))
        {
            try { File.Delete(installed); deleted++; }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }
        if (files.Length == 0)
            reportProgress?.Invoke(1);
        return new HardPurgeResult(deleted, failed);
    }
}
