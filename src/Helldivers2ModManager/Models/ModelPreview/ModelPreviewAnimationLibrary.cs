using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace Helldivers2ModManager.Models;

internal sealed class ModelPreviewAnimationLibrary
{
    public required ulong BonesId { get; init; }
    public required ulong StateMachineId { get; init; }
    public required IReadOnlyList<uint> BoneHashes { get; init; }
    public required IReadOnlyList<ModelPreviewAnimationOption> Animations { get; init; }
    /// <summary>
    /// 游戏本体 Unit 的变换层级（源参考系）。clip 轨道局部 TRS 沿它摆出游戏
    /// 姿态，再以世界差量 S⁻¹·A 转移到目标（模组）骨架——处理模组骨架
    /// 局部轴/父链与游戏不一致的重导出场景。null 时绑定退回局部差量路径。
    /// </summary>
    public ModelPreviewSkeleton? SourceSkeleton { get; init; }
    /// <summary>
    /// True when the library was built from resources bundled in the mod patch itself
    /// (added or modified actions) rather than resolved from the game archive.
    /// </summary>
    public bool IsFromMod { get; init; }
}

internal sealed class ModelPreviewAnimationOption
{
    public required ulong AnimationId { get; init; }
    public required ulong StateNameHash { get; init; }
    public required int LayerIndex { get; init; }
    public required ModelPreviewAnimationClip Clip { get; init; }
    public string DisplayName => ModelPreviewAnimationNames.GetDisplayName(AnimationId, StateNameHash);
}

/// <summary>
/// 动画 ID → 人类可读名称。名称主源是 Resources/Data/animation-names.json
/// （社区 "Helldivers 2 Archive Labeling" 表的 Animation IDs 页，874 个条目），
/// 中文表 animation-names-zh.json（地狱老司机 Archive ID 中文收集表-动画ID收集）
/// 覆盖同名键；两者键均为 16 位小写十六进制 archive ID，与 armor/helmet 名称表
/// 同约定。内置 FallbackNames 仅在两个文件都缺失或损坏时兜底。
/// </summary>
internal static class ModelPreviewAnimationNames
{
    private static readonly IReadOnlyDictionary<ulong, string> FallbackNames = new Dictionary<ulong, string>
    {
        [25247180846449471] = "Prone Wounded Strafing Right(?)",
        [90655527730108336] = "Prone Pistol Aiming Left Breathing(Injured?)",
        [132594416690666555] = "Crawling to Standing",
        [174977385746011720] = "Crouch walk into Crouch rest",
        [216782799318029858] = "Crouch Rest to Prone",
        [358551434572852177] = "Prone, crawl backward and to the left",
        [358807758360480437] = "Downed state(?), crawl backwards",
        [404260333466794186] = "Standing, turning away and running",
        [432909579045845230] = "Sitting backward to prone crawl transition",
        [456372530071472393] = "Prone, looking right, stab with two hand",
        [474377013447375633] = "Crouch walk into Prone transition",
        [508500488933616914] = "Crouch, walking backwards to the left",
        [519512256433322988] = "Prone, looking backwards, holding two hand weapon",
        [571422367076691216] = "Standing, sprinting to the left"
    };

    private static readonly Lazy<IReadOnlyDictionary<ulong, string>> NameTable = new(LoadNameTable);

    public static string? TryGetName(ulong animationId) =>
        NameTable.Value.TryGetValue(animationId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : FallbackNames.TryGetValue(animationId, out var fallback)
                ? fallback
                : null;

    public static string GetDisplayName(ulong animationId, ulong stateNameHash) =>
        TryGetName(animationId) is { } name
            ? $"{name} / 0x{animationId:X16}"
            : $"0x{animationId:X16} / State 0x{stateNameHash:X16}";

    /// <summary>纯函数解析，供测试与加载共用；非法键（含「数据来自」元数据键）静默跳过。</summary>
    internal static IReadOnlyDictionary<ulong, string> ParseNameTable(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                     ?? new Dictionary<string, string>();
        var table = new Dictionary<ulong, string>();
        foreach (var (key, name) in values)
        {
            if (key.Length != 16 || !ulong.TryParse(key, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var id))
                continue;
            if (!string.IsNullOrWhiteSpace(name))
                table[id] = name;
        }
        return table;
    }

    /// <summary>
    /// 名称合成：英文主表（社区 Animation IDs 页）打底，中文表（地狱老司机
    /// Archive ID 中文收集表-动画ID收集）覆盖同名键；中文表里的 "Unknown"
    /// 只是英文占位的照抄，不覆盖可能存在的有效英文名。
    /// </summary>
    internal static IReadOnlyDictionary<ulong, string> MergeNameTables(
        IReadOnlyDictionary<ulong, string> baseTable,
        IReadOnlyDictionary<ulong, string>? overlayTable)
    {
        var merged = new Dictionary<ulong, string>(baseTable);
        if (overlayTable is null)
            return merged;
        foreach (var (id, name) in overlayTable)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            merged[id] = name;
        }
        return merged;
    }

    private static IReadOnlyDictionary<ulong, string> LoadNameTable()
    {
        IReadOnlyDictionary<ulong, string>? chineseTable = null;
        try
        {
            var chinesePath = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "animation-names-zh.json");
            chineseTable = ParseNameTable(File.ReadAllText(chinesePath));
        }
        catch (Exception)
        {
            // 中文表是展示增强；缺失/损坏时仍使用英文表。
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "animation-names.json");
            return MergeNameTables(ParseNameTable(File.ReadAllText(path)), chineseTable);
        }
        catch (Exception)
        {
            // 名称表是展示增强；文件缺失/损坏时回退到内置子集，不影响动画播放。
            return MergeNameTables(FallbackNames, chineseTable);
        }
    }
}

internal readonly record struct ModelPreviewAnimationReference(
    ulong AnimationId,
    ulong StateNameHash,
    int LayerIndex);

internal static class ModelPreviewAnimationCompatibility
{
    public const int MinimumMatchingBones = 16;
    public const float MinimumBoneCoverage = 0.60f;
    // 头盔/头部等部件 Unit 的骨架可能只携带少数骨骼。只要这些骨骼几乎全部被动画
    // 覆盖，就应当让部件跟随动画（未命中的骨骼保持绑定姿态），而不是把整个部件
    // 冻在绑定姿态上——那是播放时护甲与身体错位的来源之一。覆盖比例要求高
    // （90%）以避免把仅偶然共享少量骨骼哈希的武器/道具骨架误判为可动画。
    public const int MinimumCoveredSkeletonBones = 4;
    public const float SkeletonCoverageForPartialRigs = 0.90f;

    public static bool IsCompatible(
        ModelPreviewSkeleton skeleton,
        ModelPreviewAnimationLibrary library)
    {
        if (skeleton.BonesId == library.BonesId &&
            (skeleton.StateMachineId == 0 || skeleton.StateMachineId == library.StateMachineId))
        {
            return true;
        }

        return IsCompatibleHashes(
            CollectBoneHashes(skeleton.Bones.Select(static bone => bone.NameHash)),
            CollectBoneHashes(library.BoneHashes));
    }

    /// <summary>
    /// 骨架/动画哈希层面的兼容性判定，库挂接（GameUnitReferenceReader）与播放期
    /// 过滤必须共用同一实现，避免两处规则漂移。
    /// </summary>
    internal static bool IsCompatibleHashes(
        HashSet<uint> transformHashes,
        HashSet<uint> animationHashes)
    {
        if (transformHashes.Count == 0 || animationHashes.Count == 0)
            return false;

        var matchingBones = animationHashes.Count(transformHashes.Contains);
        if (matchingBones >= MinimumMatchingBones &&
            matchingBones >= animationHashes.Count * MinimumBoneCoverage &&
            matchingBones >= transformHashes.Count * MinimumBoneCoverage)
        {
            return true;
        }

        return matchingBones >= MinimumCoveredSkeletonBones &&
               matchingBones >= transformHashes.Count * SkeletonCoverageForPartialRigs;
    }

    internal static HashSet<uint> CollectBoneHashes(IEnumerable<uint> hashes) =>
        hashes.Where(static hash => hash != 0).ToHashSet();
}

internal static class ModelPreviewAnimationLibraryParser
{
    private const int MaxBones = 4096;
    private const int MaxLayers = 64;
    private const int MaxStatesPerLayer = 4096;
    private const int MaxAnimationsPerState = 4096;

    public static IReadOnlyList<uint> ParseBoneHashes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new InvalidDataException("Bones resource is smaller than its header.");

        var boneCount = ReadCount(data, 0, MaxBones, "bone name");
        var lodCount = ReadCount(data, 4, MaxBones, "bone LOD");
        var hashesOffset = checked(8 + lodCount * sizeof(float));
        EnsureRange(data, hashesOffset, checked(boneCount * sizeof(uint)), "bone hash table");

        var hashes = new uint[boneCount];
        for (var index = 0; index < hashes.Length; index++)
            hashes[index] = BinaryPrimitives.ReadUInt32LittleEndian(data[(hashesOffset + index * sizeof(uint))..]);
        return hashes;
    }

    public static IReadOnlyList<ModelPreviewAnimationReference> ParseStateMachineAnimations(ReadOnlySpan<byte> data)
    {
        const int headerSize = 76;
        if (data.Length < headerSize)
            throw new InvalidDataException("State machine resource is smaller than its header.");

        var declaredLayerCount = ReadCount(data, 4, MaxLayers, "state machine layer");
        var layerDataOffset = ReadOffset(data, 8, data.Length, "state machine layer table");
        EnsureRange(data, layerDataOffset, sizeof(uint), "state machine layer count");
        var layerCount = ReadCount(data, layerDataOffset, MaxLayers, "state machine layer");
        if (declaredLayerCount != 0 && layerCount != declaredLayerCount)
            throw new InvalidDataException("State machine layer counts do not match.");

        var layerOffsetsStart = checked(layerDataOffset + sizeof(uint));
        EnsureRange(data, layerOffsetsStart, checked(layerCount * sizeof(uint)), "state machine layer offsets");
        var references = new List<ModelPreviewAnimationReference>();
        for (var layerIndex = 0; layerIndex < layerCount; layerIndex++)
        {
            var relativeLayerOffset = ReadOffset(data, layerOffsetsStart + layerIndex * sizeof(uint), data.Length, "state machine layer");
            var layerOffset = checked(layerDataOffset + relativeLayerOffset);
            EnsureRange(data, layerOffset, 12, "state machine layer header");

            var stateCount = ReadCount(data, layerOffset + 8, MaxStatesPerLayer, "state");
            var stateOffsetsStart = checked(layerOffset + 12);
            EnsureRange(data, stateOffsetsStart, checked(stateCount * sizeof(uint)), "state offsets");
            for (var stateIndex = 0; stateIndex < stateCount; stateIndex++)
            {
                var relativeStateOffset = ReadOffset(data, stateOffsetsStart + stateIndex * sizeof(uint), data.Length, "state");
                var stateOffset = checked(layerOffset + relativeStateOffset);
                EnsureRange(data, stateOffset, 112, "state header");

                var stateNameHash = BinaryPrimitives.ReadUInt64LittleEndian(data[stateOffset..]);
                var animationCount = ReadCount(data, stateOffset + 12, MaxAnimationsPerState, "state animation");
                var relativeAnimationOffset = ReadOffset(data, stateOffset + 16, data.Length, "state animation table");
                var animationOffset = checked(stateOffset + relativeAnimationOffset);
                EnsureRange(data, animationOffset, checked(animationCount * sizeof(ulong)), "state animation IDs");
                for (var animationIndex = 0; animationIndex < animationCount; animationIndex++)
                {
                    var animationId = BinaryPrimitives.ReadUInt64LittleEndian(
                        data[(animationOffset + animationIndex * sizeof(ulong))..]);
                    references.Add(new ModelPreviewAnimationReference(animationId, stateNameHash, layerIndex));
                }
            }
        }

        return references
            .GroupBy(static reference => reference.AnimationId)
            .Select(static group => group.First())
            .ToArray();
    }

    private static int ReadCount(ReadOnlySpan<byte> data, int offset, int maximum, string description)
    {
        EnsureRange(data, offset, sizeof(uint), description);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        if (value > maximum)
            throw new InvalidDataException($"{description} count {value} is outside the supported range.");
        return (int)value;
    }

    private static int ReadOffset(ReadOnlySpan<byte> data, int offset, int length, string description)
    {
        EnsureRange(data, offset, sizeof(uint), description);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        if (value > int.MaxValue || value > length)
            throw new InvalidDataException($"{description} offset exceeds the resource boundary.");
        return (int)value;
    }

    private static void EnsureRange(ReadOnlySpan<byte> data, int offset, int size, string description)
    {
        if (offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset)
            throw new InvalidDataException($"{description} exceeds the resource boundary.");
    }
}

internal sealed class ModelPreviewAnimationBinding
{
    private readonly ModelPreviewSkeleton _skeleton;
    private readonly ModelPreviewSkeleton? _sourceSkeleton;
    private readonly ModelPreviewAnimationClip _clip;
    private readonly int[] _animationBoneByTransform;
    private readonly Matrix4x4[] _restLocalTransforms;
    private readonly BoneTrack[] _tracks;
    private readonly Dictionary<uint, int> _trackByHash;
    private readonly int[] _sourceBoneByTransform;
    private readonly Matrix4x4[] _sourceRestLocalTransforms;

    public ModelPreviewAnimationBinding(
        ModelPreviewSkeleton skeleton,
        IReadOnlyList<uint> animationBoneHashes,
        ModelPreviewAnimationClip clip)
        : this(skeleton, animationBoneHashes, clip, sourceSkeleton: null)
    {
    }

    public ModelPreviewAnimationBinding(
        ModelPreviewSkeleton skeleton,
        IReadOnlyList<uint> animationBoneHashes,
        ModelPreviewAnimationClip clip,
        ModelPreviewSkeleton? sourceSkeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(animationBoneHashes);
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.BoneCount > animationBoneHashes.Count)
            throw new InvalidDataException("Animation has more bones than its Bones resource.");

        _skeleton = skeleton;
        _sourceSkeleton = sourceSkeleton;
        _clip = clip;
        _tracks = Enumerable.Range(0, clip.BoneCount).Select(static _ => new BoneTrack()).ToArray();
        foreach (var keyframe in clip.Keyframes)
        {
            if (keyframe.BoneIndex < 0 || keyframe.BoneIndex >= _tracks.Length)
                continue;
            _tracks[keyframe.BoneIndex].Add(keyframe);
        }

        var animationIndexByHash = animationBoneHashes
            .Take(clip.BoneCount)
            .Select(static (hash, index) => (hash, index))
            .GroupBy(static item => item.hash)
            .ToDictionary(static group => group.Key, static group => group.First().index);
        _trackByHash = animationIndexByHash;
        _animationBoneByTransform = new int[skeleton.Bones.Count];
        Array.Fill(_animationBoneByTransform, -1);
        for (var index = 0; index < skeleton.Bones.Count; index++)
            if (animationIndexByHash.TryGetValue(skeleton.Bones[index].NameHash, out var animationIndex))
                _animationBoneByTransform[index] = animationIndex;

        _restLocalTransforms = BuildRestLocalTransforms(skeleton.Bones);

        if (sourceSkeleton is null)
        {
            _sourceBoneByTransform = [];
            _sourceRestLocalTransforms = [];
            return;
        }

        // 源（游戏本体）骨架：clip 轨道经哈希定位到源骨骼，沿源层级摆出游戏
        // 姿态后以世界差量转移到目标骨架。模组重导出骨架的局部轴/父链与游戏
        // 不一致，局部差量公式在该场景不成立（关节绕错轴），必须走世界差量。
        _sourceRestLocalTransforms = BuildRestLocalTransforms(sourceSkeleton.Bones);
        _sourceBoneByTransform = new int[skeleton.Bones.Count];
        Array.Fill(_sourceBoneByTransform, -1);
        var sourceIndexByHash = new Dictionary<uint, int>();
        for (var index = 0; index < sourceSkeleton.Bones.Count; index++)
            sourceIndexByHash.TryAdd(sourceSkeleton.Bones[index].NameHash, index);
        for (var index = 0; index < skeleton.Bones.Count; index++)
            if (sourceIndexByHash.TryGetValue(skeleton.Bones[index].NameHash, out var sourceIndex))
                _sourceBoneByTransform[index] = sourceIndex;
    }

    public Matrix4x4[] SampleSkinningTransforms(float timeSeconds)
    {
        var time = _clip.LengthSeconds > 0
            ? Math.Clamp(timeSeconds % _clip.LengthSeconds, 0, _clip.LengthSeconds)
            : 0;
        return _sourceSkeleton is not null
            ? SampleWithSourceSkeleton(time)
            : SampleWithLocalDeltas(time);
    }

    /// <summary>
    /// 源骨架路径（游戏本体 Unit 层级可用时的首选）：clip 轨道局部 TRS 是游戏
    /// 骨架层级内的绝对局部姿态（初始姿态即参考系，游戏引擎直接套用），沿源
    /// 层级合成世界矩阵 A；蒙皮差量 G = S⁻¹·A（S 为源绑定）是与局部轴无关的
    /// 世界差量——旋转轨道等价于绕目标骨骼自身位置的纯旋转，可直接应用于
    /// 模组网格。未命中源骨骼的模组骨骼（裙摆/头发等自定义骨骼）保持 rest
    /// 局部并跟随已动画的父链。
    /// </summary>
    private Matrix4x4[] SampleWithSourceSkeleton(float time)
    {
        var sourceBones = _sourceSkeleton!.Bones;
        var sourceCount = sourceBones.Count;

        var sourceLocals = new Matrix4x4[sourceCount];
        for (var index = 0; index < sourceCount; index++)
        {
            var parentIndex = sourceBones[index].ParentIndex;
            if (parentIndex < 0 || parentIndex >= sourceCount)
            {
                // 根位移/旋转由游戏角色控制器消耗，预览中保持绑定姿态。
                sourceLocals[index] = _sourceRestLocalTransforms[index];
                continue;
            }

            if (!_trackByHash.TryGetValue(sourceBones[index].NameHash, out var trackIndex))
            {
                sourceLocals[index] = _sourceRestLocalTransforms[index];
                continue;
            }

            var initial = _clip.InitialPoses[trackIndex];
            var track = _tracks[trackIndex];
            var position = track.SamplePosition(initial.Position, time);
            var rotation = track.SampleRotation(initial.Rotation, time);
            var scale = _clip.IsAdditive
                ? Vector3.One
                : track.SampleScale(initial.Scale, time);
            var animatedLocal = CreatePoseTransform(position, rotation, scale);
            if (_clip.IsAdditive)
            {
                var initialLocal = CreatePoseTransform(initial.Position, initial.Rotation, initial.Scale);
                sourceLocals[index] = Matrix4x4.Invert(initialLocal, out var inverseInitial)
                    ? _sourceRestLocalTransforms[index] * inverseInitial * animatedLocal
                    : _sourceRestLocalTransforms[index];
            }
            else
            {
                sourceLocals[index] = animatedLocal;
            }
        }

        var sourceWorlds = new Matrix4x4[sourceCount];
        var sourceVisitState = new byte[sourceCount];
        for (var index = 0; index < sourceCount; index++)
            ResolveGlobalTransform(index, sourceLocals, sourceWorlds, sourceVisitState, sourceBones);

        // G = S⁻¹·A：与目标骨架无关的世界差量，可整体预算。
        var sourceDeltas = new Matrix4x4[sourceCount];
        for (var index = 0; index < sourceCount; index++)
            sourceDeltas[index] = Matrix4x4.Invert(sourceBones[index].BindTransform, out var inverseBind)
                ? inverseBind * sourceWorlds[index]
                : Matrix4x4.Identity;

        // 目标骨架：命中源骨骼 → T·G；未命中 → rest 局部沿目标父链合成。
        var targetBones = _skeleton.Bones;
        var targetCount = targetBones.Count;
        var targetWorlds = new Matrix4x4[targetCount];
        var targetVisitState = new byte[targetCount];
        for (var index = 0; index < targetCount; index++)
            ResolveTargetWorld(index, targetWorlds, targetVisitState, targetBones, sourceDeltas);

        var skinningTransforms = new Matrix4x4[targetCount];
        for (var index = 0; index < targetCount; index++)
        {
            skinningTransforms[index] = Matrix4x4.Invert(targetBones[index].BindTransform, out var inverseBind)
                ? inverseBind * targetWorlds[index]
                : Matrix4x4.Identity;
        }
        return skinningTransforms;
    }

    private void ResolveTargetWorld(
        int index,
        Matrix4x4[] targetWorlds,
        byte[] visitState,
        IReadOnlyList<ModelPreviewSkeletonBone> targetBones,
        Matrix4x4[] sourceDeltas)
    {
        if (visitState[index] == 2)
            return;
        if (visitState[index] == 1)
        {
            // 层级成环：退回绑定姿态，避免无限递归。
            targetWorlds[index] = targetBones[index].BindTransform;
            visitState[index] = 2;
            return;
        }

        visitState[index] = 1;
        var parentIndex = targetBones[index].ParentIndex;
        var sourceIndex = _sourceBoneByTransform[index];
        if (sourceIndex >= 0)
        {
            targetWorlds[index] = targetBones[index].BindTransform * sourceDeltas[sourceIndex];
        }
        else if (parentIndex >= 0 && parentIndex < targetWorlds.Length)
        {
            ResolveTargetWorld(parentIndex, targetWorlds, visitState, targetBones, sourceDeltas);
            targetWorlds[index] = _restLocalTransforms[index] * targetWorlds[parentIndex];
        }
        else
        {
            targetWorlds[index] = _restLocalTransforms[index];
        }

        visitState[index] = 2;
    }

    /// <summary>
    /// 局部差量路径（无源骨架时的兜底，如模组自带动作库）：把 clip 局部 TRS
    /// 表达为相对源参考系的差量（initial⁻¹ · animated）后迁移到目标骨架 rest 上。
    /// 当目标骨架的局部轴/父链与源参考一致时与绝对路径等价；重导出骨架场景下
    /// 会绕错轴，仅作为没有更好数据时的回退。
    /// </summary>
    private Matrix4x4[] SampleWithLocalDeltas(float time)
    {
        var localTransforms = new Matrix4x4[_skeleton.Bones.Count];
        for (var transformIndex = 0; transformIndex < localTransforms.Length; transformIndex++)
        {
            var parentIndex = _skeleton.Bones[transformIndex].ParentIndex;
            if (parentIndex < 0 || parentIndex >= localTransforms.Length)
            {
                // Root translation/rotation is consumed by the game's character
                // controller. Applying it to preview geometry rotates or displaces the
                // entire model, so keep every hierarchy root in its bind pose.
                localTransforms[transformIndex] = _restLocalTransforms[transformIndex];
                continue;
            }

            var animationBoneIndex = _animationBoneByTransform[transformIndex];
            if (animationBoneIndex < 0)
            {
                localTransforms[transformIndex] = _restLocalTransforms[transformIndex];
                continue;
            }

            var initial = _clip.InitialPoses[animationBoneIndex];
            var track = _tracks[animationBoneIndex];
            var position = track.SamplePosition(initial.Position, time);
            var rotation = track.SampleRotation(initial.Rotation, time);
            var scale = _clip.IsAdditive
                ? Vector3.One
                : track.SampleScale(initial.Scale, time);
            var animatedLocal = CreatePoseTransform(position, rotation, scale);
            if (_clip.IsAdditive)
            {
                localTransforms[transformIndex] = _restLocalTransforms[transformIndex] * animatedLocal;
            }
            else
            {
                var initialLocal = CreatePoseTransform(initial.Position, initial.Rotation, initial.Scale);
                localTransforms[transformIndex] = Matrix4x4.Invert(initialLocal, out var inverseInitial)
                    ? _restLocalTransforms[transformIndex] * inverseInitial * animatedLocal
                    : _restLocalTransforms[transformIndex];
            }
        }

        var currentGlobal = new Matrix4x4[localTransforms.Length];
        var visitState = new byte[localTransforms.Length];
        for (var index = 0; index < currentGlobal.Length; index++)
            ResolveGlobalTransform(index, localTransforms, currentGlobal, visitState, _skeleton.Bones);

        var skinningTransforms = new Matrix4x4[currentGlobal.Length];
        for (var index = 0; index < skinningTransforms.Length; index++)
        {
            skinningTransforms[index] = Matrix4x4.Invert(_skeleton.Bones[index].BindTransform, out var inverseBind)
                ? inverseBind * currentGlobal[index]
                : Matrix4x4.Identity;
        }
        return skinningTransforms;
    }

    private static Matrix4x4 CreatePoseTransform(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) *
        Matrix4x4.CreateFromQuaternion(rotation) *
        Matrix4x4.CreateTranslation(position);

    private static void ResolveGlobalTransform(
        int index,
        IReadOnlyList<Matrix4x4> localTransforms,
        Matrix4x4[] globalTransforms,
        byte[] visitState,
        IReadOnlyList<ModelPreviewSkeletonBone> bones)
    {
        if (visitState[index] == 2)
            return;
        if (visitState[index] == 1)
        {
            globalTransforms[index] = bones[index].BindTransform;
            visitState[index] = 2;
            return;
        }

        visitState[index] = 1;
        var parentIndex = bones[index].ParentIndex;
        if (parentIndex >= 0 && parentIndex < globalTransforms.Length)
        {
            ResolveGlobalTransform(parentIndex, localTransforms, globalTransforms, visitState, bones);
            globalTransforms[index] = localTransforms[index] * globalTransforms[parentIndex];
        }
        else
        {
            globalTransforms[index] = localTransforms[index];
        }
        visitState[index] = 2;
    }

    private static Matrix4x4[] BuildRestLocalTransforms(IReadOnlyList<ModelPreviewSkeletonBone> bones)
    {
        var transforms = new Matrix4x4[bones.Count];
        for (var index = 0; index < bones.Count; index++)
        {
            var parentIndex = bones[index].ParentIndex;
            transforms[index] = parentIndex >= 0 && parentIndex < bones.Count &&
                                Matrix4x4.Invert(bones[parentIndex].BindTransform, out var inverseParent)
                ? bones[index].BindTransform * inverseParent
                : bones[index].BindTransform;
        }
        return transforms;
    }

    private sealed class BoneTrack
    {
        private readonly List<(float Time, Vector3 Value)> _positions = [];
        private readonly List<(float Time, Quaternion Value)> _rotations = [];
        private readonly List<(float Time, Vector3 Value)> _scales = [];

        public void Add(ModelPreviewAnimationKeyframe keyframe)
        {
            switch (keyframe.Channel)
            {
                case ModelPreviewAnimationChannel.Position:
                    _positions.Add((keyframe.TimeSeconds, keyframe.Position));
                    break;
                case ModelPreviewAnimationChannel.Rotation:
                    _rotations.Add((keyframe.TimeSeconds, keyframe.Rotation));
                    break;
                case ModelPreviewAnimationChannel.Scale:
                    _scales.Add((keyframe.TimeSeconds, keyframe.Scale));
                    break;
            }
        }

        public Vector3 SamplePosition(Vector3 initial, float time) => SampleVector(_positions, initial, time);
        public Vector3 SampleScale(Vector3 initial, float time) => SampleVector(_scales, initial, time);

        public Quaternion SampleRotation(Quaternion initial, float time)
        {
            if (_rotations.Count == 0)
                return initial;
            var upper = FindUpperBound(_rotations, time);
            if (upper == 0)
                return Interpolate(initial, _rotations[0].Value, time, _rotations[0].Time, Quaternion.Slerp);
            if (upper >= _rotations.Count)
                return _rotations[^1].Value;
            var lower = _rotations[upper - 1];
            var next = _rotations[upper];
            return Interpolate(lower.Value, next.Value, time - lower.Time, next.Time - lower.Time, Quaternion.Slerp);
        }

        private static Vector3 SampleVector(List<(float Time, Vector3 Value)> values, Vector3 initial, float time)
        {
            if (values.Count == 0)
                return initial;
            var upper = FindUpperBound(values, time);
            if (upper == 0)
                return Interpolate(initial, values[0].Value, time, values[0].Time, Vector3.Lerp);
            if (upper >= values.Count)
                return values[^1].Value;
            var lower = values[upper - 1];
            var next = values[upper];
            return Interpolate(lower.Value, next.Value, time - lower.Time, next.Time - lower.Time, Vector3.Lerp);
        }

        private static int FindUpperBound<T>(List<(float Time, T Value)> values, float time)
        {
            var low = 0;
            var high = values.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (values[middle].Time <= time)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private static T Interpolate<T>(T from, T to, float elapsed, float duration, Func<T, T, float, T> lerp) =>
            duration <= 0 ? to : lerp(from, to, Math.Clamp(elapsed / duration, 0, 1));
    }
}

internal static class ModelPreviewCpuSkinner
{
    public static ModelPreviewSkinnedGeometry Skin(
        ModelPreviewMesh mesh,
        IReadOnlyList<Matrix4x4> skinningTransforms,
        bool skinNormals = true)
    {
        if (mesh.Skinning is not { } skinning || !skinning.IsValidForVertexCount(mesh.VertexCount))
            return new ModelPreviewSkinnedGeometry(mesh.Positions, mesh.Normals);

        var result = new float[mesh.Positions.Length];
        var normals = skinNormals &&
                      mesh.Normals is { Length: > 0 } &&
                      mesh.Normals.Length == mesh.Positions.Length
            ? new float[mesh.Normals.Length]
            : null;
        for (var vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
        {
            var positionOffset = vertexIndex * 3;
            var source = new Vector3(
                mesh.Positions[positionOffset],
                mesh.Positions[positionOffset + 1],
                mesh.Positions[positionOffset + 2]);
            var sourceNormal = normals is not null && mesh.Normals is { } sourceNormals
                ? new Vector3(
                    sourceNormals[positionOffset],
                    sourceNormals[positionOffset + 1],
                    sourceNormals[positionOffset + 2])
                : Vector3.Zero;
            var influenceOffset = vertexIndex * ModelPreviewSkinningData.InfluencesPerVertex;
            var transformed = Vector3.Zero;
            var transformedNormal = Vector3.Zero;
            var totalWeight = 0f;
            for (var influence = 0; influence < ModelPreviewSkinningData.InfluencesPerVertex; influence++)
            {
                var transformIndex = skinning.TransformIndices[influenceOffset + influence];
                var weight = skinning.Weights[influenceOffset + influence];
                if (transformIndex < 0 || transformIndex >= skinningTransforms.Count || weight <= 0)
                    continue;
                var transform = skinningTransforms[transformIndex];
                transformed += Vector3.Transform(source, transform) * weight;
                if (normals is not null)
                    transformedNormal += Vector3.TransformNormal(sourceNormal, transform) * weight;
                totalWeight += weight;
            }

            if (totalWeight < 0.999f)
            {
                var remainder = Math.Clamp(1f - totalWeight, 0f, 1f);
                transformed += source * remainder;
                transformedNormal += sourceNormal * remainder;
            }
            result[positionOffset] = transformed.X;
            result[positionOffset + 1] = transformed.Y;
            result[positionOffset + 2] = transformed.Z;
            if (normals is not null)
            {
                transformedNormal = transformedNormal.LengthSquared() > 0.000001f
                    ? Vector3.Normalize(transformedNormal)
                    : sourceNormal;
                normals[positionOffset] = transformedNormal.X;
                normals[positionOffset + 1] = transformedNormal.Y;
                normals[positionOffset + 2] = transformedNormal.Z;
            }
        }
        return new ModelPreviewSkinnedGeometry(result, normals);
    }

    public static float[] SkinPositions(ModelPreviewMesh mesh, IReadOnlyList<Matrix4x4> skinningTransforms)
    {
        return Skin(mesh, skinningTransforms).Positions;
    }
}

internal readonly record struct ModelPreviewSkinnedGeometry(
    float[] Positions,
    float[]? Normals);
