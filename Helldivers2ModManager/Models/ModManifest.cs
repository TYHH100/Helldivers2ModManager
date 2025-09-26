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
        
        if (root.TryGetProperty(nameof(IModManifest.Version), JsonValueKind.Number, out var prop))
        {
            if (prop.TryGetInt32(out var value))
                version = value switch
                {
                    1 => ManifestVersion.V1,
                    2 => ManifestVersion.V2,
                    _ => throw new UnknownManifestVersionException()
                };
            else
                throw new SerializationException($"Could not convert value of property \"{nameof(IModManifest.Version)}\" to `{typeof(int).Name}`!");
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

        if (dirs.Length == 0)
            return new LegacyModManifest
            {
                Guid = Guid.NewGuid(),
                Name = dir.Name,
                Description = "A locally imported mod.",
            };

        return new LegacyModManifest
		{
			Guid = Guid.NewGuid(),
			Name = dir.Name,
			Description = "A locally imported mod.",
            Options = dirs.Select(static d => d.Name).ToArray(),
		};
	}
}