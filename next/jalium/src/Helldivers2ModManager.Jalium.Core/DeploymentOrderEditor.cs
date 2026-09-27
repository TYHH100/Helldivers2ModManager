using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed class DeploymentOrderEditor : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsService _settings;
    private readonly LocalizationService _localization;
    private readonly IReadOnlyList<ModData> _mods;
    private readonly Func<IReadOnlyList<Guid>?> _getDashboardOrder;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _disposed;

    public ObservableCollection<DeploymentOrderItem> Items { get; } = [];
    public string Title => _localization["DashboardPage.DeploymentOrder"];
    public string OrderDescription => _localization[_settings.DeployBottomToTop
        ? "DeploymentOrderPage.OrderDescBottomUp"
        : "DeploymentOrderPage.OrderDescTopDown"];
    public bool CanMoveToTop => Items.Any(item => item.IsSelected && item.ItemType == DeploymentItemType.Mod && Items.IndexOf(item) > 0);
    public bool CanMoveToBottom => Items.Any(item => item.IsSelected && item.ItemType == DeploymentItemType.Mod && Items.IndexOf(item) < Items.Count - 1);

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeploymentOrderEditor(
        SettingsService settings,
        LocalizationService localization,
        IReadOnlyList<ModData> mods,
        Func<IReadOnlyList<Guid>?> getDashboardOrder)
    {
        if (!settings.Initialized || !settings.UseDeploymentOrder || settings.IsReadonly)
            throw new InvalidOperationException("Deployment order editing requires initialized, writable settings with custom order enabled.");
        _settings = settings;
        _localization = localization;
        _mods = mods;
        _getDashboardOrder = getDashboardOrder;

        Reload();
        Items.CollectionChanged += OnItemsChanged;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public void Reload()
    {
        Items.CollectionChanged -= OnItemsChanged;
        foreach (var item in Items)
            item.PropertyChanged -= OnItemChanged;
        Items.Clear();

        var remaining = _mods.ToDictionary(mod => mod.Manifest.Guid);
        foreach (var guid in _settings.DeploymentOrderGuids)
        {
            var name = remaining.TryGetValue(guid, out var mod)
                ? mod.Manifest.Name
                : _localization["DeploymentOrderPage.DeletedModPlaceholder"];
            AddItem(new DeploymentOrderItem(guid, name));
            remaining.Remove(guid);
        }
        foreach (var mod in _mods)
            if (remaining.Remove(mod.Manifest.Guid))
                AddItem(new DeploymentOrderItem(mod.Manifest.Guid, mod.Manifest.Name));

        Items.CollectionChanged += OnItemsChanged;
        NotifyMoveAvailability();
    }

    public Task MoveToTopAsync() => ChangeOrderAsync(() =>
    {
        var selected = Items.Where(item => item.IsSelected && item.ItemType == DeploymentItemType.Mod)
            .OrderBy(Items.IndexOf).ToList();
        foreach (var item in selected)
        {
            var index = Items.IndexOf(item);
            if (index > 0)
                Items.Move(index, 0);
        }
    });

    public Task MoveToBottomAsync() => ChangeOrderAsync(() =>
    {
        var selected = Items.Where(item => item.IsSelected && item.ItemType == DeploymentItemType.Mod)
            .OrderByDescending(Items.IndexOf).ToList();
        foreach (var item in selected)
        {
            var index = Items.IndexOf(item);
            if (index < Items.Count - 1)
                Items.Move(index, Items.Count - 1);
        }
    });

    public Task SyncFromDashboardAsync() => ChangeOrderAsync(() =>
    {
        var existing = Items.Select(item => item.Guid).ToHashSet();
        var byGuid = _mods.ToDictionary(mod => mod.Manifest.Guid);
        var ordered = new List<ModData>(_mods.Count);
        var dashboardOrder = _getDashboardOrder();
        if (dashboardOrder is { Count: > 0 })
            foreach (var guid in dashboardOrder)
                if (byGuid.Remove(guid, out var mod))
                    ordered.Add(mod);
        foreach (var mod in _mods)
            if (byGuid.Remove(mod.Manifest.Guid))
                ordered.Add(mod);

        foreach (var mod in ordered)
            if (existing.Add(mod.Manifest.Guid))
                AddItem(new DeploymentOrderItem(mod.Manifest.Guid, mod.Manifest.Name));
    });

    public Task ClearOrderAsync() => ChangeOrderAsync(() =>
    {
        foreach (var item in Items)
            item.PropertyChanged -= OnItemChanged;
        Items.Clear();
    });

    public Task MoveByDropAsync(DeploymentOrderItem source, int insertIndex) => ChangeOrderAsync(() =>
    {
        if (source.ItemType != DeploymentItemType.Mod || !Items.Contains(source))
            return;
        var selected = source.IsSelected
            ? Items.Where(item => item.IsSelected && item.ItemType == DeploymentItemType.Mod).ToList()
            : [source];
        var target = Math.Clamp(insertIndex, 0, Items.Count);
        target -= selected.Count(item => Items.IndexOf(item) < target);
        foreach (var item in selected)
            Items.Remove(item);
        for (var i = 0; i < selected.Count; i++)
            Items.Insert(target + i, selected[i]);
    });

    public void SelectAll() => SetSelection(item => true);
    public void DeselectAll() => SetSelection(item => false);
    public void InvertSelection() => SetSelection(item => !item.IsSelected);

    private void SetSelection(Func<DeploymentOrderItem, bool> value)
    {
        foreach (var item in Items)
            item.IsSelected = value(item);
    }

    private async Task ChangeOrderAsync(Action change)
    {
        await _saveGate.WaitAsync();
        try
        {
            var before = Items.ToArray();
            change();
            try
            {
                await _settings.SaveAsync();
            }
            catch
            {
                Items.CollectionChanged -= OnItemsChanged;
                foreach (var item in Items)
                    item.PropertyChanged -= OnItemChanged;
                Items.Clear();
                foreach (var item in before)
                    AddItem(item);
                SyncToSettings();
                Items.CollectionChanged += OnItemsChanged;
                NotifyMoveAvailability();
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void AddItem(DeploymentOrderItem item)
    {
        item.PropertyChanged += OnItemChanged;
        Items.Add(item);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncToSettings();
        NotifyMoveAvailability();
    }

    private void SyncToSettings()
    {
        _settings.DeploymentOrderGuids.Clear();
        _settings.DeploymentOrderGuids.AddRange(Items.Where(item => item.ItemType == DeploymentItemType.Mod).Select(item => item.Guid));
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeploymentOrderItem.IsSelected))
            NotifyMoveAvailability();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrderDescription)));
    }

    private void NotifyMoveAvailability()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveToTop)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveToBottom)));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _localization.PropertyChanged -= OnLocalizationChanged;
        Items.CollectionChanged -= OnItemsChanged;
        foreach (var item in Items)
            item.PropertyChanged -= OnItemChanged;
        _saveGate.Dispose();
    }
}
