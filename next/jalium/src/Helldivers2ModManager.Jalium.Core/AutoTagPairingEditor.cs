using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed class AutoTagPairingRow(ModType type, string nameKey, Guid? selectedTagId)
{
    public ModType Type { get; } = type;
    public string NameKey { get; } = nameKey;
    public Guid? SelectedTagId { get; internal set; } = selectedTagId;
}

internal sealed class AutoTagPairingEditor : IDisposable
{
    private readonly SettingsService _settings;
    private readonly TagManagementEditor _tags;
    private readonly AutoTagPairingRow[] _rows;

    public IReadOnlyList<AutoTagPairingRow> Rows => _rows;
    public IReadOnlyList<ModTag> Tags => _settings.Tags;

    public AutoTagPairingEditor(SettingsService settings, LocalizationService localization)
    {
        _settings = settings;
        _tags = new TagManagementEditor(settings);
        _rows = ModTypeDetectionService.BuiltInTagDefinitions.Select(def =>
        {
            var mapped = settings.AutoTagMappings.FirstOrDefault(mapping => mapping.Type == def.Type)?.TagId;
            if (mapped is not null && settings.Tags.All(tag => tag.Id != mapped.Value))
                mapped = null;
            mapped ??= settings.Tags.FirstOrDefault(tag => string.Equals(tag.Name?.Trim(),
                localization[def.NameKey], StringComparison.OrdinalIgnoreCase))?.Id;
            return new AutoTagPairingRow(def.Type, def.NameKey, mapped);
        }).ToArray();
    }

    public void Select(AutoTagPairingRow row, Guid? tagId)
    {
        if (!_rows.Contains(row))
            throw new ArgumentException("Unknown auto-tag row.", nameof(row));
        if (tagId is not null && _settings.Tags.All(tag => tag.Id != tagId.Value))
            throw new ArgumentException("Unknown tag ID.", nameof(tagId));
        row.SelectedTagId = tagId;
    }

    public async Task<ModTag> CreateTagAsync(AutoTagPairingRow row, string name)
    {
        if (!_rows.Contains(row))
            throw new ArgumentException("Unknown auto-tag row.", nameof(row));
        var tag = await _tags.CreateAsync(name);
        row.SelectedTagId = tag.Id;
        return tag;
    }

    public async Task SaveAsync()
    {
        if (_settings.IsReadonly)
            throw new InvalidOperationException("Auto-tag mappings are not writable.");
        var previous = _settings.AutoTagMappings;
        _settings.AutoTagMappings = _rows.Where(row => row.SelectedTagId is not null)
            .Select(row => new AutoTagMapping { Type = row.Type, TagId = row.SelectedTagId!.Value })
            .ToList();
        try { await _settings.SaveAsync(); }
        catch
        {
            _settings.AutoTagMappings = previous;
            throw;
        }
    }

    public void Dispose() => _tags.Dispose();
}
