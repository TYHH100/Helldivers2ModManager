using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 选项/子选项图片处理基类。
/// 统一管理 ImagePath（显示相对路径）、ImagePreview（预览）、BrowseImage（浏览）逻辑，
/// 避免在 CreateModOptionViewModel 和 CreateModSubOptionViewModel 中重复维护。
/// </summary>
internal abstract partial class ModImageViewModelBase : ObservableObject
{
    protected LocalizationService LocalizationService { get; }

    protected ModImageViewModelBase(LocalizationService localizationService)
    {
        LocalizationService = localizationService;
    }

    /// <summary>图片文件路径（显示相对路径，如 icon.png）</summary>
    [ObservableProperty]
    private string _imagePath = string.Empty;

    /// <summary>浏览选择图片时的原始文件路径（用于复制到模组目录）</summary>
    private string? _browsedImageSourcePath;

    /// <summary>
    /// 源目录路径。
    /// <list type="bullet">
    /// <item>创建页面：用户选择的源目录（SourceDirectory）</item>
    /// <item>编辑页面：模组文件所在目录</item>
    /// </list>
    /// 用于 Include 浏览定位和图片相对路径解析。
    /// </summary>
    public string SourceDirectory { get; set; } = string.Empty;

    /// <summary>图片预览，支持绝对路径和相对路径（从 SourceDirectory 解析）</summary>
    public ImageSource? ImagePreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ImagePath))
                return null;

            // 尝试从绝对路径加载（浏览选择的外部文件）
            if (Path.IsPathRooted(ImagePath) && File.Exists(ImagePath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(ImagePath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    return bmp;
                }
                catch { return null; }
            }

            // 尝试从 SourceDirectory 解析相对路径
            if (!string.IsNullOrWhiteSpace(SourceDirectory))
            {
                var fullPath = Path.Combine(SourceDirectory, ImagePath);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(fullPath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        return bmp;
                    }
                    catch { }
                }
            }

            return null;
        }
    }

    /// <summary>浏览选择图片文件</summary>
    [RelayCommand]
    void BrowseImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = BrowseImageDialogTitle,
            Filter = LocalizationService["Common.SelectImageFilter"],
            InitialDirectory = !string.IsNullOrWhiteSpace(SourceDirectory) ? SourceDirectory : null,
        };

        if (dialog.ShowDialog() == true)
        {
            _browsedImageSourcePath = dialog.FileName;
            ImagePath = Path.GetFileName(dialog.FileName);
            OnPropertyChanged(nameof(ImagePreview));
        }
    }

    /// <summary>浏览对话框标题，子类可自定义</summary>
    protected virtual string BrowseImageDialogTitle => LocalizationService["ImagePicker.SelectImageTitle"];

    /// <summary>获取图片的完整源路径（优先浏览来源，否则从 SourceDirectory 解析）</summary>
    public string ResolveImageSourcePath()
    {
        if (_browsedImageSourcePath is not null)
            return _browsedImageSourcePath;
        if (string.IsNullOrWhiteSpace(ImagePath))
            return string.Empty;
        if (Path.IsPathRooted(ImagePath))
            return ImagePath;
        if (!string.IsNullOrWhiteSpace(SourceDirectory))
            return Path.Combine(SourceDirectory, ImagePath);
        return ImagePath;
    }

    /// <summary>浏览选择后清理临时记录（保存后调用）</summary>
    public void ResetBrowsedImageSource()
    {
        _browsedImageSourcePath = null;
    }

    partial void OnImagePathChanged(string value)
    {
        OnPropertyChanged(nameof(ImagePreview));
    }
}
