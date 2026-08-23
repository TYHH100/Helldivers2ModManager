using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using System.Text;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModTypeDetectionServiceTests
{
    private const ulong Unit = 0xE0A48D0BE9A7453FUL;
    private const ulong Texture = 0xCD4238C6A0C69E32UL;
    private const ulong Material = 0xEAC0B497876ADEDFUL;
    private const ulong Bones = 0x18DEAD01056B72E9UL;
    private const ulong Animation = 0x931E336D7646CC26UL;
    private const ulong StateMachine = 0xA486D4045106165CUL;
    private const ulong Audio = 0x535A7BD3E650D799UL;
    private const ulong Script = 0xA14E8DFA2CD117E2UL;
    private const ulong UnknownType = 0xDEADBEEFCAFEBABEUL;

    private string _tempRoot = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "modtype_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static ModTypeDetectionService CreateService() =>
        new(NullLogger<ModTypeDetectionService>.Instance);

    private string WriteMod(string subDir, params (ulong TypeId, byte[] Payload)[] entries)
    {
        var dir = Path.Combine(_tempRoot, subDir);
        Directory.CreateDirectory(dir);
        var patch = BuildPatch(entries, pathString: null);
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), patch);
        return dir;
    }

    private string WriteModWithPath(string subDir, string pathString, params (ulong TypeId, byte[] Payload)[] entries)
    {
        var dir = Path.Combine(_tempRoot, subDir);
        Directory.CreateDirectory(dir);
        var patch = BuildPatch(entries, pathString);
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), patch);
        return dir;
    }

    private static byte[] BuildPatch(
        IReadOnlyList<(ulong TypeId, byte[] Payload)> entries,
        string? pathString)
    {
        var typeIds = entries.Select(static e => e.TypeId).Distinct().ToArray();
        var numFiles = entries.Count;
        var fileEntriesOffset = 72 + typeIds.Length * 32;
        var dataStart = fileEntriesOffset + numFiles * 80;
        var pathBytes = pathString is null ? 0 : Encoding.ASCII.GetByteCount(pathString) + 8;
        var total = dataStart + entries.Sum(static e => e.Payload.Length) + pathBytes;
        var b = new byte[total];

        BinaryPrimitives.WriteInt32LittleEndian(b, unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), typeIds.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), numFiles);

        for (var i = 0; i < typeIds.Length; i++)
        {
            var o = 72 + i * 32;
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 8), typeIds[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 16), (ulong)entries.Count(e => e.TypeId == typeIds[i]));
        }

        var dataOffset = dataStart;
        for (var i = 0; i < entries.Count; i++)
        {
            var o = fileEntriesOffset + i * 80;
            var (typeId, payload) = entries[i];
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), (ulong)(0x1000 + i));
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 8), typeId);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 16), (ulong)dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 56), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 76), (uint)(i + 1));
            payload.CopyTo(b, dataOffset);
            dataOffset += payload.Length;
        }

        if (pathString is not null)
        {
            var text = Encoding.ASCII.GetBytes(pathString);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(dataOffset), (uint)text.Length);
            text.CopyTo(b, dataOffset + 4);
        }

        return b;
    }

    private static byte[] AudioPayload()
    {
        var payload = new byte[64];
        Encoding.ASCII.GetBytes("BKHD").CopyTo(payload, 12);
        return payload;
    }

    private static byte[] DdsPayload()
    {
        var payload = new byte[196];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(payload, 192);
        return payload;
    }

    private static byte[] LuaPayload()
    {
        var payload = new byte[64];
        Encoding.ASCII.GetBytes("local BC = ").CopyTo(payload, 0);
        return payload;
    }

    private static byte[] UnitPayload() => new byte[128];

    [TestMethod]
    public void Detect_AudioTypeId_ReturnsAudio()
    {
        var dir = WriteMod("audio", (Audio, AudioPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Audio, result.Type);
        Assert.AreEqual(1, result.PatchesScanned);
        Assert.AreEqual(1, result.TypeIdCounts[Audio]);
        CollectionAssert.AreEqual(new[] { ModType.Audio }, result.Types.ToArray());
    }

    [TestMethod]
    public void Detect_BkHdSignatureOnly_ReturnsAudio()
    {
        var dir = WriteMod("audio_bkhd", (UnknownType, AudioPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Audio, result.Type);
    }

    [TestMethod]
    public void Detect_TextureOnly_ReturnsUi()
    {
        var dir = WriteMod("ui", (Texture, DdsPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Ui, result.Type);
    }

    [TestMethod]
    public void Detect_TextureAndMaterial_ReturnsTexture()
    {
        var dir = WriteMod("texture",
            (Texture, DdsPayload()),
            (Texture, DdsPayload()),
            (Material, UnitPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Texture, result.Type);
    }

    [TestMethod]
    public void Detect_UnitsWithTexturesAndMaterial_ReturnsArmor()
    {
        var dir = WriteMod("armor",
            (Unit, UnitPayload()),
            (Unit, UnitPayload()),
            (Texture, DdsPayload()),
            (Material, UnitPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Armor, result.Type);
    }

    [TestMethod]
    public void Detect_UnitBonesStateMachine_ReturnsStratagem()
    {
        var dir = WriteMod("stratagem",
            (Unit, UnitPayload()),
            (Bones, UnitPayload()),
            (StateMachine, UnitPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Stratagem, result.Type);
    }

    [TestMethod]
    public void Detect_UnitBonesStateMachineAnimation_ReturnsSupportWeapon()
    {
        var dir = WriteMod("weapon",
            (Unit, UnitPayload()),
            (Bones, UnitPayload()),
            (StateMachine, UnitPayload()),
            (Animation, UnitPayload()),
            (Animation, UnitPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.SupportWeapon, result.Type);
    }

    [TestMethod]
    public void Detect_EnemyPathOverridesStructure_ReturnsEnemy()
    {
        var dir = WriteModWithPath("enemy", "content/audio/vo_bugs",
            (Unit, UnitPayload()), (Bones, UnitPayload()), (StateMachine, UnitPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Enemy, result.Type);
        Assert.IsTrue(result.PathHints.Any(p => p.Contains("vo_bugs", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Detect_EmptyDirectory_ReturnsUnknown()
    {
        var dir = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(dir);

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Unknown, result.Type);
        Assert.AreEqual(0, result.PatchesScanned);
    }

    [TestMethod]
    public void Detect_CompanionFilesAreIgnored()
    {
        var dir = Path.Combine(_tempRoot, "companions");
        Directory.CreateDirectory(dir);
        var patch = BuildPatch([(Texture, DdsPayload())], pathString: null);
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), patch);
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0.gpu_resources"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0.stream"), new byte[64]);

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Ui, result.Type);
        Assert.AreEqual(1, result.PatchesScanned);
    }

    [TestMethod]
    public void Detect_ScriptTypeId_ReturnsScript()
    {
        var dir = WriteMod("script", (Script, LuaPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Script, result.Type);
    }

    [TestMethod]
    public void Detect_LuaSignatureOnly_ReturnsScript()
    {
        var dir = WriteMod("script_lua", (UnknownType, LuaPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Script, result.Type);
    }

    [TestMethod]
    public void Detect_PrimaryWeaponPath_ReturnsPrimaryWeapon()
    {
        var dir = WriteModWithPath("primary", "content/fac_helldivers/equipment/primary_weapons/arc_shotgun/arc_shotgun",
            (Unit, UnitPayload()), (Bones, UnitPayload()), (StateMachine, UnitPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.PrimaryWeapon, result.Type);
    }

    [TestMethod]
    public void Detect_SupportWeaponPath_ReturnsSupportWeapon()
    {
        var dir = WriteModWithPath("support", "content/fac_helldivers/equipment/support_weapons/arc_thrower/arc_thrower",
            (Unit, UnitPayload()), (Bones, UnitPayload()), (StateMachine, UnitPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.SupportWeapon, result.Type);
    }

    [TestMethod]
    public void Detect_BackpackPath_ReturnsStratagem()
    {
        var dir = WriteModWithPath("backpack", "content/fac_helldivers/equipment/backpacks/ammo_backpack/ammo_backpack",
            (Unit, UnitPayload()), (Bones, UnitPayload()), (StateMachine, UnitPayload()));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Stratagem, result.Type);
    }

    [TestMethod]
    public void Detect_MixedMod_MultiplePatches_YieldsMultipleTags()
    {
        var dir = Path.Combine(_tempRoot, "mixed");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), BuildPatch(
            [(Unit, UnitPayload()), (Bones, UnitPayload()), (StateMachine, UnitPayload())], pathString: null));
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_1"), BuildPatch(
            [(Texture, DdsPayload()), (Material, UnitPayload())], pathString: null));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Stratagem, result.Type);
        CollectionAssert.AreEquivalent(new[] { ModType.Stratagem, ModType.Texture }, result.Types.ToArray());
    }

    [TestMethod]
    public void Detect_MixedMod_UiDroppedWhenModelPresent()
    {
        var dir = Path.Combine(_tempRoot, "mixed_ui");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), BuildPatch(
            [(Texture, DdsPayload())], pathString: null));
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_1"), BuildPatch(
            [(Unit, UnitPayload()), (Bones, UnitPayload()), (StateMachine, UnitPayload())], pathString: null));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        CollectionAssert.AreEquivalent(new[] { ModType.Stratagem }, result.Types.ToArray());
        CollectionAssert.DoesNotContain(result.Types.ToArray(), ModType.Ui);
    }

    [TestMethod]
    public void Detect_MixedMod_ModelDroppedWhenMoreSpecific()
    {
        var dir = Path.Combine(_tempRoot, "mixed_model");
        Directory.CreateDirectory(dir);
        var armorPatch = new List<(ulong, byte[])>();
        for (var i = 0; i < 21; i++)
            armorPatch.Add((Unit, UnitPayload()));
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), BuildPatch(armorPatch, pathString: null));
        File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_1"), BuildPatch(
            [(Texture, DdsPayload()), (Texture, DdsPayload()), (Material, UnitPayload())], pathString: null));

        var result = CreateService().Detect(new DirectoryInfo(dir));

        Assert.AreEqual(ModType.Armor, result.Type);
        CollectionAssert.AreEquivalent(new[] { ModType.Armor, ModType.Texture }, result.Types.ToArray());
        CollectionAssert.DoesNotContain(result.Types.ToArray(), ModType.Model);
    }

    private static ModTypeDetectionService.BuiltInTagDefinition Def(ModType type, string key) =>
        new(type, Guid.Parse("D1C3A7B0-0000-4000-8000-00000000000" + ((int)type).ToString("X1")), key, "#000000");

    private static Dictionary<ModType, ModTypeDetectionService.BuiltInTagDefinition> Defs(params ModType[] types) =>
        types.ToDictionary(t => t, t => Def(t, "ModType.Tag." + t));

    [TestMethod]
    public void MergeAutoTags_KeepsUserTagsAndAddsTypeTag()
    {
        var armorId = Guid.Parse("D1C3A7B0-0000-4000-8000-000000000004");
        var builtIn = new HashSet<Guid> { armorId };
        var userTag = Guid.NewGuid();

        var merged = ModTypeDetectionService.MergeAutoTags([userTag], [armorId], builtIn);

        Assert.IsNotNull(merged);
        CollectionAssert.AreEquivalent(new[] { userTag, armorId }, merged.ToArray());
    }

    [TestMethod]
    public void MergeAutoTags_TypeChangeReplacesStaleBuiltInTag()
    {
        var armorId = Guid.Parse("D1C3A7B0-0000-4000-8000-000000000004");
        var enemyId = Guid.Parse("D1C3A7B0-0000-4000-8000-000000000007");
        var builtIn = new HashSet<Guid> { armorId, enemyId };
        var userTag = Guid.NewGuid();

        var merged = ModTypeDetectionService.MergeAutoTags([userTag, armorId], [enemyId], builtIn);

        Assert.IsNotNull(merged);
        CollectionAssert.AreEquivalent(new[] { userTag, enemyId }, merged.ToArray());
        Assert.IsFalse(merged.Contains(armorId));
    }

    [TestMethod]
    public void MergeAutoTags_MultipleTypes_MergesAllAndDropsStale()
    {
        var armorId = Guid.Parse("D1C3A7B0-0000-4000-8000-000000000004");
        var stratagemId = Guid.Parse("D1C3A7B0-0000-4000-8000-000000000005");
        var textureId = Guid.Parse("D1C3A7B0-0000-4000-8000-000000000003");
        var builtIn = new HashSet<Guid> { armorId, stratagemId, textureId };
        var userTag = Guid.NewGuid();

        var merged = ModTypeDetectionService.MergeAutoTags(
            [userTag, armorId], [stratagemId, textureId], builtIn);

        Assert.IsNotNull(merged);
        CollectionAssert.AreEquivalent(new[] { userTag, stratagemId, textureId }, merged.ToArray());
        Assert.IsFalse(merged.Contains(armorId));
    }

    [TestMethod]
    public void MergeAutoTags_AlreadyTagged_ReturnsNull()
    {
        var armorId = Guid.Parse("D1C3A7B0-0000-4000-8000-000000000004");
        var builtIn = new HashSet<Guid> { armorId };
        var userTag = Guid.NewGuid();

        var merged = ModTypeDetectionService.MergeAutoTags([userTag, armorId], [armorId], builtIn);

        Assert.IsNull(merged);
    }

    [TestMethod]
    public void MergeAutoTags_NoDetectedIds_ReturnsNull()
    {
        var merged = ModTypeDetectionService.MergeAutoTags([], [], new HashSet<Guid>());

        Assert.IsNull(merged);
    }

    [TestMethod]
    public void ResolveAutoTagIds_ReusesExistingUserTagByChineseName()
    {
        var userTag = new ModTag("音效");
        var tags = new List<ModTag> { userTag };
        var defs = Defs(ModType.Audio);

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, NoMappings, _ => "音效", createMissingTags: false, out var created);

        CollectionAssert.AreEqual(new[] { userTag.Id }, ids.ToArray());
        Assert.IsFalse(created);
        Assert.AreEqual(1, tags.Count, "existing tag must not be duplicated");
    }

    [TestMethod]
    public void ResolveAutoTagIds_ReusesEnglishAliasInChineseLocale()
    {
        var userTag = new ModTag("Audio");
        var tags = new List<ModTag> { userTag };
        var defs = Defs(ModType.Audio);

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, NoMappings, _ => "音效", createMissingTags: false, out var created);

        CollectionAssert.AreEqual(new[] { userTag.Id }, ids.ToArray());
        Assert.IsFalse(created);
    }

    [TestMethod]
    public void ResolveAutoTagIds_MatchIsCaseInsensitive()
    {
        var userTag = new ModTag("audio");
        var tags = new List<ModTag> { userTag };
        var defs = Defs(ModType.Audio);

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, NoMappings, _ => "音效", createMissingTags: false, out _);

        CollectionAssert.AreEqual(new[] { userTag.Id }, ids.ToArray());
    }

    [TestMethod]
    public void ResolveAutoTagIds_ReusesFixedBuiltInIdRegardlessOfName()
    {
        var def = Def(ModType.Audio, "ModType.Tag.Audio");
        var userTag = new ModTag(def.Id, "自定义音频", def.Color);
        var tags = new List<ModTag> { userTag };
        var defs = new Dictionary<ModType, ModTypeDetectionService.BuiltInTagDefinition> { [ModType.Audio] = def };

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, NoMappings, _ => "音效", createMissingTags: false, out _);

        CollectionAssert.AreEqual(new[] { def.Id }, ids.ToArray());
        Assert.AreEqual(1, tags.Count);
    }

    [TestMethod]
    public void ResolveAutoTagIds_NoMatchWithoutCreate_ReturnsEmpty()
    {
        var tags = new List<ModTag>();
        var defs = Defs(ModType.Audio);

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, NoMappings, _ => "音效", createMissingTags: false, out var created);

        Assert.AreEqual(0, ids.Count);
        Assert.IsFalse(created);
        Assert.AreEqual(0, tags.Count);
    }

    [TestMethod]
    public void ResolveAutoTagIds_CreatesTagWhenAllowed()
    {
        var tags = new List<ModTag>();
        var def = Def(ModType.Audio, "ModType.Tag.Audio");
        var defs = new Dictionary<ModType, ModTypeDetectionService.BuiltInTagDefinition> { [ModType.Audio] = def };

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, NoMappings, _ => "音效", createMissingTags: true, out var created);

        CollectionAssert.AreEqual(new[] { def.Id }, ids.ToArray());
        Assert.IsTrue(created);
        Assert.AreEqual(1, tags.Count);
        Assert.AreEqual("音效", tags[0].Name);
    }

    [TestMethod]
    public void ResolveAutoTagIds_MultipleTypes_ReusesAndCreatesMix()
    {
        var audioTag = new ModTag("音效");
        var tags = new List<ModTag> { audioTag };
        var defs = Defs(ModType.Audio, ModType.Enemy);

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio, ModType.Enemy], defs, NoMappings, t => "类型" + (int)t,
            createMissingTags: true, out var created);

        Assert.AreEqual(2, ids.Count);
        Assert.AreEqual(audioTag.Id, ids[0]);
        Assert.IsTrue(created);
        Assert.AreEqual(2, tags.Count);
        Assert.AreEqual(defs[ModType.Enemy].Id, ids[1]);
    }

    [TestMethod]
    public void ResolveAutoTagIds_ManualMappingWinsOverNameMatch()
    {
        var audioTag = new ModTag("音效");
        var mappedTag = new ModTag("我的音频专用");
        var tags = new List<ModTag> { audioTag, mappedTag };
        var defs = Defs(ModType.Audio);
        var mappings = new Dictionary<ModType, Guid> { [ModType.Audio] = mappedTag.Id };

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, mappings, _ => "音效", createMissingTags: false, out _);

        CollectionAssert.AreEqual(new[] { mappedTag.Id }, ids.ToArray());
    }

    [TestMethod]
    public void ResolveAutoTagIds_ManualMappingToMissingTag_FallsBackToName()
    {
        var audioTag = new ModTag("音效");
        var tags = new List<ModTag> { audioTag };
        var defs = Defs(ModType.Audio);
        var mappings = new Dictionary<ModType, Guid> { [ModType.Audio] = Guid.NewGuid() };

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, mappings, _ => "音效", createMissingTags: false, out _);

        CollectionAssert.AreEqual(new[] { audioTag.Id }, ids.ToArray());
    }

    [TestMethod]
    public void ResolveAutoTagIds_ManualMappingPreventsAutoCreate()
    {
        var tags = new List<ModTag>();
        var mappedTag = new ModTag("音频专用");
        tags.Add(mappedTag);
        var def = Def(ModType.Audio, "ModType.Tag.Audio");
        var defs = new Dictionary<ModType, ModTypeDetectionService.BuiltInTagDefinition> { [ModType.Audio] = def };
        var mappings = new Dictionary<ModType, Guid> { [ModType.Audio] = mappedTag.Id };

        var ids = ModTypeDetectionService.ResolveAutoTagIds(
            tags, [ModType.Audio], defs, mappings, _ => "音效", createMissingTags: true, out var created);

        CollectionAssert.AreEqual(new[] { mappedTag.Id }, ids.ToArray());
        Assert.IsFalse(created);
        Assert.AreEqual(1, tags.Count, "no new tag may be created when a manual mapping exists");
    }

    private static readonly IReadOnlyDictionary<ModType, Guid> NoMappings =
        new Dictionary<ModType, Guid>();
}
