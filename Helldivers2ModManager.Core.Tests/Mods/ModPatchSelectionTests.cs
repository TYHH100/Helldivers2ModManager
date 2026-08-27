using Helldivers2ModManager.Core.Mods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Mods;

[TestClass]
public sealed class ModPatchSelectionTests
{
    [TestMethod]
    public void LegacyManifest_ShouldSelectOnlyChosenOption()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-selection-legacy-");
        try
        {
            var optionA = root.CreateSubdirectory("A");
            var optionB = root.CreateSubdirectory("B");
            File.WriteAllBytes(Path.Combine(optionA.FullName, "0123456789abcdef.patch_0"), []);
            File.WriteAllBytes(Path.Combine(optionB.FullName, "0123456789abcdef.patch_0"), []);
            var manifest = new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Legacy", Description = "", Options = ["A", "B"] };

            var selected = ModPatchSelection.GetSelectedPatchFiles(root, manifest, [true], [1]);

            Assert.AreEqual(1, selected.Count);
            StringAssert.Contains(selected[0].FullName, "B");
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public void V1Manifest_ShouldExpandEnabledOptionsAndSelectedSubOptions()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-selection-v1-");
        try
        {
            var disabled = root.CreateSubdirectory("disabled");
            var common = root.CreateSubdirectory("common");
            var slim = root.CreateSubdirectory("slim");
            var stocky = root.CreateSubdirectory("stocky");
            foreach (var directory in new[] { disabled, common, slim, stocky })
                File.WriteAllBytes(Path.Combine(directory.FullName, "0123456789abcdef.patch_0"), []);
            var manifest = new V1ModManifest
            {
                Guid = Guid.NewGuid(),
                Name = "V1",
                Description = "",
                Options =
                [
                    new("Disabled", "", ["disabled"], null, null),
                    new("Body", "", ["common"], null, new List<ModSubOption>
                    {
                        new("Slim", "", ["slim"], null),
                        new("Stocky", "", ["stocky"], null),
                    }),
                ],
            };

            var selected = ModPatchSelection.GetSelectedPatchFiles(root, manifest, [false, true], [0, 1]);

            Assert.AreEqual(2, selected.Count);
            Assert.IsTrue(selected.Any(file => file.DirectoryName!.EndsWith("common", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(selected.Any(file => file.DirectoryName!.EndsWith("stocky", StringComparison.OrdinalIgnoreCase)));
        }
        finally { root.Delete(true); }
    }
}
