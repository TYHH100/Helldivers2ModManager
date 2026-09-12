using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Parsing;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class ModelPreviewPageViewModel
{
    internal const int LuaScriptsPreviewTabIndex = 4;

    private const int MaxCachedLuaInventories = 2;

    private readonly LuaScriptInspectionService _luaInspectionService;
    private readonly Dictionary<string, LuaScriptInventoryResult> _luaInventoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _luaInventoryOrder = [];

    /// <summary>脚本条目列表（每个命中资源一项）。脚本模组通常只有个位数条目，无需虚拟化。</summary>
    public ObservableCollection<LuaScriptEntryViewModel> LuaEntries { get; } = [];

    [ObservableProperty]
    private LuaScriptEntryViewModel? _selectedLuaEntry;

    [ObservableProperty]
    private string _luaMessageText = string.Empty;

    public bool HasLuaEntries => LuaEntries.Count > 0;
    public bool HasMultipleLuaEntries => LuaEntries.Count > 1;
    public bool HasLuaMessage => !string.IsNullOrEmpty(LuaMessageText);

    public string LuaCountText => _localizationService["ModelPreviewPage.LuaEntryCount"]
        .Replace("{count}", LuaEntries.Count.ToString("N0"))
        .Replace("{patches}", LuaPatchCount.ToString("N0"));

    private int LuaPatchCount { get; set; }

    public string LuaReportText => SelectedLuaEntry?.Model.Report ?? string.Empty;

    partial void OnSelectedLuaEntryChanged(LuaScriptEntryViewModel? value)
    {
        OnPropertyChanged(nameof(LuaReportText));
    }

    [RelayCommand]
    private void CopyLuaReport()
    {
        if (SelectedLuaEntry is not { } entry)
            return;
        try
        {
            Clipboard.SetText(entry.Model.Report);
            LuaMessageText = _localizationService["ModelPreviewPage.LuaCopied"];
        }
        catch (Exception ex)
        {
            LuaMessageText = _localizationService["ModelPreviewPage.LuaCopyFailed"].Replace("{message}", ex.Message);
        }

        OnPropertyChanged(nameof(HasLuaMessage));
    }

    [RelayCommand]
    private void CopyAllLuaReports()
    {
        if (LuaEntries.Count == 0)
            return;
        try
        {
            var combined = string.Join(
                Environment.NewLine + Environment.NewLine,
                LuaEntries.Select(entry => entry.Model.Report));
            Clipboard.SetText(combined);
            LuaMessageText = _localizationService["ModelPreviewPage.LuaCopied"];
        }
        catch (Exception ex)
        {
            LuaMessageText = _localizationService["ModelPreviewPage.LuaCopyFailed"].Replace("{message}", ex.Message);
        }

        OnPropertyChanged(nameof(HasLuaMessage));
    }

    /// <summary>
    /// 把脚本模组内部全部内容提取为文件（原始资源体/字节码原件/还原源码/权威清单/字符串）。
    /// 仅静态写文件，绝不执行任何内容；目标目录由用户通过文件夹选择器指定。
    /// </summary>
    [RelayCommand]
    private async Task ExtractLuaFilesAsync()
    {
        if (SelectedMod is not { } mod)
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _localizationService["ModelPreviewPage.LuaExtractDialogTitle"],
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            IsLuaExtracting = true;
            var patchFiles = GetPreviewPatchFiles(mod);
            var result = await _luaInspectionService.ExtractAsync(
                mod.Directory,
                patchFiles,
                dialog.FolderName,
                CancellationToken.None);

            if (result.Error is not null)
            {
                LuaMessageText = _localizationService["ModelPreviewPage.LuaExtractFailed"].Replace("{message}", result.Error);
            }
            else if (result.FileCount == 0)
            {
                LuaMessageText = _localizationService["ModelPreviewPage.LuaExtractEmpty"];
            }
            else
            {
                LuaMessageText = _localizationService["ModelPreviewPage.LuaExtracted"]
                    .Replace("{count}", result.FileCount.ToString("N0"))
                    .Replace("{path}", result.DestinationDirectory);
            }
        }
        catch (Exception ex)
        {
            LuaMessageText = _localizationService["ModelPreviewPage.LuaExtractFailed"].Replace("{message}", ex.Message);
        }
        finally
        {
            IsLuaExtracting = false;
            OnPropertyChanged(nameof(HasLuaMessage));
        }
    }

    [ObservableProperty]
    private bool _isLuaExtracting;

    private async Task<LuaScriptInventoryResult> LoadLuaInventoryAsync(
        ModData mod,
        IReadOnlyList<FileInfo> patchFiles,
        string patchSetKey,
        int loadGeneration,
        CancellationToken cancellationToken)
    {
        if (_luaInventoryCache.TryGetValue(patchSetKey, out var cached))
        {
            TouchLuaInventoryCache(patchSetKey);
            return cached;
        }

        var result = await _luaInspectionService.InspectAsync(mod.Directory, patchFiles, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentLoad(mod, loadGeneration))
            return LuaScriptInventoryResult.Empty;
        CacheLuaInventory(patchSetKey, result);
        return result;
    }

    private void CacheLuaInventory(string key, LuaScriptInventoryResult result)
    {
        _luaInventoryCache[key] = result;
        _luaInventoryOrder.Enqueue(key);
        while (_luaInventoryOrder.Count > MaxCachedLuaInventories)
        {
            var oldest = _luaInventoryOrder.Dequeue();
            if (oldest != key)
                _luaInventoryCache.Remove(oldest);
        }
    }

    private void TouchLuaInventoryCache(string key)
    {
        if (_luaInventoryOrder.Count > 0 && _luaInventoryOrder.Peek() == key)
            return;
        var remaining = _luaInventoryOrder.ToList();
        remaining.Remove(key);
        _luaInventoryOrder.Clear();
        foreach (var item in remaining)
            _luaInventoryOrder.Enqueue(item);
        _luaInventoryOrder.Enqueue(key);
    }

    private void ApplyLuaInventory(LuaScriptInventoryResult result)
    {
        LuaEntries.Clear();
        foreach (var group in result.Groups)
        {
            foreach (var entry in group.Entries)
                LuaEntries.Add(new LuaScriptEntryViewModel(entry, _localizationService));
        }

        LuaPatchCount = result.PatchCount;
        SelectedLuaEntry = LuaEntries.FirstOrDefault();
        LuaMessageText = string.Empty;
        OnPropertyChanged(nameof(HasLuaEntries));
        OnPropertyChanged(nameof(HasMultipleLuaEntries));
        OnPropertyChanged(nameof(LuaCountText));
        OnPropertyChanged(nameof(HasLuaMessage));
    }

    private void ClearLuaCollections()
    {
        LuaEntries.Clear();
        SelectedLuaEntry = null;
        LuaPatchCount = 0;
        LuaMessageText = string.Empty;
        OnPropertyChanged(nameof(HasLuaEntries));
        OnPropertyChanged(nameof(HasMultipleLuaEntries));
        OnPropertyChanged(nameof(LuaCountText));
        OnPropertyChanged(nameof(HasLuaMessage));
    }

    private void UpdateLuaSummaryStatus(int patchCount, string? error)
    {
        if (LuaEntries.Count == 0)
            return;
        var summary = _localizationService["ModelPreviewPage.LuaLoadedStatus"]
            .Replace("{count}", LuaEntries.Count.ToString("N0"))
            .Replace("{patches}", patchCount.ToString("N0"));
        StatusText = summary;
        if (!string.IsNullOrWhiteSpace(error))
            StatusText += " " + _localizationService["ModelPreviewPage.LuaLoadFailed"].Replace("{message}", error);
    }
}

/// <summary>Wraps one restored <see cref="LuaScriptEntry"/> for the script entry list.</summary>
internal sealed class LuaScriptEntryViewModel : ObservableObject
{
    public LuaScriptEntryViewModel(LuaScriptEntry model, LocalizationService localization)
    {
        Model = model;
        KindText = model.Kind switch
        {
            "bytecode" => localization["ModelPreviewPage.LuaKindBytecode"],
            "source" => localization["ModelPreviewPage.LuaKindSource"],
            _ => localization["ModelPreviewPage.LuaKindFailed"],
        };
    }

    public LuaScriptEntry Model { get; }

    public string KindText { get; }
}
