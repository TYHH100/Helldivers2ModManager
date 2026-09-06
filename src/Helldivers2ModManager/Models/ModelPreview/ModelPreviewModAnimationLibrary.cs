namespace Helldivers2ModManager.Models;

using System.IO;

/// <summary>
/// Patch resource type identifiers for the skeletal animation family. Units reference
/// Bones and StateMachine resources by file id; StateMachine states reference Animation
/// resources by file id. The same (file id, type id) addressing is used by game archives
/// and by mod patches, so mod bundles can carry added or modified weapon actions.
/// </summary>
internal static class PatchResourceTypeIds
{
    public const ulong Bones = 0x18DEAD01056B72E9;
    public const ulong StateMachine = 0xA486D4045106165C;
    public const ulong Animation = 0x931E336D7646CC26;
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
