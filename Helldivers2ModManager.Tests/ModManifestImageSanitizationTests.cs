using System.Text.Json;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModManifestImageSanitizationTests
{
    private const string ValidGuid = "4c0dc24b-c7a0-46e1-b0c2-18e3dbb8d4e1";

    [TestCleanup]
    public void Cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-manifest-sanitize-tests");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    [TestMethod]
    public void V1_InvalidImagePaths_AreCleared_ValidOnesKept()
    {
        var dir = CreateModDirectory("v1-invalid-images");
        File.WriteAllText(Path.Combine(dir.FullName, "ok.png"), "x");

        using var doc = JsonDocument.Parse($$"""
            {
              "Version": 1,
              "Guid": "{{ValidGuid}}",
              "Name": "Test",
              "IconPath": "missing-icon.png",
              "Options": [
                {
                  "Name": "A",
                  "Image": "ok.png",
                  "Include": ["A"]
                },
                {
                  "Name": "B",
                  "Image": "missing-b.png",
                  "Include": ["B"],
                  "SubOptions": [
                    { "Name": "B1", "Image": "missing-b1.png", "Include": ["B/B1"] },
                    { "Name": "B2", "Include": ["B/B2"] }
                  ]
                }
              ]
            }
            """);
        var manifest = (V1ModManifest)ModManifest.DeserializeFromDocument(doc);

        var sanitized = (V1ModManifest)ModService.SanitizeManifestImagePaths(manifest, dir);

        Assert.IsNull(sanitized.IconPath);
        Assert.AreEqual("ok.png", sanitized.Options![0].Image);
        Assert.IsNull(sanitized.Options![1].Image);
        Assert.IsNull(sanitized.Options![1].SubOptions![0].Image);
        Assert.IsNull(sanitized.Options![1].SubOptions![1].Image);
        Assert.AreEqual(ValidGuid, sanitized.Guid.ToString());
    }

    [TestMethod]
    public void V1_AllImagePathsValid_ReturnsSameInstance()
    {
        var dir = CreateModDirectory("v1-valid-images");
        File.WriteAllText(Path.Combine(dir.FullName, "ok.png"), "x");

        using var doc = JsonDocument.Parse($$"""
            {
              "Version": 1,
              "Guid": "{{ValidGuid}}",
              "Name": "Test",
              "IconPath": "ok.png",
              "Options": [
                { "Name": "A", "Image": "ok.png", "Include": ["A"] }
              ]
            }
            """);
        var manifest = (V1ModManifest)ModManifest.DeserializeFromDocument(doc);

        var sanitized = (V1ModManifest)ModService.SanitizeManifestImagePaths(manifest, dir);

        Assert.AreSame(manifest, sanitized);
    }

    [TestMethod]
    public void Legacy_InvalidIconPath_IsCleared()
    {
        var dir = CreateModDirectory("legacy-invalid-icon");

        using var doc = JsonDocument.Parse($$"""
            {
              "Guid": "{{ValidGuid}}",
              "Name": "Test",
              "IconPath": "missing-icon.png",
              "Options": ["A"]
            }
            """);
        var manifest = (LegacyModManifest)ModManifest.DeserializeFromDocument(doc);

        var sanitized = (LegacyModManifest)ModService.SanitizeManifestImagePaths(manifest, dir);

        Assert.IsNull(sanitized.IconPath);
        CollectionAssert.AreEqual(new[] { "A" }, (System.Collections.ICollection)sanitized.Options!);
    }

    [TestMethod]
    public void V1_EmptyImagePath_IsCleared()
    {
        var dir = CreateModDirectory("v1-empty-image");

        using var doc = JsonDocument.Parse($$"""
            {
              "Version": 1,
              "Guid": "{{ValidGuid}}",
              "Name": "Test",
              "IconPath": "",
              "Options": [
                { "Name": "A", "Image": "", "Include": ["A"] }
              ]
            }
            """);
        var manifest = (V1ModManifest)ModManifest.DeserializeFromDocument(doc);

        var sanitized = (V1ModManifest)ModService.SanitizeManifestImagePaths(manifest, dir);

        Assert.IsNull(sanitized.IconPath);
        Assert.IsNull(sanitized.Options![0].Image);
    }

    private static DirectoryInfo CreateModDirectory(string name)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hd2mm-manifest-sanitize-tests", Guid.NewGuid().ToString("N"), name));
        dir.Create();
        return dir;
    }
}
