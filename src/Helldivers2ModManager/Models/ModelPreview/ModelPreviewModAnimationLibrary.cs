namespace Helldivers2ModManager.Models;

using System.IO;

/// <summary>
/// Patch resource type identifiers shared by patch inspection and model preview. The same
/// (file id, type id) addressing is used by game archives and mod patches.
/// Source: https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/blob/main/utils/constants.py
/// </summary>
internal static class PatchResourceTypeIds
{
    public const ulong Level = 0x2A690FD348FE9AC5;
    public const ulong Physics = 0x5F7203C8F280DAB8;
    public const ulong Bones = 0x18DEAD01056B72E9;
    public const ulong Animation = 0x931E336D7646CC26;
    public const ulong StateMachine = 0xA486D4045106165C;
    public const ulong Particles = 0xA8193123526FAD64;
    public const ulong Prefab = 0xAB2F78E885F513C6;
    public const ulong Texture = 0xCD4238C6A0C69E32;
    public const ulong Unit = 0xE0A48D0BE9A7453F;
    public const ulong Material = 0xEAC0B497876ADEDF;
    public const ulong WwiseBank = 0x535A7BD3E650D799;
    public const ulong WwiseDep = 0xAF32095C82F2B070;
    public const ulong WwiseStream = 0x504B55235D21440E;
    public const ulong ShadingEnvironmentMapping = 0x250E0A11AC8E26F8;
    public const ulong Entity = 0x9831CA893B0D087D;
    public const ulong Bik = 0xAA5965F03029FA18;
    public const ulong Font = 0x9EFE0A916AAE7880;
    public const ulong Cloth = 0xD7014A50477953E0;
    public const ulong ShadingEnvironment = 0xFE73C7DCFF8A7CA5;
    public const ulong Lua = 0xA14E8DFA2CD117E2;
    public const ulong VectorField = 0xF7505933166D6755;
    public const ulong Flow = 0x92D3EE038EEB610D;
    public const ulong RenderConfig = 0x27862FE24795319C;
    public const ulong Strings = 0x0D972BAB10B40FD3;
    public const ulong NetworkConfig = 0x3B1FA9E8F6BAC374;
    public const ulong Config = 0x82645835E6B73232;
    public const ulong Package = 0xAD9C6D9ED1E5E77A;
    public const ulong WwiseMetadata = 0xD50A8B7E1C82B110;
    public const ulong MouseCursor = 0xB277B11FE4A61D37;
    public const ulong Renderable = 0x7910103158FC1DE9;
    public const ulong ShaderLibraryGroup = 0x9E5C3CC74575AEB5;
    public const ulong ShaderLibrary = 0xE5EE32A477239A93;
    public const ulong RuntimeFont = 0x05106B81DCD58A13;
    public const ulong RagdollProfile = 0x1D59BD6687DB6B33;
    public const ulong AhBin = 0x2A0A70ACFE476E1D;
    public const ulong IkSkeleton = 0x57A13425279979D7;
    public const ulong WwiseProperties = 0x5FDD5FE391076F9F;
    public const ulong HavokAiProperties = 0x6592B918E67F082C;
    public const ulong TextureAtlas = 0x9199BB50B6896F02;
    public const ulong Geleta = 0xB8FD4D2CEDE20ED7;
    public const ulong GeometryGroup = 0xC4F0F4BE7FB0C8D6;
    public const ulong HashLookup = 0xE3F2851035957AF5;
    public const ulong SpeedTree = 0xE985C5F61C169997;
    public const ulong HavokPhysicsProperties = 0xF7A09F8BB35A1D49;
    public const ulong CameraShake = 0xFCAAF813B4D3CC1E;
    public const ulong Bik2 = 0x5EE65304478F8DB5;
    public const ulong Unknown = 0x46BC82AAE9AE0565;

    // Compatibility aliases from constants.py's named IDs.
    public const ulong CompositeUnit = GeometryGroup;
    public const ulong Tex = Texture;
    public const ulong MaterialId = Material;
    public const ulong Bone = Bones;
    public const ulong WwiseMetaData = WwiseMetadata;
    public const ulong Particle = Particles;
    public const ulong Script = Lua;
    public const ulong PathEntry = WwiseDep;
    public const ulong String = Strings;
    public const ulong Xaml = Unknown;

    public static string GetDisplayName(ulong typeId) => typeId switch
    {
        Level => "Level",
        Physics => "Physics",
        Bones => "Bones",
        Animation => "Animation",
        StateMachine => "StateMachine",
        Particles => "Particles",
        Prefab => "Prefab",
        Texture => "Texture",
        Unit => "Unit",
        Material => "Material",
        WwiseBank => "WwiseBank",
        WwiseDep => "WwiseDep / PathEntry",
        WwiseStream => "WwiseStream",
        ShadingEnvironmentMapping => "ShadingEnvironmentMapping",
        Entity => "Entity",
        Bik => "Bik",
        Font => "Font",
        Cloth => "Cloth",
        ShadingEnvironment => "ShadingEnvironment",
        Lua => "Script / Lua",
        VectorField => "VectorField",
        Flow => "Flow",
        RenderConfig => "RenderConfig",
        Strings => "Strings",
        NetworkConfig => "NetworkConfig",
        Config => "Config",
        Package => "Package",
        WwiseMetadata => "WwiseMetadata",
        MouseCursor => "MouseCursor",
        Renderable => "Renderable",
        ShaderLibraryGroup => "ShaderLibraryGroup",
        ShaderLibrary => "ShaderLibrary",
        RuntimeFont => "RuntimeFont",
        RagdollProfile => "RagdollProfile",
        AhBin => "AhBin",
        IkSkeleton => "IkSkeleton",
        WwiseProperties => "WwiseProperties",
        HavokAiProperties => "HavokAiProperties",
        TextureAtlas => "TextureAtlas",
        Geleta => "Geleta",
        GeometryGroup => "GeometryGroup / CompositeUnit",
        HashLookup => "HashLookup",
        SpeedTree => "SpeedTree",
        HavokPhysicsProperties => "HavokPhysicsProperties",
        CameraShake => "CameraShake",
        Bik2 => "Bik2",
        Unknown => "Unknown / Xaml",
        _ => "Unknown"
    };
}

/// <summary>
/// Builds an animation library from resources bundled inside mod patches instead of the
/// game archive. Weapon mods ship their own Bones, StateMachine and Animation resources
/// for added or modified actions; the same parsers used for game resources apply.
/// </summary>
internal static class ModelPreviewModAnimationLibraryBuilder
{
    public const int MaxAnimationsPerLibrary = 256;

    public static ModelPreviewAnimationLibrary? TryBuild(
        IReadOnlyDictionary<(ulong FileId, ulong TypeId), byte[]> payloads,
        ulong bonesId,
        ulong stateMachineId)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (bonesId == 0 || stateMachineId == 0)
            return null;
        if (!payloads.TryGetValue((bonesId, PatchResourceTypeIds.Bones), out var bonesData) ||
            !payloads.TryGetValue((stateMachineId, PatchResourceTypeIds.StateMachine), out var stateMachineData))
        {
            return null;
        }

        try
        {
            var boneHashes = ModelPreviewAnimationLibraryParser.ParseBoneHashes(bonesData);
            var references = ModelPreviewAnimationLibraryParser.ParseStateMachineAnimations(stateMachineData);
            var animations = new List<ModelPreviewAnimationOption>(Math.Min(references.Count, MaxAnimationsPerLibrary));
            foreach (var reference in references.Take(MaxAnimationsPerLibrary))
            {
                if (!payloads.TryGetValue((reference.AnimationId, PatchResourceTypeIds.Animation), out var animationData))
                    continue;
                if (!ModelPreviewAnimationParser.TryParse(
                        animationData,
                        reference.AnimationId,
                        out var clip,
                        out _) ||
                    clip is null || clip.BoneCount > boneHashes.Count)
                {
                    continue;
                }

                animations.Add(new ModelPreviewAnimationOption
                {
                    AnimationId = reference.AnimationId,
                    StateNameHash = reference.StateNameHash,
                    LayerIndex = reference.LayerIndex,
                    Clip = clip
                });
            }

            return animations.Count == 0
                ? null
                : new ModelPreviewAnimationLibrary
                {
                    BonesId = bonesId,
                    StateMachineId = stateMachineId,
                    BoneHashes = boneHashes,
                    Animations = animations,
                    IsFromMod = true
                };
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
