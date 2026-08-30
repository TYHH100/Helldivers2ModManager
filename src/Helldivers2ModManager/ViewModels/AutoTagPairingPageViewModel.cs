using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 自动识别标签配对页：把每种自动识别类型手动配对一个已有标签，
/// 也可以直接创建新标签。配对结果在自动打标签时优先使用。
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class AutoTagPairingPageViewModel : PageViewModelBase
{
    public override string Title => _localizationService["AutoTagPairingPage.Title"];

    public ObservableCollection<AutoTagPairingItem> Items { get; } = [];

    private readonly ILogger<AutoTagPairingPageViewModel> _logger;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly NavigationStore _navigationStore;

    public AutoTagPairingPageViewModel(
        ILogger<AutoTagPairingPageViewModel> logger,
        SettingsService settingsService,
        LocalizationService localizationService,
        NavigationStore navigationStore)
    {
        _logger = logger;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _navigationStore = navigationStore;

        _localizationService.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            foreach (var item in Items)
                item.RefreshDisplayNames(localizationService);
        };

        BuildItems();
    }

    private void BuildItems()
    {
        Items.Clear();
        foreach (var def in ModTypeDetectionService.BuiltInTagDefinitions)
        {
            var item = new AutoTagPairingItem(def, _settingsService, _localizationService);
            item.CreateNewRequested += OnCreateNewRequested;
            Items.Add(item);
        }
    }

    private void OnCreateNewRequested(AutoTagPairingItem item)
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = _localizationService["TagManagementPage.CreateTitle"],
            Message = _localizationService["TagManagementPage.CreateMsg"],
            MaxLength = 16,
            Confirm = (tagName) =>
            {
                if (string.IsNullOrWhiteSpace(tagName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                    {
                        Message = _localizationService["TagManagementPage.CreateEmptyError"],
                    });
                    item.RevertSelection();
                    return;
                }

                if (_settingsService.IsReadonly)
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                    {
                        Message = _localizationService["TagManagementPage.CreateReadonly"],
                    });
                    item.RevertSelection();
                    return;
                }

                var tag = new ModTag(tagName);
                _settingsService.Tags.Add(tag);
                _ = _settingsService.SaveAsync();
                item.SelectNewTag(tag);
            },
            Abort = item.RevertSelection,
        });
    }

    [RelayCommand]
    void Save()
    {
        if (_settingsService.IsReadonly)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = _localizationService["AutoTagPairingPage.ReadonlyError"],
            });
            return;
        }

        var mappings = Items
            .Where(static item => item.SelectedTagId is not null)
            .Select(static item => new AutoTagMapping { Type = item.Type, TagId = item.SelectedTagId!.Value })
            .ToList();
        _settingsService.AutoTagMappings = mappings;
        _ = _settingsService.SaveAsync();

        _logger.LogInformation("自动标签配对已保存：{Count} 项", mappings.Count);
        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
        {
            Message = _localizationService["AutoTagPairingPage.SaveSuccess"],
        });
    }

    [RelayCommand]
    void Back()
    {
        _navigationStore.Navigate<SettingsPageViewModel>();
    }
}

/// <summary>
/// 单行配对条目：识别类型 + 可选标签（未设置 / 已有标签 / 新建标签）。
/// </summary>
internal sealed class AutoTagPairingItem : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly LocalizationService _localizationService;
    private AutoTagOption? _selected;

    public ModType Type { get; }

    public ObservableCollection<AutoTagOption> Options { get; } = [];

    public event Action<AutoTagPairingItem>? CreateNewRequested;

    public string TypeName => _localizationService[_definition.NameKey];

    private readonly ModTypeDetectionService.BuiltInTagDefinition _definition;

    public AutoTagOption? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
                return;
            if (value is { IsCreateNew: true })
            {
                // 视觉上保持原选择，弹出新建输入框
                OnPropertyChanged(nameof(Selected));
                CreateNewRequested?.Invoke(this);
                return;
            }
            _selected = value;
            OnPropertyChanged(nameof(Selected));
        }
    }

    public Guid? SelectedTagId => Selected is { TagId: { } id } ? id : null;

    public AutoTagPairingItem(
        ModTypeDetectionService.BuiltInTagDefinition definition,
        SettingsService settings,
        LocalizationService localizationService)
    {
        _definition = definition;
        Type = definition.Type;
        _settings = settings;
        _localizationService = localizationService;

        Options.Add(new AutoTagOption(null, localizationService["AutoTagPairingPage.UnsetOption"], IsUnset: true));
        foreach (var tag in settings.Tags)
            Options.Add(new AutoTagOption(tag.Id, tag.Name));
        Options.Add(new AutoTagOption(null, localizationService["AutoTagPairingPage.CreateNewOption"], IsCreateNew: true));

        _selected = ResolveInitialSelection();
    }

    private AutoTagOption? ResolveInitialSelection()
    {
        var mappedId = _settings.AutoTagMappings.FirstOrDefault(m => m.Type == Type)?.TagId;
        if (mappedId is { } id)
        {
            var mapped = Options.FirstOrDefault(o => o.TagId == id);
            if (mapped is not null)
                return mapped;
        }

        var localizedName = _localizationService[_definition.NameKey];
        var byName = _settings.Tags.FirstOrDefault(t =>
            string.Equals(t.Name?.Trim(), localizedName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            var nameOption = Options.FirstOrDefault(o => o.TagId == byName.Id);
            if (nameOption is not null)
                return nameOption;
        }

        return Options[0];
    }

    public void SelectNewTag(ModTag tag)
    {
        var option = new AutoTagOption(tag.Id, tag.Name);
        Options.Insert(Options.Count - 1, option);
        Selected = option;
    }

    public void RevertSelection() => OnPropertyChanged(nameof(Selected));

    public void RefreshDisplayNames(LocalizationService localizationService)
    {
        OnPropertyChanged(nameof(TypeName));
        Options[0] = Options[0] with { Display = localizationService["AutoTagPairingPage.UnsetOption"] };
        Options[Options.Count - 1] = Options[^1] with { Display = localizationService["AutoTagPairingPage.CreateNewOption"] };
    }
}

/// <summary>
/// 下拉选项：未设置哨兵 / 真实标签 / 新建标签哨兵。
/// </summary>
internal sealed record AutoTagOption(Guid? TagId, string Display, bool IsCreateNew = false, bool IsUnset = false)
{
    public override string ToString() => Display;
}
