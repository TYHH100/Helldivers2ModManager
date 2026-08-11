using System.Text.Json;
using Helldivers2ModManager.Extensions;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Models;

internal sealed class LegacyModManifest : IModManifest
{
    public ManifestVersion Version => ManifestVersion.Legacy;
    
    public required Guid Guid { get; init; }
    
    public required string Name { get; init; }

    public required string Description { get; init; }
    
    public string? IconPath { get; init; }
    
    public IReadOnlyList<string>? Options { get; init; }

    public static IModManifest Deserialize(JsonElement root, ILogger? logger = null)
    {
        var guid = Guid.Parse(root.GetProperty<string>(nameof(Guid)));
        var name = root.GetProperty<string>(nameof(Name));
        // 部分作者漏写 Description 字段，直接报错会导致整个模组导入失败；
        // 缺失或类型不对时用空字符串兜底，仅记录警告。
        string description;
        if (root.TryGetProperty(nameof(Description), JsonValueKind.String, out var descProp))
        {
            description = descProp.GetString() ?? string.Empty;
        }
        else
        {
            logger?.LogWarning($"Manifest \"{nameof(Description)}\" is missing or not a string, using an empty string as fallback.");
            description = string.Empty;
        }
        string? iconPath = null;
        if (root.TryGetProperty(nameof(IconPath), JsonValueKind.String, out var prop))
            iconPath = prop.GetString()!;
        List<string>? options = null;
        if (root.TryGetProperty(nameof(Options), JsonValueKind.Array, out prop))
        {
            options = new(prop.GetArrayLength());
            foreach (var elm in prop.EnumerateArray())
                if (elm.ValueKind == JsonValueKind.String)
                    options.Add(elm.GetString()!);
                else
                    logger?.LogWarning("Unexpected none `string` value found in legacy manifest options");
        }

        return new LegacyModManifest
        {
            Guid = guid,
            Name = name,
            Description = description,
            IconPath = iconPath,
            Options = options,
        };
    }

    public void Serialize(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(Guid), Guid.ToString());
        writer.WriteString(nameof(Name), Name);
        writer.WriteString(nameof(Description), Description);
        if (IconPath is not null)
            writer.WriteString(nameof(IconPath), IconPath);
        if (Options is not null)
        {
            writer.WriteStartArray(nameof(Options));
            foreach (var opt in Options)
                writer.WriteStringValue(opt);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}