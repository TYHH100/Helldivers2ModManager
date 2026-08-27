using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Mods;

public sealed class ManifestParseException(string message) : Exception(message);

public static class ModManifest
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    private static readonly string[] PriorityIconNames =
    [
        "icon",
        "logo",
        "cover",
        "thumbnail",
        "preview",
        "banner",
    ];

    public static IModManifest InferFromDirectory(DirectoryInfo directory, ILogger? logger = null)
    {
        var imageFiles = directory
            .EnumerateFiles()
            .Where(file => ImageExtensions.Contains(file.Extension.ToLowerInvariant()))
            .ToList();
        var iconPath = imageFiles.Count > 0 ? SelectBestIcon(imageFiles, logger) : null;
        var optionNames = directory
            .EnumerateDirectories()
            .Select(static subdirectory => subdirectory.Name)
            .ToArray();

        return new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = directory.Name,
            Description = "A locally imported mod.",
            IconPath = iconPath,
            Options = optionNames,
        };
    }

    private static string? SelectBestIcon(IReadOnlyList<FileInfo> imageFiles, ILogger? logger)
    {
        var bestScore = -1;
        FileInfo? bestFile = null;
        foreach (var file in imageFiles)
        {
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();
            var score = 0;
            for (var index = 0; index < PriorityIconNames.Length; index++)
            {
                if (!fileNameWithoutExtension.Contains(PriorityIconNames[index], StringComparison.Ordinal))
                {
                    continue;
                }

                score += (PriorityIconNames.Length - index) * 10;
                if (fileNameWithoutExtension == PriorityIconNames[index])
                {
                    score += 5;
                }
            }

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestFile = file;
        }

        if (bestFile is null && imageFiles.Count > 0)
        {
            bestFile = imageFiles.OrderBy(static file => file.Name, StringComparer.OrdinalIgnoreCase).First();
            logger?.LogInformation("No prioritized icon found, using \"{FileName}\".", bestFile.Name);
        }

        return bestFile?.Name;
    }

    public static IModManifest DeserializeFromDirectory(DirectoryInfo directory, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var file = directory.GetFiles("manifest.json", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? throw new FileNotFoundException($"Could not find file `manifest.json` in `{directory.FullName}`!");
        return DeserializeFromFile(file, logger);
    }

    public static IModManifest DeserializeFromFile(FileInfo file, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        using var stream = file.OpenRead();
        using var document = JsonDocument.Parse(stream, ParseOptions);
        return DeserializeFromDocument(document, logger);
    }

    public static IModManifest DeserializeFromJson(string json, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        using var document = JsonDocument.Parse(json, ParseOptions);
        return DeserializeFromDocument(document, logger);
    }

    public static IModManifest DeserializeFromDocument(JsonDocument document, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        DeserializeFromElement(document.RootElement, logger);

    public static IModManifest DeserializeFromElement(JsonElement root, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        if (root.ValueKind is not JsonValueKind.Object)
            throw new ManifestParseException("The manifest root must be a JSON object.");

        var version = root.TryGetProperty(nameof(IModManifest.Version), out var versionProperty)
            ? ManifestVersion.V1
            : ManifestVersion.Legacy;
        var guid = ParseGuid(root, logger);

        if (version == ManifestVersion.Legacy)
        {
            return new LegacyModManifest
            {
                Guid = guid,
                Name = RequireString(root, nameof(LegacyModManifest.Name)),
                Description = GetDescription(root, logger),
                IconPath = GetStringOrNull(root, nameof(LegacyModManifest.IconPath)),
                Options = GetStringArrayOrNull(root, nameof(LegacyModManifest.Options), logger),
            };
        }

        List<ModOption>? options = null;
        if (root.TryGetProperty(nameof(V1ModManifest.Options), out var optionsProperty))
        {
            if (optionsProperty.ValueKind != JsonValueKind.Array)
                throw new ManifestParseException($"Property \"{nameof(V1ModManifest.Options)}\" was not of expected type array.");

            options = [];
            foreach (var optionElement in optionsProperty.EnumerateArray())
            {
                if (optionElement.ValueKind != JsonValueKind.Object)
                {
                    logger?.LogWarning("Unexpected none `object` value found in v1 manifest options");
                    continue;
                }

                options.Add(new(
                    Name: RequireString(optionElement, nameof(ModOption.Name)),
                    Description: optionElement.TryGetProperty(nameof(ModOption.Description), JsonValueKind.String, out var descriptionProperty)
                        ? descriptionProperty.GetString() ?? string.Empty
                        : string.Empty,
                    Include: GetStringArrayOrNull(optionElement, nameof(ModOption.Include), logger),
                    Image: GetStringOrNull(optionElement, nameof(ModOption.Image)),
                    SubOptions: GetSubOptions(optionElement, logger)));
            }
        }

        NexusManifestData? nexusData = null;
        if (root.TryGetProperty(nameof(V1ModManifest.NexusData), JsonValueKind.Object, out var nexusProperty))
        {
            nexusData = new(
                RequireInt32(nexusProperty, nameof(NexusManifestData.ModId)),
                RequireString(nexusProperty, nameof(NexusManifestData.Version)));
        }

        return new V1ModManifest
        {
            Guid = guid,
            Name = RequireString(root, nameof(V1ModManifest.Name)),
            Description = GetDescription(root, logger),
            IconPath = GetStringOrNull(root, nameof(V1ModManifest.IconPath)),
            Options = options,
            NexusData = nexusData,
        };
    }

    public static Guid ParseGuid(JsonElement root, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        if (root.TryGetProperty(nameof(IModManifest.Guid), JsonValueKind.String, out var property)
            && Guid.TryParse(property.GetString(), out var guid))
        {
            return guid;
        }

        logger?.LogWarning("Manifest \"Guid\" is missing or invalid, generating a new GUID.");
        return Guid.NewGuid();
    }

    public static string Serialize(IModManifest manifest) => manifest switch
        {
            LegacyModManifest legacy => JsonSerializer.Serialize(legacy, typeof(LegacyModManifest), ManifestJsonContext.Default),
            V1ModManifest v1 => JsonSerializer.Serialize(v1, typeof(V1ModManifest), ManifestJsonContext.Default),
            _ => throw new NotSupportedException($"Unsupported manifest version: {manifest.Version}"),
        };

    public static void SaveToFile(IModManifest manifest, DirectoryInfo directory)
    {
        var path = Path.Combine(directory.FullName, "manifest.json");
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        switch (manifest)
        {
            case LegacyModManifest legacy:
                JsonSerializer.Serialize(writer, legacy, ManifestJsonContext.Default.LegacyModManifest);
                break;
            case V1ModManifest v1:
                JsonSerializer.Serialize(writer, v1, ManifestJsonContext.Default.V1ModManifest);
                break;
            default:
                throw new NotSupportedException($"Unsupported manifest version: {manifest.Version}");
        }
    }

    private static string GetDescription(JsonElement root, Microsoft.Extensions.Logging.ILogger? logger)
    {
        if (root.TryGetProperty(nameof(IModManifest.Description), JsonValueKind.String, out var property))
            return property.GetString() ?? string.Empty;

        logger?.LogWarning("Manifest \"Description\" is missing or not a string, using an empty string as fallback.");
        return string.Empty;
    }

    private static string RequireString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, JsonValueKind.String, out var property)
            ? property.GetString() ?? throw new ManifestParseException($"Property \"{propertyName}\" was null.")
            : throw new ManifestParseException($"Could not find or convert property \"{propertyName}\" to string.");

    private static int RequireInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
            throw new ManifestParseException($"Could not find or convert property \"{propertyName}\" to Int32.");
        return value;
    }

    private static string? GetStringOrNull(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, JsonValueKind.String, out var property)
            ? property.GetString()
            : null;

    private static IReadOnlyList<string>? GetStringArrayOrNull(JsonElement element, string propertyName, Microsoft.Extensions.Logging.ILogger? logger)
    {
        if (!element.TryGetProperty(propertyName, JsonValueKind.Array, out var array)) return null;
        var values = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString() ?? string.Empty);
            else
                logger?.LogWarning($"Unexpected none `string` value found in manifest property {propertyName}");
        }

        return values;
    }

    private static IReadOnlyList<ModSubOption> GetSubOptions(JsonElement option, Microsoft.Extensions.Logging.ILogger? logger)
    {
        if (!option.TryGetProperty(nameof(ModOption.SubOptions), JsonValueKind.Array, out var array)) return [];
        var values = new List<ModSubOption>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                logger?.LogWarning("Unexpected none `object` value found in mod options sub-options");
                continue;
            }

            var include = RequireStringArray(item, nameof(ModSubOption.Include), logger);
            values.Add(new(
                Name: RequireString(item, nameof(ModSubOption.Name)),
                Description: item.TryGetProperty(nameof(ModSubOption.Description), JsonValueKind.String, out var descriptionProperty)
                    ? descriptionProperty.GetString() ?? string.Empty
                    : string.Empty,
                Include: include,
                Image: GetStringOrNull(item, nameof(ModSubOption.Image))));
        }

        return values;
    }

    private static IReadOnlyList<string> RequireStringArray(JsonElement element, string propertyName, Microsoft.Extensions.Logging.ILogger? logger)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            throw new ManifestParseException($"Property \"{propertyName}\" was not of expected type array.");

        var values = new List<string>(property.GetArrayLength());
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString() ?? string.Empty);
            else
                logger?.LogWarning($"Unexpected none `string` value found in manifest property {propertyName}");
        }

        return values;
    }

    private static bool TryGetProperty(this JsonElement element, string name, JsonValueKind kind, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value) && value.ValueKind == kind) return true;
        value = default;
        return false;
    }
}
