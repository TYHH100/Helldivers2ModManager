using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class PatchResourceTypeIdTests
{
    [TestMethod]
    public void GetDisplayName_UsesKnownNamesAndUnknownFallback()
    {
        var known = new Dictionary<ulong, string>
        {
            [PatchResourceTypeIds.Level] = "Level",
            [PatchResourceTypeIds.Physics] = "Physics",
            [PatchResourceTypeIds.Bones] = "Bones",
            [PatchResourceTypeIds.Animation] = "Animation",
            [PatchResourceTypeIds.StateMachine] = "StateMachine",
            [PatchResourceTypeIds.Particles] = "Particles",
            [PatchResourceTypeIds.Prefab] = "Prefab",
            [PatchResourceTypeIds.Texture] = "Texture",
            [PatchResourceTypeIds.Unit] = "Unit",
            [PatchResourceTypeIds.Material] = "Material",
            [PatchResourceTypeIds.WwiseBank] = "WwiseBank",
            [PatchResourceTypeIds.WwiseDep] = "WwiseDep / PathEntry",
            [PatchResourceTypeIds.WwiseStream] = "WwiseStream",
            [PatchResourceTypeIds.ShadingEnvironmentMapping] = "ShadingEnvironmentMapping",
            [PatchResourceTypeIds.Entity] = "Entity",
            [PatchResourceTypeIds.Bik] = "Bik",
            [PatchResourceTypeIds.Font] = "Font",
            [PatchResourceTypeIds.Cloth] = "Cloth",
            [PatchResourceTypeIds.ShadingEnvironment] = "ShadingEnvironment",
            [PatchResourceTypeIds.Lua] = "Script / Lua",
            [PatchResourceTypeIds.VectorField] = "VectorField",
            [PatchResourceTypeIds.Flow] = "Flow",
            [PatchResourceTypeIds.RenderConfig] = "RenderConfig",
            [PatchResourceTypeIds.Strings] = "Strings",
            [PatchResourceTypeIds.NetworkConfig] = "NetworkConfig",
            [PatchResourceTypeIds.Config] = "Config",
            [PatchResourceTypeIds.Package] = "Package",
            [PatchResourceTypeIds.WwiseMetadata] = "WwiseMetadata",
            [PatchResourceTypeIds.MouseCursor] = "MouseCursor",
            [PatchResourceTypeIds.Renderable] = "Renderable",
            [PatchResourceTypeIds.ShaderLibraryGroup] = "ShaderLibraryGroup",
            [PatchResourceTypeIds.ShaderLibrary] = "ShaderLibrary",
            [PatchResourceTypeIds.RuntimeFont] = "RuntimeFont",
            [PatchResourceTypeIds.RagdollProfile] = "RagdollProfile",
            [PatchResourceTypeIds.AhBin] = "AhBin",
            [PatchResourceTypeIds.IkSkeleton] = "IkSkeleton",
            [PatchResourceTypeIds.WwiseProperties] = "WwiseProperties",
            [PatchResourceTypeIds.HavokAiProperties] = "HavokAiProperties",
            [PatchResourceTypeIds.TextureAtlas] = "TextureAtlas",
            [PatchResourceTypeIds.Geleta] = "Geleta",
            [PatchResourceTypeIds.GeometryGroup] = "GeometryGroup / CompositeUnit",
            [PatchResourceTypeIds.HashLookup] = "HashLookup",
            [PatchResourceTypeIds.SpeedTree] = "SpeedTree",
            [PatchResourceTypeIds.HavokPhysicsProperties] = "HavokPhysicsProperties",
            [PatchResourceTypeIds.CameraShake] = "CameraShake",
            [PatchResourceTypeIds.Bik2] = "Bik2",
            [PatchResourceTypeIds.Unknown] = "Unknown / Xaml"
        };

        foreach (var pair in known)
            Assert.AreEqual(pair.Value, PatchResourceTypeIds.GetDisplayName(pair.Key));

        Assert.AreEqual("Unknown", PatchResourceTypeIds.GetDisplayName(0x1234567890ABCDEF));
    }
}
