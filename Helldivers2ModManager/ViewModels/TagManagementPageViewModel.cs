using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class TagManagementPageViewModel : PageViewModelBase
{
    public override string Title => "Tag Manager";

    public ObservableCollection<ModTag> Tags => _settingsService.Initialized ? _settingsService.Tags : [];

    [ObservableProperty]
    private ModTag? _selectedTag;

    private readonly ILogger<TagManagementPageViewModel> _logger;
    private readonly SettingsService _settingsService;
    private readonly NavigationStore _navigationStore;
    private readonly ModService _modService;

    public TagManagementPageViewModel(ILogger<TagManagementPageViewModel> logger, SettingsService settingsService, NavigationStore navigationStore, ModService modService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _navigationStore = navigationStore;
        _modService = modService;
    }

    [RelayCommand]
    void CreateTag()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = "创建标签",
            Message = "请输入新标签的名称：",
            MaxLength = 16,
            Confirm = (tagName) =>
            {
                if (string.IsNullOrWhiteSpace(tagName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "标签名称不能为空" });
                    return;
                }

                if (!_settingsService.IsReadonly)
                {
                    _settingsService.Tags.Add(new ModTag(tagName));
                    _ = _settingsService.SaveAsync();
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "标签创建成功" });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法创建标签，设置处于只读模式" });
                }
            }
        });
    }

    [RelayCommand]
    void RenameTag(ModTag? tag)
    {
        if (tag == null)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = "重命名标签",
            Message = "请输入新的标签名称：",
            MaxLength = 16,
            InitialText = tag.Name,
            Confirm = (newName) =>
            {
                if (string.IsNullOrWhiteSpace(newName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "标签名称不能为空" });
                    return;
                }

                if (!_settingsService.IsReadonly)
                {
                    tag.Name = newName;
                    _ = _settingsService.SaveAsync();
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "标签重命名成功" });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法重命名标签，设置处于只读模式" });
                }
            }
        });
    }

    [RelayCommand]
    void ChangeColor(ModTag? tag)
    {
        if (tag == null)
            return;

        if (!_settingsService.IsReadonly)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxColorPickerMessage
            {
                Title = "选择颜色",
                Message = $"为标签 \"{tag.Name}\" 选择颜色：",
                CurrentColor = tag.Color,
                Confirm = (colorCode) =>
                {
                    tag.Color = colorCode;
                    _ = _settingsService.SaveAsync();
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "标签颜色已更新" });
                }
            });
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法更改颜色，设置处于只读模式" });
        }
    }

    [RelayCommand]
    void DeleteTag(ModTag? tag)
    {
        if (tag == null)
            return;

        if (!_settingsService.IsReadonly)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = "确认删除",
                Message = $"确定要删除标签 \"{tag.Name}\" 吗？",
                Confirm = () =>
                {
                    _settingsService.Tags.Remove(tag);
                    _ = _settingsService.SaveAsync();

                    SelectedTag = null;
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "标签删除成功" });
                }
            });
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法删除标签，设置处于只读模式" });
        }
    }

    [RelayCommand]
    void Back()
    {
        _navigationStore.Navigate<DashboardPageViewModel>();
    }
}

