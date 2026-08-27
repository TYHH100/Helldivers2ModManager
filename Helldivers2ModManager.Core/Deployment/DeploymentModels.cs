namespace Helldivers2ModManager.Core.Deployment;

public sealed record ModDeploymentInput(
    Guid Guid,
    DirectoryInfo Directory,
    Mods.IModManifest Manifest,
    IReadOnlyList<bool> EnabledOptions,
    IReadOnlyList<int> SelectedOptions);

public sealed record DeploymentOptions(
    DirectoryInfo GameDataDirectory,
    bool UseSymbolicLinks,
    IReadOnlyCollection<string> SkipList)
{
    public static DeploymentOptions Copy(DirectoryInfo gameDataDirectory, IReadOnlyCollection<string>? skipList = null) =>
        new(gameDataDirectory, false, skipList ?? []);
}

public sealed record DeploymentPlan(
    IReadOnlyList<ModDeploymentInput> Mods,
    IReadOnlyList<PatchDeploymentItem> Files,
    int PlaceholderCount);

public sealed record PatchDeploymentItem(
    Guid ModGuid,
    string? SourcePath,
    string DestinationPath,
    long Size);

public sealed record DeploymentProgress(double Progress, int CompletedFiles, int TotalFiles, string CurrentFile);

public sealed record DeploymentFileProgress(
    Guid ModGuid,
    PatchDeploymentItem Item,
    long CompletedBytes,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles);

public sealed record DeploymentStepCallbacks(
    Action<Guid>? ModStarted = null,
    Action<DeploymentFileProgress>? FileCopied = null,
    Action<Guid>? ModCompleted = null,
    Action<Guid, Exception>? ModFailed = null)
{
    public static DeploymentStepCallbacks FromGlobalProgress(IProgress<DeploymentProgress>? progress) => new(
        FileCopied: item => progress?.Report(new(
            ((double)item.CompletedFiles + (item.TotalBytes == 0 ? 1 : (double)item.CompletedBytes / item.TotalBytes)) / item.TotalFiles,
            item.CompletedFiles,
            item.TotalFiles,
            Path.GetFileName(item.Item.SourcePath ?? item.Item.DestinationPath))));
}
