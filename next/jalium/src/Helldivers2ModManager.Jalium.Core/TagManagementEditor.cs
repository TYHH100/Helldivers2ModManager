using System.Collections.ObjectModel;
using System.Globalization;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services.Infrastructure;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed class TagManagementEditor(SettingsService settings) : IDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public const int MaxNameLength = 16;
    public ObservableCollection<ModTag> Tags => settings.Tags;

    public async Task<ModTag> CreateAsync(string name)
    {
        var normalized = NormalizeName(name);
        await _saveGate.WaitAsync();
        try
        {
            GuardWritable();
            var tag = new ModTag(normalized);
            Tags.Add(tag);
            try { await settings.SaveAsync(); }
            catch
            {
                Tags.Remove(tag);
                throw;
            }
            return tag;
        }
        finally { _saveGate.Release(); }
    }

    public async Task RenameAsync(ModTag tag, string name)
    {
        var normalized = NormalizeName(name);
        await _saveGate.WaitAsync();
        try
        {
            GuardWritable();
            GuardMember(tag);
            var previous = tag.Name;
            tag.Name = normalized;
            try { await settings.SaveAsync(); }
            catch
            {
                tag.Name = previous;
                throw;
            }
        }
        finally { _saveGate.Release(); }
    }

    public async Task SetColorAsync(ModTag tag, string color)
    {
        if (color.Length != 9 || color[0] != '#'
            || !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("Tag color must be #AARRGGBB.", nameof(color));
        await _saveGate.WaitAsync();
        try
        {
            GuardWritable();
            GuardMember(tag);
            var previous = tag.Color;
            tag.Color = color;
            try { await settings.SaveAsync(); }
            catch
            {
                tag.Color = previous;
                throw;
            }
        }
        finally { _saveGate.Release(); }
    }

    public async Task DeleteAsync(ModTag tag)
    {
        await _saveGate.WaitAsync();
        try
        {
            GuardWritable();
            GuardMember(tag);
            var index = Tags.IndexOf(tag);
            Tags.RemoveAt(index);
            try { await settings.SaveAsync(); }
            catch
            {
                Tags.Insert(index, tag);
                throw;
            }
        }
        finally { _saveGate.Release(); }
    }

    private void GuardWritable()
    {
        if (!settings.Initialized || settings.IsReadonly)
            throw new InvalidOperationException("Tag settings are not writable.");
    }

    private void GuardMember(ModTag tag)
    {
        if (!Tags.Contains(tag))
            throw new ArgumentException("Tag is not in settings.", nameof(tag));
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > MaxNameLength)
            throw new ArgumentException("Tag name must contain 1 to 16 characters.", nameof(name));
        return normalized;
    }

    public void Dispose() => _saveGate.Dispose();
}
