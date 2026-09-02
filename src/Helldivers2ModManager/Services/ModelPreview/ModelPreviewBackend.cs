using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Frozen;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Model-preview orchestration boundary.
///
/// PatchResourceInspectionService owns the low-level patch/GPU/texture readers used by
/// the resource viewer. This service owns the model asset graph: it resolves Unit
/// package identities, attaches armor membership to each decoded section, and exposes
/// the alternatives that the UI can select without changing deployment state.
/// Keeping this boundary explicit means a model can be assembled from many Unit parts,
/// while a shared Unit remains visible for every armor that reuses it.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModelPreviewBackend
{
    private static readonly Regex ArchiveIdRegex = new("(?<![0-9a-f])[0-9a-f]{16}(?![0-9a-f])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly PatchResourceInspectionService _inspectionService;
    private readonly VersionCheckService _versionCheckService;
    private readonly ILogger<ModelPreviewBackend> _logger;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _armorNames;

    public ModelPreviewBackend(
        PatchResourceInspectionService inspectionService,
        VersionCheckService versionCheckService,
        ILogger<ModelPreviewBackend> logger)
    {
        _inspectionService = inspectionService;
        _versionCheckService = versionCheckService;
        _logger = logger;
        _armorNames = new Lazy<IReadOnlyDictionary<string, string>>(LoadArmorNames);
    }

    public async Task<ModelPreviewResult> PreviewModelAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken = default)
    {
        var result = await _inspectionService.PreviewModelAsync(modDirectory, patchFiles, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await AttachArmorMembershipAsync(result, cancellationToken);
        await AttachAnimationLibrariesAsync(result, cancellationToken);
        return result;
    }

    /// <summary>
    /// 从游戏归档按需解析原版贴图（模组材质引用模组未携带的贴图资源时使用）。
    /// 定位表与 Unit 索引共用同一次全库扫描；游戏未配置或单张贴图失败都不抛出——
    /// 原版解析是增强，不是前提。
    /// </summary>
    public async Task<IReadOnlyDictionary<ulong, GameUnitReferenceReader.GameOriginalTexture>> ReadOriginalTexturesAsync(
        IReadOnlyList<ulong> textureFileIds,
        int maxPreviewPixels,
        CancellationToken cancellationToken = default)
    {
        if (textureFileIds.Count == 0)
            return new Dictionary<ulong, GameUnitReferenceReader.GameOriginalTexture>();

        try
        {
            return await _versionCheckService.ReadOriginalTexturesAsync(
                textureFileIds,
                maxPreviewPixels,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve original textures from the game archive for model preview");
            return new Dictionary<ulong, GameUnitReferenceReader.GameOriginalTexture>();
        }
    }

    private async Task AttachAnimationLibrariesAsync(
        ModelPreviewResult result,
        CancellationToken cancellationToken)
    {
        result.AnimationLibraries.Clear();
        var skeletons = result.Meshes
            .Select(static mesh => mesh.Skinning?.Skeleton)
            .Where(static skeleton => skeleton is not null)
            .Select(static skeleton => skeleton!)
            .Distinct()
            .ToArray();
        foreach (var skeleton in skeletons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Model resources may carry creature or vehicle state machines. Preview
                // animations are intentionally sourced only from the canonical Helldiver
                // avatar Unit; incompatible skeletons get no animation instead of an
                // approximate cross-species mapping.
                var playerLibrary = await _versionCheckService.FindCompatibleGameAnimationLibraryAsync(
                    skeleton.Bones.Select(static bone => bone.NameHash).ToArray(),
                    cancellationToken);
                if (playerLibrary is not null &&
                    result.AnimationLibraries.All(library =>
                        library.BonesId != playerLibrary.BonesId ||
                        library.StateMachineId != playerLibrary.StateMachineId))
                {
                    result.AnimationLibraries.Add(playerLibrary);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to attach the Helldiver avatar animation library");
            }
        }
    }

    private async Task AttachArmorMembershipAsync(
        ModelPreviewResult result,
        CancellationToken cancellationToken)
    {
        result.Armors.Clear();
        if (result.Meshes.Count == 0)
            return;

        var unitIds = result.Meshes
            .Select(static mesh => unchecked((long)mesh.UnitId))
            .Distinct()
            .ToArray();

        IReadOnlyDictionary<long, IReadOnlyList<string>> packageNames;
        try
        {
            packageNames = await _versionCheckService.ResolveGameUnitPackageNamesAsync(unitIds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Armor metadata is an enhancement, not a reason to discard valid model
            // geometry when the game archive is unavailable or still being indexed.
            _logger.LogDebug(ex, "Unable to resolve game armor package names for model preview");
            packageNames = new Dictionary<long, IReadOnlyList<string>>();
        }

        var membershipsByUnit = new Dictionary<long, IReadOnlyList<string>>();
        foreach (var (unitId, names) in packageNames)
        {
            var ids = names
                .Select(NormalizeArmorId)
                .Where(static id => id is not null)
                .Select(static id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ids.Length > 0)
                membershipsByUnit[unitId] = ids;
        }

        foreach (var mesh in result.Meshes)
        {
            mesh.ArmorIds = membershipsByUnit.TryGetValue(unchecked((long)mesh.UnitId), out var ids)
                ? ids
                : [];
        }

        BuildArmorOptions(result, _armorNames.Value);
    }

    /// <summary>
    /// Pure selection seam used by tests and by callers that already have package-name
    /// metadata. Unknown/shared Units remain in every filtered set so selecting a named
    /// armor never removes common body parts merely because the archive lookup lacked an
    /// alias for them.
    /// </summary>
    internal static IReadOnlyList<ModelPreviewMesh> FilterByArmor(
        IReadOnlyList<ModelPreviewMesh> meshes,
        string? armorId)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        if (string.IsNullOrWhiteSpace(armorId) || string.Equals(armorId, ModelPreviewArmorSelection.AllId, StringComparison.OrdinalIgnoreCase))
            return meshes;

        return meshes
            .Where(mesh => mesh.ArmorIds.Count == 0 ||
                          mesh.ArmorIds.Contains(armorId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    internal static void ApplyPackageNames(
        ModelPreviewResult result,
        IReadOnlyDictionary<long, IReadOnlyList<string>> packageNames,
        IReadOnlyDictionary<string, string>? armorNames = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(packageNames);
        result.Armors.Clear();

        foreach (var mesh in result.Meshes)
        {
            mesh.ArmorIds = packageNames.TryGetValue(unchecked((long)mesh.UnitId), out var names)
                ? names.Select(NormalizeArmorId)
                    .Where(static id => id is not null)
                    .Select(static id => id!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }

        BuildArmorOptions(result, armorNames);
    }

    private static void BuildArmorOptions(
        ModelPreviewResult result,
        IReadOnlyDictionary<string, string>? armorNames = null)
    {
        var allName = "All model parts";
        result.Armors.Add(new ModelPreviewArmorOption
        {
            Id = ModelPreviewArmorSelection.AllId,
            Name = allName,
            IsAll = true,
            MeshCount = result.Meshes.Count
        });

        var names = armorNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var armorId in result.Meshes
                     .SelectMany(static mesh => mesh.ArmorIds)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase))
        {
            var meshCount = result.Meshes.Count(mesh => mesh.ArmorIds.Contains(armorId, StringComparer.OrdinalIgnoreCase));
            result.Armors.Add(new ModelPreviewArmorOption
            {
                Id = armorId,
                Name = names.TryGetValue(armorId, out var name) ? name : $"Armor {armorId}",
                MeshCount = meshCount
            });
        }
    }

    private IReadOnlyDictionary<string, string> LoadArmorNames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "armor-names.json");
        try
        {
            using var stream = File.OpenRead(path);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            // 只读表冻结：FrozenDictionary 查找更快，且懒加载后不再变化
            return (values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to load model preview armor names from {Path}", path);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? NormalizeArmorId(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return null;

        var match = ArchiveIdRegex.Match(packageName);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }
}

internal static class ModelPreviewArmorSelection
{
    public const string AllId = "__all__";
}
