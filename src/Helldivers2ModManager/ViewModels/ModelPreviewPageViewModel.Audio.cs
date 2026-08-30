using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class ModelPreviewPageViewModel
{
    internal const int AudioPreviewTabIndex = 2;

    private const int MaxCachedAudioInventories = 2;

    private readonly AudioBankInspectionService _audioInspectionService;
    private readonly AudioPlaybackService _audioPlaybackService;
    private readonly ModTypeDetectionService _modTypeDetectionService;
    private readonly Dictionary<string, AudioInventoryResult> _audioInventoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _audioInventoryOrder = [];
    private readonly DispatcherTimer _audioPositionTimer;
    private List<AudioEntryViewModel> _allAudioEntries = [];
    private AudioEntryViewModel? _currentAudioEntry;
    private int _audioUncomparedCount;
    private bool _suppressAudioPositionUpdates;

    private readonly List<AudioEntryViewModel> _viewEntries = [];

    /// <summary>虚拟化列表视图（按音频库分组、按过滤条件筛选）。语音包可达数千条目，
    /// UI 端必须走 ListBox 虚拟化，禁止替换为无虚拟化的 ItemsControl+ScrollViewer。
    /// 源是普通 List（无变更事件），任何内容/过滤变化都通过一次 Refresh() 应用。</summary>
    public ListCollectionView AudioEntriesView { get; private set; } = null!;

    /// <summary>在主构造函数中调用：列表视图依赖 UI 线程创建。</summary>
    private void InitializeAudioView()
    {
        AudioEntriesView = new ListCollectionView(_viewEntries)
        {
            Filter = FilterAudioEntry,
        };
        AudioEntriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AudioEntryViewModel.GroupKey)));
    }

    private bool FilterAudioEntry(object item)
    {
        if (item is not AudioEntryViewModel entry)
            return false;
        if (OnlyShowModified && entry.Model.MatchesOriginal != false)
            return false;
        var filter = AudioFilterText.Trim();
        return filter.Length == 0 || entry.MatchesFilter(filter);
    }

    [ObservableProperty]
    private AudioEntryViewModel? _selectedAudioEntry;

    [ObservableProperty]
    private string _audioFilterText = string.Empty;

    [ObservableProperty]
    private bool _onlyShowModified;

    [ObservableProperty]
    private bool _isAudioBusy;

    [ObservableProperty]
    private bool _isAudioPlaying;

    [ObservableProperty]
    private string _audioMessageText = string.Empty;

    [ObservableProperty]
    private double _audioPositionSeconds;

    [ObservableProperty]
    private double _audioDurationSeconds;

    [ObservableProperty]
    private double _audioVolumePercent = 100;

    [ObservableProperty]
    private int _selectedPreviewTabIndex;

    public bool HasAudioEntries => AudioEntryTotalCount > 0;
    public bool IsAudioOnlyPreview => HasAudioEntries && !HasModel;
    public int AudioEntryTotalCount { get; private set; }
    public int AudioBankCount { get; private set; }
    public bool HasAudioMessage => !string.IsNullOrEmpty(AudioMessageText);

    /// <summary>有游戏原版基线时才显示“只看已替换”过滤和替换标记。</summary>
    public bool HasOriginalComparison { get; private set; }

    public int AudioModifiedCount { get; private set; }

    public string AudioModifiedCountText => _localizationService["ModelPreviewPage.AudioModifiedCount"]
        .Replace("{modified}", AudioModifiedCount.ToString("N0"))
        .Replace("{total}", AudioEntryTotalCount.ToString("N0"));

    public AudioEntryViewModel? CurrentAudioEntry
    {
        get => _currentAudioEntry;
        private set
        {
            if (ReferenceEquals(_currentAudioEntry, value))
                return;
            var old = _currentAudioEntry;
            _currentAudioEntry = value;
            old?.SetPlaying(false);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioCurrentEntryText));
        }
    }

    public string AudioCurrentEntryText =>
        CurrentAudioEntry is { } entry
            ? entry.DisplayTitle
            : _localizationService["ModelPreviewPage.AudioNoSelection"];

    public string AudioPlaybackGlyph => IsAudioPlaying ? "\uE769" : "\uE768";

    public string AudioPlaybackToolTip => _localizationService[
        IsAudioPlaying ? "ModelPreviewPage.AudioPause" : "ModelPreviewPage.AudioPlay"];

    public string AudioTimeText
    {
        get
        {
            var position = TimeSpan.FromSeconds(AudioPositionSeconds);
            var duration = TimeSpan.FromSeconds(AudioDurationSeconds);
            return $"{position:hh\\:mm\\:ss} / {duration:hh\\:mm\\:ss}";
        }
    }

    public string AudioCountText => _localizationService["ModelPreviewPage.AudioEntryCount"]
        .Replace("{count}", AudioEntryTotalCount.ToString("N0"))
        .Replace("{banks}", AudioBankCount.ToString("N0"));

    partial void OnAudioFilterTextChanged(string value) => RefreshAudioFilter();

    partial void OnOnlyShowModifiedChanged(bool value) => RefreshAudioFilter();

    private void RefreshAudioFilter()
    {
        if (AudioEntriesView is not null)
            AudioEntriesView.Refresh();
    }


    partial void OnIsAudioPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(AudioPlaybackGlyph));
        OnPropertyChanged(nameof(AudioPlaybackToolTip));
    }

    partial void OnAudioMessageTextChanged(string value) => OnPropertyChanged(nameof(HasAudioMessage));

    partial void OnAudioPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(AudioTimeText));
        // 暂停状态下也允许拖动进度条（NVorbis 的 SamplePosition setter 在暂停时同样有效）。
        if (!_suppressAudioPositionUpdates && CurrentAudioEntry is not null && _audioPlaybackService.Duration > TimeSpan.Zero)
            _audioPlaybackService.Seek(value);
    }

    partial void OnAudioDurationSecondsChanged(double value) => OnPropertyChanged(nameof(AudioTimeText));

    partial void OnAudioVolumePercentChanged(double value) =>
        _audioPlaybackService.SetVolume((float)(value / 100.0));

    [RelayCommand]
    private async Task ToggleAudioEntryPlayback(AudioEntryViewModel? entry)
    {
        if (entry is null)
            return;
        if (ReferenceEquals(CurrentAudioEntry, entry))
        {
            if (_audioPlaybackService.State == AudioPlaybackState.Playing)
            {
                _audioPlaybackService.Pause();
                IsAudioPlaying = false;
                entry.SetPlaying(false);
                return;
            }
            if (_audioPlaybackService.State == AudioPlaybackState.Paused)
            {
                _audioPlaybackService.Resume();
                IsAudioPlaying = true;
                entry.SetPlaying(true);
                return;
            }
        }

        if (!entry.IsPlayable)
        {
            AudioMessageText = entry.IssueText;
            return;
        }

        AudioMessageText = string.Empty;
        IsAudioBusy = true;
        try
        {
            var (success, error) = await _audioPlaybackService.PlayAsync(entry.Model, CancellationToken.None);
            if (success)
            {
                CurrentAudioEntry = entry;
                SelectedAudioEntry = entry;
                IsAudioPlaying = true;
                entry.SetPlaying(true);
                _suppressAudioPositionUpdates = true;
                try
                {
                    AudioPositionSeconds = 0;
                    AudioDurationSeconds = _audioPlaybackService.Duration.TotalSeconds;
                }
                finally
                {
                    _suppressAudioPositionUpdates = false;
                }
                _audioPositionTimer.Start();
            }
            else if (error is not null)
            {
                AudioMessageText = _localizationService["ModelPreviewPage.AudioPlaybackFailed"]
                    .Replace("{message}", error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio preview playback failed");
            AudioMessageText = _localizationService["ModelPreviewPage.AudioPlaybackFailed"]
                .Replace("{message}", ex.Message);
        }
        finally
        {
            IsAudioBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleAudioPlayback()
    {
        var entry = CurrentAudioEntry ?? SelectedAudioEntry;
        if (entry is null)
            return;
        _ = ToggleAudioEntryPlayback(entry);
    }

    [RelayCommand]
    private void StopAudio()
    {
        StopAudioPlayback(clearCurrent: true);
    }

    private void StopAudioPlayback(bool clearCurrent)
    {
        _audioPositionTimer.Stop();
        _audioPlaybackService.Stop();
        IsAudioPlaying = false;
        _suppressAudioPositionUpdates = true;
        try
        {
            AudioPositionSeconds = 0;
            AudioDurationSeconds = 0;
        }
        finally
        {
            _suppressAudioPositionUpdates = false;
        }
        if (clearCurrent)
            CurrentAudioEntry = null;
        else
            CurrentAudioEntry?.SetPlaying(false);
    }

    private void AudioPositionTimerOnTick(object? sender, EventArgs e)
    {
        if (_audioPlaybackService.State is not (AudioPlaybackState.Playing or AudioPlaybackState.Paused))
            return;
        _suppressAudioPositionUpdates = true;
        try
        {
            AudioPositionSeconds = _audioPlaybackService.Position.TotalSeconds;
            var duration = _audioPlaybackService.Duration.TotalSeconds;
            if (duration > 0 && Math.Abs(AudioDurationSeconds - duration) > 0.001)
                AudioDurationSeconds = duration;
        }
        finally
        {
            _suppressAudioPositionUpdates = false;
        }
    }

    private void AudioPlaybackServiceOnPlaybackEnded(AudioEntry entry, string? error)
    {
        // PlaybackStopped fires on an audio thread; all state below is UI-bound.
        void Apply()
        {
            if (!ReferenceEquals(CurrentAudioEntry?.Model, entry))
                return;
            IsAudioPlaying = false;
            _audioPositionTimer.Stop();
            _suppressAudioPositionUpdates = true;
            try
            {
                // 播放自然结束后进度归零，避免滑块停留在末端。
                AudioPositionSeconds = 0;
            }
            finally
            {
                _suppressAudioPositionUpdates = false;
            }
            CurrentAudioEntry = null;
            if (error is not null)
            {
                AudioMessageText = _localizationService["ModelPreviewPage.AudioPlaybackFailed"]
                    .Replace("{message}", error);
            }
        }

        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    /// <summary>多选项全音频模组跳过预览（用户决策）：每个选项各自带 bank/stream，
    /// 一次性解析+基线比对代价高；部署后在游戏内体验即可。</summary>
    internal static bool ShouldSkipAudioPreviewCore(int optionCount, ModType detectedType)
        => optionCount > 1 && detectedType == ModType.Audio;

    private async Task<bool> ShouldSkipAudioPreviewAsync(ModData mod, CancellationToken cancellationToken)
    {
        if (mod.Manifest is not V1ModManifest { Options: { Count: > 1 } options })
            return false;
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detection = _modTypeDetectionService.Detect(mod.Directory);
                return ShouldSkipAudioPreviewCore(options.Count, detection.Type);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio mod type detection failed for {Mod}", mod.Manifest.Name);
            return false;
        }
    }

    /// <summary>Runs beside the model preview load; guarded by the same generation counter.</summary>
    private async Task<AudioInventoryResult> LoadAudioInventoryAsync(
        ModData mod,
        IReadOnlyList<FileInfo> patchFiles,
        string patchSetKey,
        int loadGeneration,
        CancellationToken cancellationToken)
    {
        if (_audioInventoryCache.TryGetValue(patchSetKey, out var cached))
        {
            TouchAudioInventoryCache(patchSetKey);
            return cached;
        }

        var result = await _audioInspectionService.InspectAsync(mod.Directory, patchFiles, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentLoad(mod, loadGeneration))
            return AudioInventoryResult.Empty;
        CacheAudioInventory(patchSetKey, result);
        return result;
    }

    private void CacheAudioInventory(string key, AudioInventoryResult result)
    {
        _audioInventoryCache[key] = result;
        _audioInventoryOrder.Enqueue(key);
        while (_audioInventoryOrder.Count > MaxCachedAudioInventories)
        {
            var oldest = _audioInventoryOrder.Dequeue();
            if (oldest != key)
                _audioInventoryCache.Remove(oldest);
        }
    }

    private void TouchAudioInventoryCache(string key)
    {
        // Re-enqueue so the oldest eviction candidate is the least recently used entry.
        if (_audioInventoryOrder.Count > 0 && _audioInventoryOrder.Peek() == key)
            return;
        var remaining = _audioInventoryOrder.ToList();
        remaining.Remove(key);
        _audioInventoryOrder.Clear();
        foreach (var item in remaining)
            _audioInventoryOrder.Enqueue(item);
        _audioInventoryOrder.Enqueue(key);
    }

    private void ClearAudioInventoryCache()
    {
        _audioInventoryCache.Clear();
        _audioInventoryOrder.Clear();
    }

    private void ApplyAudioInventory(AudioInventoryResult result)
    {
        StopAudioPlayback(clearCurrent: true);
        AudioMessageText = string.Empty;

        _allAudioEntries = result.Groups
            .SelectMany(static group => group.Entries)
            .Select(model => new AudioEntryViewModel(model, _localizationService))
            .ToList();
        if (CurrentAudioEntry is { } current && !_allAudioEntries.Contains(current))
            CurrentAudioEntry = null;

        AudioEntryTotalCount = _allAudioEntries.Count;
        AudioBankCount = result.Groups.Count(static group => group.BankName is not null);
        AudioModifiedCount = _allAudioEntries.Count(static entry => entry.Model.MatchesOriginal == false);
        HasOriginalComparison = _allAudioEntries.Any(static entry => entry.Model.MatchesOriginal is not null);
        _audioUncomparedCount = HasOriginalComparison ? result.UncomparedEntries : 0;
        if (!HasOriginalComparison)
            OnlyShowModified = false;

        _viewEntries.Clear();
        _viewEntries.AddRange(_allAudioEntries);
        AudioEntriesView.Refresh();

        OnPropertyChanged(nameof(HasAudioEntries));
        OnPropertyChanged(nameof(IsAudioOnlyPreview));
        OnPropertyChanged(nameof(AudioEntryTotalCount));
        OnPropertyChanged(nameof(AudioBankCount));
        OnPropertyChanged(nameof(AudioCountText));
        OnPropertyChanged(nameof(HasOriginalComparison));
        OnPropertyChanged(nameof(AudioModifiedCount));
        OnPropertyChanged(nameof(AudioModifiedCountText));
    }

    private void ClearAudioCollections()
    {
        _allAudioEntries = [];
        AudioEntryTotalCount = 0;
        AudioBankCount = 0;
        AudioModifiedCount = 0;
        HasOriginalComparison = false;
        _audioUncomparedCount = 0;
        OnlyShowModified = false;
        _viewEntries.Clear();
        AudioEntriesView?.Refresh();
        OnPropertyChanged(nameof(HasOriginalComparison));
        OnPropertyChanged(nameof(AudioModifiedCountText));
        CurrentAudioEntry = null;
        SelectedAudioEntry = null;
        AudioMessageText = string.Empty;
        OnPropertyChanged(nameof(HasAudioEntries));
        OnPropertyChanged(nameof(IsAudioOnlyPreview));
        OnPropertyChanged(nameof(AudioEntryTotalCount));
        OnPropertyChanged(nameof(AudioBankCount));
        OnPropertyChanged(nameof(AudioCountText));
    }

    private void UpdateAudioSummaryStatus(int patchCount, string? error)
    {
        if (AudioEntryTotalCount == 0)
            return;
        var summary = _localizationService["ModelPreviewPage.AudioLoadedStatus"]
            .Replace("{count}", AudioEntryTotalCount.ToString("N0"))
            .Replace("{banks}", AudioBankCount.ToString("N0"))
            .Replace("{patches}", patchCount.ToString("N0"));
        StatusText = summary;
        if (_audioUncomparedCount > 0)
        {
            StatusText += " " + _localizationService["ModelPreviewPage.AudioUncomparedHint"]
                .Replace("{count}", _audioUncomparedCount.ToString("N0"));
        }
        if (!string.IsNullOrWhiteSpace(error))
            StatusText += " " + _localizationService["ModelPreviewPage.AudioLoadFailed"].Replace("{message}", error);
    }
}

/// <summary>Wraps one parsed <see cref="AudioEntry"/> for the audio preview list.</summary>
internal sealed class AudioEntryViewModel : ObservableObject
{
    public AudioEntryViewModel(AudioEntry model, LocalizationService localization)
    {
        Model = model;
        Localization = localization;
        var header = model.BankName is { Length: > 0 } bankName
            ? bankName
            : model.Origin == AudioEntryOrigin.BankMedia
                ? string.Create(CultureInfo.InvariantCulture, $"Bank 0x{model.BankFileId:X16}")
                : Localization["ModelPreviewPage.AudioLooseStreamsGroup"];
        GroupKey = new AudioGroupKey(header, model.PatchRelativePath);
    }

    /// <summary>分组键（音频库头 + 补丁相对路径），由 ListCollectionView 按值分组。</summary>
    public AudioGroupKey GroupKey { get; }

    public AudioEntry Model { get; }

    private LocalizationService Localization { get; }

    public bool IsPlayable => Model.IsPlayable;

    public string IdText => Model.SourceId.ToString(CultureInfo.InvariantCulture);

    public string OriginText => Model.Origin switch
    {
        AudioEntryOrigin.BankMedia => "Bank",
        _ => "Stream",
    };

    public string FormatText => Model.SampleRate > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Model.SampleRate / 1000.0:0.#} kHz · {Model.Channels}ch")
        : "—";

    public string IssueText => Model.Issue == AudioEntryIssue.None
        ? string.Empty
        : Model.Issue switch
        {
            AudioEntryIssue.NotRiff => Localization["ModelPreviewPage.AudioIssueNotRiff"],
            AudioEntryIssue.NotVorbis => Localization["ModelPreviewPage.AudioIssueNotVorbis"],
            AudioEntryIssue.Truncated => Localization["ModelPreviewPage.AudioIssueTruncated"],
            _ => Localization["ModelPreviewPage.AudioIssueReadFailed"],
        };

    public string DisplayTitle => $"#{IdText}";

    public string ModifiedText => Model.MatchesOriginal switch
    {
        false => Localization["ModelPreviewPage.AudioModifiedTag"],
        true => Localization["ModelPreviewPage.AudioOriginalTag"],
        _ => string.Empty,
    };

    private bool _isPlaying;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (_isPlaying == value)
                return;
            _isPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayGlyph));
        }
    }

    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";

    /// <summary>The page view model owns the playback state; entries only mirror it for the UI.</summary>
    public void SetPlaying(bool playing) => IsPlaying = playing;

    public bool MatchesFilter(string filter)
    {
        if (filter.Length == 0)
            return true;
        return IdText.Contains(filter, StringComparison.Ordinal) ||
               OriginText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               (Model.BankName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
               Model.PatchRelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>音频列表的分组键：一个 Wwise bank（含同补丁的流媒体）或一个补丁的独立音频流。</summary>
internal sealed record AudioGroupKey(string Header, string PatchPath);
