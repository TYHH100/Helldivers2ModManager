using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.PatchKit;
using Helldivers2ModManager.Core.Versioning;

namespace Helldivers2ModManager.Core.Repair;

public sealed record CompanionRecoveryItem(
    string PatchPath,
    string CompanionPath,
    GameCompanionKind Kind,
    bool CanRecover,
    string Reason,
    GameCompanionRecipe? Recipe = null,
    string? SourcePath = null);

public sealed record ModCompanionRecoveryPlan(IReadOnlyList<CompanionRecoveryItem> Items)
{
    public int MissingCount => Items.Count;
    public int RecoverableCount => Items.Count(item => item.CanRecover);
    public bool CanRecover => MissingCount > 0 && RecoverableCount == MissingCount;
}

public sealed record SingleCompanionRecoveryPlan(GameCompanionRecipe? Recipe, GameCompanionKind Kind, string TargetPath, string? ErrorMessage = null)
{
    public bool CanRecover => Recipe is not null;
}

public sealed record CompanionRecoveryResult(
    bool Success,
    string TargetPath = "",
    IReadOnlyList<string>? RecoveredPaths = null,
    string? ErrorMessage = null);

public sealed class CompanionRecoveryService(GameArchiveService gameArchiveService, PatchStructureAnalyzer analyzer)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static string GetSuffix(GameCompanionKind kind) => kind == GameCompanionKind.GpuResources ? ".gpu_resources" : ".stream";

    private static FileInfo[] GetPatches(DirectoryInfo directory) => directory.Exists
        ? directory.EnumerateFiles("*", SearchOption.AllDirectories).Where(file => PatchFileRules.IsMainPatchFile(file.Name)).ToArray()
        : [];

    public async Task<SingleCompanionRecoveryPlan> CreatePlanAsync(
        DirectoryInfo dataDirectory,
        FileInfo patchFile,
        GameCompanionKind kind,
        CancellationToken cancellationToken = default)
    {
        var result = await gameArchiveService.BuildCompanionRecipeAsync(dataDirectory, patchFile, kind, includePayloads: true, cancellationToken);
        return new(result.Recipe, kind, patchFile.FullName + (kind == GameCompanionKind.GpuResources ? ".gpu_resources" : ".stream"), result.ErrorMessage);
    }

    public async Task<ModCompanionRecoveryPlan> CreatePlanAsync(
        DirectoryInfo modDirectory,
        DirectoryInfo dataDirectory,
        CancellationToken cancellationToken = default)
    {
        var items = new List<CompanionRecoveryItem>();
        var patches = GetPatches(modDirectory);
        foreach (var file in patches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = await analyzer.AnalyzeFileAsync(file, cancellationToken);
            GameCompanionKind[] kinds = [GameCompanionKind.GpuResources, GameCompanionKind.Stream];
            foreach (var pair in kinds)
            {
                var missing = pair == GameCompanionKind.GpuResources
                    ? analysis.RequiresGpuResources && !analysis.HasGpuResources
                    : analysis.RequiresStream && !analysis.HasStream;
                if (!missing) continue;
                var exactSource = await FindExactSourceAsync(modDirectory, file, GetSuffix(pair), cancellationToken);
                var recipe = await gameArchiveService.BuildCompanionRecipeAsync(dataDirectory, file, pair, false, cancellationToken);
                items.Add(new(file.FullName, file.FullName + GetSuffix(pair), pair, exactSource is not null || recipe.Recipe is not null,
                    exactSource is not null ? "An exact complete patch copy is available." : recipe.ErrorMessage ?? "Unavailable", recipe.Recipe, exactSource));
            }
        }
        return new(items);
    }

    public async Task<CompanionRecoveryResult> RecoverAsync(
        DirectoryInfo dataDirectory,
        FileInfo patchFile,
        GameCompanionKind kind,
        CancellationToken cancellationToken = default)
    {
        var plan = await CreatePlanAsync(dataDirectory, patchFile, kind, cancellationToken);
        if (plan.Recipe is not { } recipe) return new(false, plan.TargetPath, ErrorMessage: plan.ErrorMessage);
        return await RecoverCoreAsync(recipe, plan.TargetPath, patchFile, cancellationToken);
    }

    public async Task<CompanionRecoveryResult> RecoverAsync(
        DirectoryInfo modDirectory,
        DirectoryInfo dataDirectory,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var plan = await CreatePlanAsync(modDirectory, dataDirectory, cancellationToken);
            if (!plan.CanRecover)
            {
                var reasons = plan.Items.Where(item => !item.CanRecover).Select(item => $"{Path.GetFileName(item.CompanionPath)}: {item.Reason}");
                return new(false, ErrorMessage: plan.MissingCount == 0 ? "NoMissingCompanions" : string.Join(Environment.NewLine, reasons));
            }

            var recovered = new List<string>();
            try
            {
                foreach (var item in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(item.CompanionPath)) throw new IOException($"The companion file appeared after planning: {item.CompanionPath}");
                    if (item.SourcePath is null && item.Recipe is not { } recipe) throw new InvalidDataException(item.Reason);
                    var result = await RecoverCoreAsync(item.Recipe, item.CompanionPath, new FileInfo(item.PatchPath), cancellationToken, item.SourcePath);
                    if (!result.Success) throw new InvalidDataException(result.ErrorMessage);
                    recovered.Add(item.CompanionPath);
                }
                foreach (var path in plan.Items.Select(item => item.PatchPath).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    analyzer.ClearCache();
                    var validation = await analyzer.AnalyzeFileAsync(new FileInfo(path), cancellationToken);
                    if ((validation.RequiresGpuResources && !validation.HasGpuResources) ||
                        (validation.RequiresStream && !validation.HasStream) ||
                        !validation.GpuResourceBoundsValid || !validation.StreamBoundsValid)
                    {
                        throw new InvalidDataException($"Recovered companion validation failed for {Path.GetFileName(path)}.");
                    }
                }
                return new(true, RecoveredPaths: recovered);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                foreach (var path in recovered.AsEnumerable().Reverse())
                    File.Delete(path);
                return new(false, ErrorMessage: exception.Message);
            }
        }
        finally { _lock.Release(); }
    }

    private async Task<CompanionRecoveryResult> RecoverCoreAsync(GameCompanionRecipe? recipe, string targetPath, FileInfo patchFile, CancellationToken cancellationToken, string? sourcePath = null)
    {
        if (File.Exists(targetPath)) return new(false, targetPath, ErrorMessage: "TargetAlreadyExists");
        var temporary = Path.Combine(Path.GetDirectoryName(targetPath)!, "." + Path.GetFileName(targetPath) + ".hd2mm-recover-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (sourcePath is not null)
            {
                File.Copy(sourcePath, temporary, true);
            }
            else
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    output.SetLength(recipe!.Length);
                    foreach (var segment in recipe.Segments)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (segment.Payload is null) continue;
                        output.Seek((long)segment.TargetOffset, SeekOrigin.Begin);
                        await output.WriteAsync(segment.Payload, cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
            }
            var validation = await analyzer.AnalyzeTemporaryFileAsync(new FileInfo(temporary), patchFile, cancellationToken);
            if (!validation.GpuResourceBoundsValid || !validation.StreamBoundsValid)
                throw new InvalidDataException("The staged companion failed validation.");
            File.Move(temporary, targetPath);
            return new(true, targetPath, [targetPath]);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<string?> FindExactSourceAsync(DirectoryInfo modDirectory, FileInfo patchFile, string suffix, CancellationToken cancellationToken)
    {
        if (modDirectory.Parent is not { Exists: true }) return null;
        var patchHash = await BackupService.ComputeSha256Async(patchFile.FullName, cancellationToken);
        foreach (var candidate in modDirectory.Parent.EnumerateFiles(patchFile.Name, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (candidate.FullName.Equals(patchFile.FullName, StringComparison.OrdinalIgnoreCase) ||
                    !patchHash.Equals(await BackupService.ComputeSha256Async(candidate.FullName, cancellationToken), StringComparison.OrdinalIgnoreCase))
                    continue;
                var companion = candidate.FullName + suffix;
                var analysis = await new PatchStructureAnalyzer().AnalyzeFileAsync(candidate, cancellationToken);
                var valid = suffix == ".gpu_resources"
                    ? analysis.HasGpuResources && analysis.GpuResourceBoundsValid
                    : analysis.HasStream && analysis.StreamBoundsValid;
                if (valid && File.Exists(companion)) return companion;
            }
            catch { }
        }
        return null;
    }
}

public enum BatchRepairState { NoAction, Repairable, Blocked, SkippedUnsupported }

public sealed record BatchRepairItem(
    Guid ModId,
    DirectoryInfo Directory,
    ModRepairPlan Plan,
    BatchRepairState State = BatchRepairState.NoAction,
    string Message = "",
    AssistedModRepairPlan? AssistedPlan = null,
    ModCompanionRecoveryPlan? CompanionPlan = null,
    int MetadataActionCount = 0,
    int AssistedActionCount = 0,
    int CompanionRecoveryCount = 0);

public sealed record BatchModRepairResult(IReadOnlyList<BatchRepairItem> Items);

public sealed class BatchRepairService(
    MetadataRepairService metadataRepairService,
    AssistedRepairService? assistedRepairService = null,
    PatchStructureAnalyzer? analyzer = null,
    CompanionRecoveryService? companionRecoveryService = null,
    Func<DirectoryInfo?>? gameDataDirectoryProvider = null)
{
    private readonly AssistedRepairService? _assistedRepairService = assistedRepairService;
    private readonly PatchStructureAnalyzer _analyzer = analyzer ?? new();
    private readonly CompanionRecoveryService? _companionRecoveryService = companionRecoveryService;
    private readonly Func<DirectoryInfo?>? _gameDataDirectoryProvider = gameDataDirectoryProvider;

    public async Task<IReadOnlyList<BatchRepairItem>> CreatePlanAsync(IEnumerable<(Guid Id, DirectoryInfo Directory)> mods, CancellationToken cancellationToken = default)
    {
        var items = new List<BatchRepairItem>();
        foreach (var (id, directory) in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await HasUnitsAsync(directory, cancellationToken))
            {
                items.Add(new(id, directory, new([], []), BatchRepairState.SkippedUnsupported, "No supported Unit resources"));
                continue;
            }

            var gameDataDirectory = _gameDataDirectoryProvider?.Invoke();
            if (_companionRecoveryService is not null && gameDataDirectory is not null)
            {
                var companionPlan = await _companionRecoveryService.CreatePlanAsync(
                    directory,
                    gameDataDirectory,
                    cancellationToken).ConfigureAwait(false);
                if (companionPlan.MissingCount > 0)
                {
                    var state = companionPlan.CanRecover ? BatchRepairState.Repairable : BatchRepairState.Blocked;
                    var message = companionPlan.CanRecover
                        ? $"{companionPlan.RecoverableCount} missing companion file(s) can be recovered before patch repair."
                        : string.Join(Environment.NewLine, companionPlan.Items.Where(item => !item.CanRecover).Select(item => $"{Path.GetFileName(item.CompanionPath)}: {item.Reason}"));
                    items.Add(new(id, directory, new([], []), state, message, CompanionPlan: companionPlan, CompanionRecoveryCount: companionPlan.RecoverableCount));
                    continue;
                }
            }

            var plan = await metadataRepairService.CreatePlanAsync(directory, cancellationToken).ConfigureAwait(false);
            if (plan.CanRepair)
                items.Add(new(id, directory, plan, BatchRepairState.Repairable, $"{plan.ActionCount} safe metadata repair(s)", MetadataActionCount: plan.ActionCount));
            else if (_assistedRepairService is not null)
            {
                var assisted = await _assistedRepairService.CreateAutomaticPlanAsync(directory, cancellationToken).ConfigureAwait(false);
                var state = assisted.CanRepair ? BatchRepairState.Repairable : assisted.BlockingReasons.Count > 0 ? BatchRepairState.Blocked : BatchRepairState.NoAction;
                items.Add(new(id, directory, plan, state,
                    assisted.CanRepair ? $"{assisted.Actions.Count} assisted repair(s)" : string.Join(Environment.NewLine, assisted.BlockingReasons),
                    assisted,
                    MetadataActionCount: 0,
                    AssistedActionCount: assisted.Actions.Count + assisted.MaterialActions.Count));
            }
            else items.Add(new(id, directory, plan, plan.BlockingReasons.Count > 0 ? BatchRepairState.Blocked : BatchRepairState.NoAction, string.Join(Environment.NewLine, plan.BlockingReasons)));
        }
        return items;
    }

    public async Task<BatchModRepairResult> RepairAsync(IReadOnlyList<BatchRepairItem> items, CancellationToken cancellationToken = default)
    {
        var repaired = new List<BatchRepairItem>();
        var skipped = new List<BatchRepairItem>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (item.State == BatchRepairState.SkippedUnsupported)
                {
                    repaired.Add(item with { State = BatchRepairState.SkippedUnsupported });
                    continue;
                }

                var companionCount = 0;
                if (item.CompanionPlan is { } companionPlan)
                {
                if (_companionRecoveryService is null || _gameDataDirectoryProvider?.Invoke() is not { } recoveryGameData)
                    throw new InvalidOperationException("Companion recovery service is not configured.");
                    var companionResult = await _companionRecoveryService.RecoverAsync(
                        item.Directory,
                        recoveryGameData,
                        cancellationToken).ConfigureAwait(false);
                    if (!companionResult.Success)
                        throw new InvalidDataException(companionResult.ErrorMessage);
                    companionCount = companionResult.RecoveredPaths?.Count ?? 0;
                }

                var changed = false;
                var metadataCount = 0;
                var plan = item.CompanionPlan is null && item.MetadataActionCount > 0
                    ? await metadataRepairService.CreatePlanAsync(item.Directory, cancellationToken).ConfigureAwait(false)
                    : item.Plan;
                if (plan.CanRepair)
                {
                    var metadataResult = await metadataRepairService.RepairAsync(item.Directory, cancellationToken).ConfigureAwait(false);
                    if (!metadataResult.Success)
                        throw new InvalidDataException(metadataResult.ErrorMessage);
                    metadataCount = metadataResult.AppliedActionCount;
                    changed |= metadataCount > 0;
                }

                var assistedCount = 0;
                if (_assistedRepairService is not null)
                {
                    var assistedPlan = await _assistedRepairService.CreateAutomaticPlanAsync(item.Directory, cancellationToken).ConfigureAwait(false);
                    if (assistedPlan.CanRepair)
                    {
                        var assistedResult = await _assistedRepairService.RepairAsync(
                            item.Directory,
                            automatic: true,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (!assistedResult.Success)
                            throw new InvalidDataException(assistedResult.ErrorMessage);
                        assistedCount = assistedResult.AppliedActionCount;
                        changed |= assistedCount > 0;
                    }
                    else if (assistedPlan.BlockingReasons.Count > 0)
                    {
                        throw new InvalidDataException(string.Join(Environment.NewLine, assistedPlan.BlockingReasons));
                    }
                }

                repaired.Add(item with
                {
                    State = changed ? BatchRepairState.NoAction : BatchRepairState.NoAction,
                    MetadataActionCount = metadataCount,
                    AssistedActionCount = assistedCount,
                    CompanionRecoveryCount = companionCount,
                    Message = changed
                        ? $"Recovered {companionCount} companion file(s), applied {metadataCount} metadata repair(s) and {assistedCount} Unit repair(s)."
                        : "No repair was required after the plan was refreshed."
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { skipped.Add(item with { State = BatchRepairState.Blocked, Message = exception.Message }); }
        }
        return new(repaired.Concat(skipped).ToArray());
    }

    private static string Suffix(GameCompanionKind kind) => kind == GameCompanionKind.GpuResources ? ".gpu_resources" : ".stream";

    private static FileInfo[] EnumeratePatches(DirectoryInfo directory) => directory.Exists
        ? directory.EnumerateFiles("*", SearchOption.AllDirectories).Where(file => PatchFileRules.IsMainPatchFile(file.Name)).ToArray()
        : [];

    private async Task<bool> HasUnitsAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        foreach (var file in EnumeratePatches(directory))
        {
            var parsed = await new PatchFileParser().ParseFileAsync(file, options: null, cancellationToken);
            if (parsed.Snapshot?.Entries.Any(entry => entry.TypeId == PatchFileParser.UnitTypeId) == true) return true;
        }
        return false;
    }
}
