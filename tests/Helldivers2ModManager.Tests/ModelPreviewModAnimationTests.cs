using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using System.IO;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 武器模组“添加或修改动作”预览：模组补丁自带的 Bones/StateMachine/Animation 资源
/// 必须能被捕获并解析成可播放的动画库。
/// </summary>
[TestClass]
public sealed class ModelPreviewModAnimationTests
{
    private const ulong BonesTypeId = PatchResourceTypeIds.Bones;
    private const ulong StateMachineTypeId = PatchResourceTypeIds.StateMachine;
    private const ulong AnimationTypeId = PatchResourceTypeIds.Animation;
    private const string PatchMagic = "F0000011";

    private string? _tempRoot;

    [TestInitialize]
    public void Initialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "hd2mm-mod-anim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [TestMethod]
    public void TryBuild_SyntheticAnimationResources_ParsesLibraryWithClips()
    {
        const ulong bonesId = 0x35A61296619CC47E;
        const ulong stateMachineId = 0x35A61296619CC47E;
        const ulong animationId = 0xFC5C286B5DEAEB25;
        var payloads = new Dictionary<(ulong FileId, ulong TypeId), byte[]>
        {
            [(bonesId, BonesTypeId)] = BuildBonesResource(0x11111111, 0x22222222),
            [(stateMachineId, StateMachineTypeId)] = BuildStateMachineResource(
                stateNameHash: 0x1AD6D70FCD7C3209, animationIds: [animationId]),
            [(animationId, AnimationTypeId)] = BuildAnimationResource(
                boneCount: 2, lengthSeconds: 1.5f,
                boneHashes: [0x11111111, 0x22222222],
                keyframes: [(BoneIndex: 1, TimeSeconds: 0.25f)])
        };

        var library = ModelPreviewModAnimationLibraryBuilder.TryBuild(payloads, bonesId, stateMachineId);

        Assert.IsNotNull(library);
        Assert.IsTrue(library.IsFromMod);
        Assert.AreEqual(bonesId, library.BonesId);
        Assert.AreEqual(stateMachineId, library.StateMachineId);
        Assert.AreEqual(2, library.BoneHashes.Count);
        Assert.AreEqual(1, library.Animations.Count);
        var option = library.Animations[0];
        Assert.AreEqual(animationId, option.AnimationId);
        Assert.AreEqual(0x1AD6D70FCD7C3209UL, option.StateNameHash);
        Assert.AreEqual(1.5f, option.Clip.LengthSeconds);
        Assert.AreEqual(2, option.Clip.BoneCount);
        Assert.AreEqual(1, option.Clip.Keyframes.Count);
        Assert.AreEqual(0.25f, option.Clip.Keyframes[0].TimeSeconds);
    }

    [TestMethod]
    public void TryBuild_MissingOrInvalidInputs_ReturnsNull()
    {
        var payloads = new Dictionary<(ulong FileId, ulong TypeId), byte[]>
        {
            [(0x1, BonesTypeId)] = BuildBonesResource(0x11111111, 0x22222222)
        };

        Assert.IsNull(ModelPreviewModAnimationLibraryBuilder.TryBuild(payloads, 0, 0x1),
            "Zero resource ids must not resolve to a library.");
        Assert.IsNull(ModelPreviewModAnimationLibraryBuilder.TryBuild(payloads, 0x2, 0x1),
            "Missing Bones payload must not resolve to a library.");
        Assert.IsNull(ModelPreviewModAnimationLibraryBuilder.TryBuild(payloads, 0x1, 0x1),
            "Missing StateMachine payload must not resolve to a library.");
        Assert.IsNull(ModelPreviewModAnimationLibraryBuilder.TryBuild(
            new Dictionary<(ulong FileId, ulong TypeId), byte[]>(), 0x1, 0x1));
    }

    [TestMethod]
    public void TryAddAnimationResource_EnforcesPerResourceAndTotalBudgets()
    {
        var result = new ModelPreviewResult();
        var small = new byte[1024];

        Assert.IsTrue(result.TryAddAnimationResource(0x1, BonesTypeId, small));
        Assert.IsFalse(result.TryAddAnimationResource(0x2, AnimationTypeId, new byte[32 * 1024 * 1024 + 1]),
            "A payload above the per-resource budget must be rejected.");
        Assert.IsNull(result.Error);

        // 总量预算：64 MiB，单个资源上限 32 MiB。先放 1KB，再用 32 MiB + (32 MiB - 1KB)
        // 恰好填满总量，随后任何新增都必须被拒绝。
        var halfBudget = new byte[32 * 1024 * 1024];
        var fillRemainder = new byte[32 * 1024 * 1024 - 1024];
        Assert.IsTrue(result.TryAddAnimationResource(0x3, AnimationTypeId, halfBudget));
        Assert.IsTrue(result.TryAddAnimationResource(0x4, AnimationTypeId, fillRemainder));
        Assert.IsFalse(result.TryAddAnimationResource(0x5, AnimationTypeId, small),
            "Adding a new key beyond the total budget must be rejected.");
        Assert.IsTrue(result.TryAddAnimationResource(0x3, AnimationTypeId, small),
            "Overriding an existing key with a smaller payload stays within the total budget.");
        Assert.AreSame(small, result.PatchAnimationResources[(0x3, AnimationTypeId)]);

        // 捕获路径从补丁文件重新读出字节数组，所以跨补丁覆盖只保证值相等。
        var largeOverride = new byte[32 * 1024 * 1024 + 512];
        Assert.IsFalse(result.TryAddAnimationResource(0x3, AnimationTypeId, largeOverride),
            "An override that would exceed the total budget must keep the previous payload.");
        Assert.AreSame(small, result.PatchAnimationResources[(0x3, AnimationTypeId)]);
    }

    [TestMethod]
    public async Task PreviewModelAsync_CapturesModAnimationResourcesWithPatchOverrideSemantics()
    {
        const ulong bonesId = 0xAAAA000000000001;
        const ulong animationId = 0xAAAA000000000002;
        var firstPatch = Path.Combine(_tempRoot!, "9ba626afa44a3aa3.patch_0");
        var secondPatch = Path.Combine(_tempRoot!, "9ba626afa44a3aa3.patch_1");
        var bones = BuildBonesResource(0x11111111, 0x22222222);
        WriteAnimationResourcePatch(firstPatch, [
            (bonesId, BonesTypeId, bones),
            (bonesId, StateMachineTypeId, BuildStateMachineResource(0x1AD6D70FCD7C3209, [animationId])),
            (animationId, AnimationTypeId, BuildAnimationResource(2, 1f, [0x11111111, 0x22222222], [(1, 0.5f)]))]);
        var overrideBones = BuildBonesResource(0x11111111, 0x33333333);
        WriteAnimationResourcePatch(secondPatch, [
            (bonesId, BonesTypeId, overrideBones)]);

        var modDirectory = new DirectoryInfo(_tempRoot!);
        var result = await new PatchResourceInspectionService().PreviewModelAsync(
            modDirectory,
            [new FileInfo(firstPatch), new FileInfo(secondPatch)]);

        Assert.IsNull(result.Error, result.Error);
        Assert.AreEqual(0, result.SkippedAnimationResources);
        Assert.AreEqual(3, result.PatchAnimationResources.Count);
        // 捕获是从补丁文件重新读出的字节数组，按值比较而不是引用比较。
        CollectionAssert.AreEqual(overrideBones, result.PatchAnimationResources[(bonesId, BonesTypeId)],
            "The later patch in the chain must be the effective version of an overridden resource.");
        Assert.IsTrue(result.PatchAnimationResources.ContainsKey((animationId, AnimationTypeId)));

        var library = ModelPreviewModAnimationLibraryBuilder.TryBuild(
            result.PatchAnimationResources, bonesId, bonesId);
        Assert.IsNotNull(library);
        Assert.AreEqual(1, library.Animations.Count);
        CollectionAssert.AreEqual(
            new uint[] { 0x11111111, 0x33333333 },
            library.BoneHashes.ToArray());
    }

    [TestMethod]
    public async Task PreviewModelAsync_WeaponModBundlesAddedActionsAsPlayableLibraries()
    {
        var root = FindRepositoryRoot();
        var modDirectory = new DirectoryInfo(Path.Combine(
            root.FullName, "Test", "Mods", "Mods", "manual_download_d5005d83_7851e9fe"));
        var weaponPatch = new FileInfo(Path.Combine(modDirectory.FullName, "LAS-98 LAS-99", "9ba626afa44a3aa3.patch_0"));

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, [weaponPatch]);

        Assert.IsNull(result.Error, result.Error);
        var skinnedMeshes = result.Meshes.Where(static mesh => mesh.Skinning is not null).ToArray();
        Assert.IsTrue(skinnedMeshes.Length > 0, "The weapon Unit must decode skinned sections for animation playback.");
        Assert.IsTrue(result.PatchAnimationResources.Count >= 4,
            $"The weapon mod bundles Bones/StateMachine/Animation resources, found {result.PatchAnimationResources.Count}.");

        var totalAnimations = 0;
        foreach (var skeleton in skinnedMeshes
                     .Select(static mesh => mesh.Skinning!.Skeleton)
                     .Distinct())
        {
            var library = ModelPreviewModAnimationLibraryBuilder.TryBuild(
                result.PatchAnimationResources, skeleton.BonesId, skeleton.StateMachineId);
            Assert.IsNotNull(library, $"Skeleton 0x{skeleton.BonesId:X16} must resolve a mod-bundled library.");
            Assert.IsTrue(library.IsFromMod);
            Assert.IsTrue(ModelPreviewAnimationCompatibility.IsCompatible(skeleton, library),
                "The mod-bundled library must be compatible with the weapon skeleton by resource id.");
            Assert.IsTrue(library.Animations.All(static animation => animation.Clip.LengthSeconds > 0));
            var binding = new ModelPreviewAnimationBinding(
                skeleton, library.BoneHashes, library.Animations[0].Clip, library.BonesId);
            Assert.AreEqual(skeleton.Bones.Count, binding.SampleSkinningTransforms(0.1f).Length);
            totalAnimations += library.Animations.Count;
        }

        Assert.IsTrue(totalAnimations >= 4,
            $"The fixture weapon mod should expose at least four bundled actions, found {totalAnimations}.");
    }

    /// <summary>
    /// Bones 资源：u32 骨骼数 + u32 LOD 数 + LOD 浮点表 + u32 名称哈希表。
    /// </summary>
    private static byte[] BuildBonesResource(params uint[] boneHashes)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)boneHashes.Length);
        stream.Write(buffer.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
        stream.Write(buffer.AsSpan(0, 4));
        foreach (var hash in boneHashes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, hash);
            stream.Write(buffer.AsSpan(0, 4));
        }
        return stream.ToArray();
    }

    /// <summary>
    /// StateMachine 资源：76 字节头（+4 声明层数，+8 层数据偏移）→ 层表 → 层 → 状态 → 动画 ID 表。
    /// </summary>
    private static byte[] BuildStateMachineResource(ulong stateNameHash, IReadOnlyList<ulong> animationIds)
    {
        const int layerDataOffset = 76;
        const int layerOffset = layerDataOffset + 8;
        const int stateTableOffset = layerOffset + 12;
        const int stateOffset = stateTableOffset + 4;
        const int animationTableOffset = stateOffset + 112;
        var size = animationTableOffset + animationIds.Count * sizeof(ulong);

        var data = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1); // declared layer count
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), layerDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(layerDataOffset), 1); // layer count
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(layerDataOffset + 4), 8); // relative layer offset
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(layerOffset + 8), 1); // state count
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(stateTableOffset), 16); // relative state offset（相对 layerOffset）
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(stateOffset), stateNameHash);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(stateOffset + 12), animationIds.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(stateOffset + 16), 112); // relative animation table offset
        for (var index = 0; index < animationIds.Count; index++)
            BinaryPrimitives.WriteUInt64LittleEndian(
                data.AsSpan(animationTableOffset + index * sizeof(ulong)), animationIds[index]);
        return data;
    }

    /// <summary>
    /// Animation 资源：TRS 初始姿势（未压缩）+ 可选的关键帧 + 终止符 3。
    /// </summary>
    private static byte[] BuildAnimationResource(
        int boneCount,
        float lengthSeconds,
        IReadOnlyList<uint> boneHashes,
        IReadOnlyList<(int BoneIndex, float TimeSeconds)> keyframes)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
        stream.Write(buffer.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(buffer, boneCount);
        stream.Write(buffer.AsSpan(0, 4));
        stream.Write(BitConverter.GetBytes(lengthSeconds));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
        stream.Write(buffer.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(buffer, boneHashes.Count);
        stream.Write(buffer.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 0);
        stream.Write(buffer.AsSpan(0, 4));
        foreach (var hash in boneHashes)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, hash);
            stream.Write(buffer);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, 0); // animation flags
        stream.Write(buffer.AsSpan(0, 2));

        var flagByteCount = (3 * boneCount + 7) / 8;
        if ((flagByteCount & 1) != 0)
            flagByteCount++;
        stream.Write(new byte[flagByteCount]); // all poses uncompressed

        // 初始 TRS 姿势：零四元数无法归一化、零缩放会判成 additive，必须写单位值。
        foreach (var _ in boneHashes)
        {
            stream.Write(BitConverter.GetBytes(0f));
            stream.Write(BitConverter.GetBytes(0f));
            stream.Write(BitConverter.GetBytes(0f));
            stream.Write(BitConverter.GetBytes(0f));
            stream.Write(BitConverter.GetBytes(0f));
            stream.Write(BitConverter.GetBytes(0f));
            stream.Write(BitConverter.GetBytes(1f)); // w
            stream.Write(BitConverter.GetBytes(1f));
            stream.Write(BitConverter.GetBytes(1f));
            stream.Write(BitConverter.GetBytes(1f));
        }

        foreach (var hash in boneHashes)
            stream.Write(BitConverter.GetBytes(1f)); // hash float table

        // 未压缩条目的 4 字节头与载荷前 4 字节重叠（解析器 peek 后 rewind）：
        // subtype(2) + 骨骼索引(4) + 时间(4) + Vector3(12) = 22 字节。
        // stackalloc 提到循环外复用（CA2014）。
        Span<byte> entry = stackalloc byte[22];
        foreach (var (boneIndex, timeSeconds) in keyframes)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[2..], (uint)boneIndex);
            BitConverter.GetBytes(timeSeconds).CopyTo(entry[6..10]);
            BitConverter.GetBytes(0.25f).CopyTo(entry[10..14]);
            BitConverter.GetBytes(0.5f).CopyTo(entry[14..18]);
            BitConverter.GetBytes(0.75f).CopyTo(entry[18..22]);
            stream.Write(entry);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(buffer, 3); // terminator
        stream.Write(buffer.AsSpan(0, 2));
        return stream.ToArray();
    }

    private void WriteAnimationResourcePatch(
        string path,
        IReadOnlyList<(ulong FileId, ulong TypeId, byte[] Data)> resources)
    {
        using var patch = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, Convert.ToUInt32(PatchMagic, 16));
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 1);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)resources.Count);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
        patch.Write(buffer[..4]);
        patch.Write(new byte[56]);

        // 单个类型头（32 字节），与检视服务的读取方式保持一致。
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 0);
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, resources[0].TypeId);
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)resources.Count);
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 16);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 64);
        patch.Write(buffer[..4]);

        var dataOffset = patch.Length + 80L * resources.Count + 8;
        foreach (var (fileId, typeId, data) in resources)
        {
            patch.Write(BuildTocEntry(fileId, typeId, dataOffset, (uint)data.Length));
            dataOffset += data.Length;
        }
        patch.Write(new byte[8]);
        foreach (var (_, _, data) in resources)
            patch.Write(data);

        File.WriteAllBytes(path, patch.ToArray());
    }

    private static byte[] BuildTocEntry(ulong fileId, ulong typeId, long dataOffset, uint dataSize)
    {
        var entry = new byte[80];
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(0), fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(16), (ulong)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(56), dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(68), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(72), 64);
        return entry;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the model-preview fixtures.");
    }
}
