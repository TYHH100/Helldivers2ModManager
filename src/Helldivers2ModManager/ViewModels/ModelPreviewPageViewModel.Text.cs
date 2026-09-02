using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class ModelPreviewPageViewModel
{
    internal const int TextPreviewTabIndex = 3;

    private const int MaxCachedTextInventories = 2;

    private readonly TextBankInspectionService _textInspectionService;
    private readonly Dictionary<string, TextInventoryResult> _textInventoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _textInventoryOrder = [];
    private List<TextEntryViewModel> _allTextEntries = [];
    private readonly List<TextEntryViewModel> _textViewEntries = [];

    /// <summary>虚拟化列表视图（按文本库分组、按过滤条件筛选）。与音频列表同样的约束：
    /// 必须走 ListBox 虚拟化，禁止替换为无虚拟化的 ItemsControl+ScrollViewer。</summary>
    public ListCollectionView TextEntriesView { get; private set; } = null!;

    private void InitializeTextView()
    {
        TextEntriesView = new ListCollectionView(_textViewEntries)
        {
            Filter = FilterTextEntry,
        };
        TextEntriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TextEntryViewModel.GroupKey)));
    }

    private bool FilterTextEntry(object item)
    {
        if (item is not TextEntryViewModel entry)
            return false;
        if (OnlyShowTextModified && entry.Model.MatchesOriginal != false)
            return false;
        var filter = TextFilterText.Trim();
        return filter.Length == 0 || entry.MatchesFilter(filter);
    }

    [ObservableProperty]
    private string _textFilterText = string.Empty;

    [ObservableProperty]
    private bool _onlyShowTextModified;

    public bool HasTextEntries => TextEntryTotalCount > 0;
    public int TextEntryTotalCount { get; private set; }
    public int TextBankCount { get; private set; }
    public bool HasTextOriginalComparison { get; private set; }
    public int TextModifiedCount { get; private set; }

    public string TextModifiedCountText => _localizationService["ModelPreviewPage.TextModifiedCount"]
        .Replace("{modified}", TextModifiedCount.ToString("N0"))
        .Replace("{total}", TextEntryTotalCount.ToString("N0"));

    public string TextCountText => _localizationService["ModelPreviewPage.TextEntryCount"]
        .Replace("{count}", TextEntryTotalCount.ToString("N0"))
        .Replace("{banks}", TextBankCount.ToString("N0"));

    partial void OnTextFilterTextChanged(string value) => RefreshTextFilter();

    partial void OnOnlyShowTextModifiedChanged(bool value) => RefreshTextFilter();

    private void RefreshTextFilter()
    {
        if (TextEntriesView is not null)
            TextEntriesView.Refresh();
    }

    private async Task<TextInventoryResult> LoadTextInventoryAsync(
        ModData mod,
        IReadOnlyList<FileInfo> patchFiles,
        string patchSetKey,
        int loadGeneration,
        CancellationToken cancellationToken)
    {
        if (_textInventoryCache.TryGetValue(patchSetKey, out var cached))
        {
            TouchTextInventoryCache(patchSetKey);
            return cached;
        }

        var result = await _textInspectionService.InspectAsync(mod.Directory, patchFiles, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentLoad(mod, loadGeneration))
            return TextInventoryResult.Empty;
        CacheTextInventory(patchSetKey, result);
        return result;
    }

    private void CacheTextInventory(string key, TextInventoryResult result)
    {
        _textInventoryCache[key] = result;
        _textInventoryOrder.Enqueue(key);
        while (_textInventoryOrder.Count > MaxCachedTextInventories)
        {
            var oldest = _textInventoryOrder.Dequeue();
            if (oldest != key)
                _textInventoryCache.Remove(oldest);
        }
    }

    private void TouchTextInventoryCache(string key)
    {
        if (_textInventoryOrder.Count > 0 && _textInventoryOrder.Peek() == key)
            return;
        var remaining = _textInventoryOrder.ToList();
        remaining.Remove(key);
        _textInventoryOrder.Clear();
        foreach (var item in remaining)
            _textInventoryOrder.Enqueue(item);
        _textInventoryOrder.Enqueue(key);
    }

    private void ClearTextInventoryCache()
    {
        _textInventoryCache.Clear();
        _textInventoryOrder.Clear();
    }

    private void ApplyTextInventory(TextInventoryResult result)
    {
        _allTextEntries = result.Groups
            .SelectMany(static group => group.Entries)
            .Select(model => new TextEntryViewModel(model, _localizationService))
            .ToList();

        TextEntryTotalCount = _allTextEntries.Count;
        TextBankCount = result.Groups.Count;
        TextModifiedCount = _allTextEntries.Count(static entry => entry.Model.MatchesOriginal == false);
        HasTextOriginalComparison = _allTextEntries.Any(static entry => entry.Model.MatchesOriginal is not null);
        if (!HasTextOriginalComparison)
            OnlyShowTextModified = false;

        _textViewEntries.Clear();
        _textViewEntries.AddRange(_allTextEntries);
        TextEntriesView.Refresh();

        OnPropertyChanged(nameof(HasTextEntries));
        OnPropertyChanged(nameof(TextEntryTotalCount));
        OnPropertyChanged(nameof(TextBankCount));
        OnPropertyChanged(nameof(TextCountText));
        OnPropertyChanged(nameof(HasTextOriginalComparison));
        OnPropertyChanged(nameof(TextModifiedCount));
        OnPropertyChanged(nameof(TextModifiedCountText));
    }

    private void ClearTextCollections()
    {
        _allTextEntries = [];
        TextEntryTotalCount = 0;
        TextBankCount = 0;
        TextModifiedCount = 0;
        HasTextOriginalComparison = false;
        OnlyShowTextModified = false;
        _textViewEntries.Clear();
        TextEntriesView?.Refresh();
        OnPropertyChanged(nameof(HasTextOriginalComparison));
        OnPropertyChanged(nameof(TextModifiedCountText));
        OnPropertyChanged(nameof(HasTextEntries));
        OnPropertyChanged(nameof(TextEntryTotalCount));
        OnPropertyChanged(nameof(TextBankCount));
        OnPropertyChanged(nameof(TextCountText));
    }

    private void UpdateTextSummaryStatus(int patchCount, string? error)
    {
        if (TextEntryTotalCount == 0)
            return;
        var summary = _localizationService["ModelPreviewPage.TextLoadedStatus"]
            .Replace("{count}", TextEntryTotalCount.ToString("N0"))
            .Replace("{banks}", TextBankCount.ToString("N0"))
            .Replace("{patches}", patchCount.ToString("N0"));
        StatusText = summary;
        if (!string.IsNullOrWhiteSpace(error))
            StatusText += " " + _localizationService["ModelPreviewPage.TextLoadFailed"].Replace("{message}", error);
    }
}

/// <summary>Wraps one parsed <see cref="TextEntry"/> for the text preview list.</summary>
internal sealed class TextEntryViewModel : ObservableObject
{
    public TextEntryViewModel(TextEntry model, LocalizationService localization)
    {
        Model = model;
        Localization = localization;
        GroupKey = new TextGroupKey(
            localization["ModelPreviewPage.TextBankGroup"]
                .Replace("{id}", $"0x{model.TextBankFileId:X16}")
                .Replace("{language}", TextBankFormat.GetLanguageName(model.Language)),
            model.PatchRelativePath);
    }

    /// <summary>分组键（文本库头 + 补丁相对路径），由 ListCollectionView 按值分组。</summary>
    public TextGroupKey GroupKey { get; }

    public TextEntry Model { get; }

    private LocalizationService Localization { get; }

    public string IdText => Model.StringId.ToString(CultureInfo.InvariantCulture);

    public string ModifiedText => Model.MatchesOriginal switch
    {
        false when Model.OriginalText is null => Localization["ModelPreviewPage.TextNewTag"],
        false => Localization["ModelPreviewPage.TextModifiedTag"],
        true => Localization["ModelPreviewPage.TextOriginalTag"],
        _ => string.Empty,
    };

    /// <summary>悬停提示：完整文本；已替换时附带原版文本（行内文本会被省略号截断，tooltip 承载全文）。</summary>
    public string OriginalToolTip => Model.OriginalText is { } original && Model.MatchesOriginal == false
        ? $"{Localization["ModelPreviewPage.TextOriginalTooltip"].Replace("{text}", original)}\n{Model.Text}"
        : Model.Text;

    public bool MatchesFilter(string filter)
    {
        if (filter.Length == 0)
            return true;
        return IdText.Contains(filter, StringComparison.Ordinal) ||
               Model.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               (Model.OriginalText?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
               Model.PatchRelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>文本列表的分组键：一个补丁里的一个 TEXT_BANK 资源。</summary>
internal sealed record TextGroupKey(string Header, string PatchPath);
