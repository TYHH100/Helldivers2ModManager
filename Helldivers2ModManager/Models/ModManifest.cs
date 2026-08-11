using System.IO;
using System.Runtime.Serialization;
using System.Text.Json;
using Helldivers2ModManager.Exceptions;
using Helldivers2ModManager.Extensions;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Models;

internal static class ModManifest
{
    private static readonly JsonDocumentOptions s_options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };
    
    public static IModManifest DeserializeFromDirectory(DirectoryInfo dir, ILogger? logger = null)
    {
        foreach (var file in dir.EnumerateFiles())
            if (file.Name == "manifest.json")
                return DeserializeFromFile(file, logger);
        throw new FileNotFoundException($"Could not find file `manifest.json` in `{dir.FullName}`!");
    }
    
    public static IModManifest DeserializeFromFile(FileInfo file, ILogger? logger = null)
    {
        using var stream = file.OpenRead();
        var doc = JsonDocument.Parse(stream, s_options);
        return DeserializeFromDocument(doc, logger);
    }

    public static IModManifest DeserializeFromDocument(JsonDocument doc, ILogger? logger = null)
    {
        var root = doc.RootElement;
        var version = ManifestVersion.Legacy;

        if (root.TryGetProperty(nameof(IModManifest.Version), out var prop))
        {
            // 部分作者误把模组自身的版本号（如 2、5、1.0 等）写入清单的 Version 字段，
            // 导致导入失败。只要 Version 存在且不是 1，就自动按 V1 宽容处理。
            var isOne = prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value) && value == 1;
            if (!isOne)
                logger?.LogWarning($"Manifest \"{nameof(IModManifest.Version)}\" is not 1, automatically treating the manifest as V1.");
            version = ManifestVersion.V1;
        }

        return version switch
        {
            ManifestVersion.Legacy => LegacyModManifest.Deserialize(root, logger),
            ManifestVersion.V1 => V1ModManifest.Deserialize(root),
            ManifestVersion.V2 => throw new EndOfLifeException(),
            _ => throw new UnknownManifestVersionException()
        };
    }

    public static IModManifest InferFromDirectory(DirectoryInfo dir, ILogger? logger = null)
    {
        var dirs = dir.GetDirectories();

        string? iconPath = null;
        var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
        var priorityIconNames = new[] { "icon", "Icon", "ICON", "logo", "Logo", "LOGO", "cover", "Cover", "COVER", "thumbnail", "Thumbnail", "THUMBNAIL", "preview", "Preview", "PREVIEW", "banner", "Banner", "BANNER" };

        var imageFiles = dir.GetFiles()
            .Where(file => imageExtensions.Contains(file.Extension.ToLowerInvariant()))
            .ToList();

        if (imageFiles.Count > 0)
        {
            iconPath = SelectBestIcon(imageFiles, priorityIconNames, logger);
        }

        if (dirs.Length == 0)
            return new LegacyModManifest
            {
                Guid = Guid.NewGuid(),
                Name = dir.Name,
                Description = "A locally imported mod.",
                IconPath = iconPath,
            };

        return new LegacyModManifest
		{
			Guid = Guid.NewGuid(),
			Name = dir.Name,
			Description = "A locally imported mod.",
            Options = dirs.Select(static d => d.Name).ToArray(),
			IconPath = iconPath,
		};
	}

    private static string? SelectBestIcon(List<FileInfo> imageFiles, string[] priorityNames, ILogger? logger)
    {
        var scoredImages = new List<(FileInfo File, int Score)>();

        foreach (var file in imageFiles)
        {
            int score = 0;
            string fileNameLower = file.Name.ToLowerInvariant();
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileNameLower);

            for (int i = 0; i < priorityNames.Length; i++)
            {
                string priorityNameLower = priorityNames[i].ToLowerInvariant();
                if (fileNameWithoutExt.Contains(priorityNameLower))
                {
                    score += (priorityNames.Length - i) * 10;
                    if (fileNameWithoutExt == priorityNameLower)
                    {
                        score += 5;
                    }
                }
            }

            if (fileNameLower.EndsWith(".png"))
            {
                score += 3;
            }

            long fileSize = file.Length;
            if (fileSize < 100 * 1024)
            {
                score += 5;
            }
            else if (fileSize < 500 * 1024)
            {
                score += 2;
            }

            scoredImages.Add((file, score));
        }

        scoredImages.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (scoredImages.Count > 1 && scoredImages[0].Score == scoredImages[1].Score)
        {
            logger?.LogInformation("Multiple images with same highest score found, selecting first one");
        }

        return scoredImages.FirstOrDefault().File?.Name;
    }

    public static void SaveToFile(IModManifest manifest, DirectoryInfo dir)
    {
        var file = new FileInfo(Path.Combine(dir.FullName, "manifest.json"));
        using var stream = file.Open(FileMode.Create, FileAccess.Write);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        manifest.Serialize(writer);
    }
}