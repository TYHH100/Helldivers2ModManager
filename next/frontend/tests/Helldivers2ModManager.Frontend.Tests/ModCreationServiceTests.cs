using System.IO;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class ModCreationServiceTests
{
    private string? _root;
    private readonly List<string> _sourceRoots = [];

    [TestCleanup]
    public void Cleanup()
    {
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        foreach (var source in _sourceRoots.Where(Directory.Exists))
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAsync_BuildsV1ManifestAndCopiesSourceTree()
    {
        var service = CreateService(out var paths);
        var source = CreateSourceDirectory();
        _sourceRoots.Add(source);
        File.WriteAllText(Path.Combine(source, "data", "patch_0"), "patch");

        var created = await service.CreateAsync(new CreateModRequest(
            new DirectoryInfo(source),
            "Test Mod",
            "A test mod",
            Path.Combine(source, "icon.png"),
            UseV1Manifest: true,
            [
                new("Main", "Main option", ["data"], null,
                    [new("Alt", "Alternate", ["textures"])]),
            ]));

        Assert.AreEqual("Test Mod", created.Name);
        Assert.IsTrue(File.Exists(Path.Combine(created.Directory.FullName, "data", "patch_0")));
        Assert.IsTrue(File.Exists(Path.Combine(created.Directory.FullName, "icon.png")));
        var manifest = ModManifest.DeserializeFromDirectory(created.Directory);
        var v1 = (V1ModManifest)manifest;
        Assert.AreEqual("Main", v1.Options![0].Name);
        Assert.AreEqual("data", v1.Options[0].Include![0]);
        Assert.AreEqual("Alt", v1.Options[0].SubOptions![0].Name);
        Assert.AreEqual(Path.Combine(paths.Data, "Mods", "Test Mod"), created.Directory.FullName);
    }

    [TestMethod]
    public async Task CreateAsync_BuildsLegacyOptionsWithoutSubOptions()
    {
        var service = CreateService(out _);
        var source = CreateSourceDirectory();
        _sourceRoots.Add(source);
        File.WriteAllText(Path.Combine(source, "OptionA", "patch_0"), "patch");

        var created = await service.CreateAsync(new CreateModRequest(
            new DirectoryInfo(source),
            "Legacy Mod",
            string.Empty,
            null,
            UseV1Manifest: false,
            [new("OptionA", "First", ["OptionA"], null, [new("Dropped", string.Empty, [])])]));

        var manifest = (LegacyModManifest)ModManifest.DeserializeFromDirectory(created.Directory);
        CollectionAssert.AreEqual(new[] { "OptionA" }, manifest.Options!.ToArray());
    }

    private ModCreationService CreateService(out ApplicationPaths paths)
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        paths = new ApplicationPaths(_root);
        using var database = new Database(paths.Database);
        var service = new ApplicationSettingsService(paths, new PreferenceRepository(database));
        service.InitializeAsync().GetAwaiter().GetResult();
        return new ModCreationService(service);
    }

    private static string CreateSourceDirectory()
    {
        var source = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendSource", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "data"));
        Directory.CreateDirectory(Path.Combine(source, "OptionA"));
        File.WriteAllText(Path.Combine(source, "icon.png"), "image");
        return source;
    }
}
