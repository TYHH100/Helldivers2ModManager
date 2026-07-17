using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Core.UI;

namespace Helldivers2ModManager.Views.Create;

/// <summary>
/// Include 目录选择对话框。
/// 以树形结构展示源目录的子目录，用户可以勾选要包含的目录，
/// 选中后自动生成相对于源目录的路径。
/// 对于子选项模式，只展示父选项 Include 目录内的内容，
/// 父选项的 Include 目录作为根节点（不可勾选）。
/// </summary>
internal sealed partial class IncludeDirectoryPicker : Window
{
    private readonly LocalizationService _localizationService;
    private readonly IDialogService _dialogService;

    /// <summary>用户选中的相对路径列表</summary>
    public List<string> SelectedRelativePaths { get; } = [];

    private readonly string _sourceDirectory;

    /// <summary>已存在的 Include 路径集合，用于回显勾选状态</summary>
    private readonly HashSet<string> _existingPaths;

    /// <summary>被父选项占用的 Include 路径集合，这些目录不可勾选</summary>
    private readonly HashSet<string> _disabledPaths;

    /// <summary>子选项模式下的浏览根目录（父选项的 Include 路径列表）</summary>
    private readonly List<string> _scopePaths;

    /// <summary>
    /// 创建目录选择对话框（选项模式）。
    /// 展示源目录的完整子目录树。
    /// </summary>
    /// <param name="sourceDirectory">源目录路径</param>
    /// <param name="existingIncludePaths">已有的 Include 路径（分号分隔），用于回显勾选状态</param>
    /// <param name="disabledIncludePaths">被父选项占用的 Include 路径（分号分隔），这些目录不可勾选</param>
    public IncludeDirectoryPicker(IDialogService dialogService, LocalizationService localizationService, string sourceDirectory, string existingIncludePaths = "", string disabledIncludePaths = "")
    {
        InitializeComponent();
        _dialogService = dialogService;
        _localizationService = localizationService;
        _sourceDirectory = sourceDirectory;

        _existingPaths = ParsePathString(existingIncludePaths);
        _disabledPaths = ParsePathString(disabledIncludePaths);
        _scopePaths = [];

        Loaded += async (_, _) => await BuildDirectoryTreeAsync();
    }

    /// <summary>
    /// 创建目录选择对话框（子选项模式）。
    /// 只展示父选项 Include 目录内的子目录，父选项的 Include 目录作为不可勾选的根节点。
    /// </summary>
    /// <param name="sourceDirectory">源目录路径</param>
    /// <param name="existingIncludePaths">已有的 Include 路径（分号分隔），用于回显勾选状态</param>
    /// <param name="scopePaths">浏览范围的相对路径列表（父选项的 Include 路径），只展示这些目录内的内容</param>
    /// <param name="disabledIncludePaths">被占用的路径（分号分隔），不可勾选</param>
    public IncludeDirectoryPicker(IDialogService dialogService, LocalizationService localizationService, string sourceDirectory, string existingIncludePaths, List<string> scopePaths, string disabledIncludePaths = "")
    {
        InitializeComponent();
        _dialogService = dialogService;
        _localizationService = localizationService;
        _sourceDirectory = sourceDirectory;

        _existingPaths = ParsePathString(existingIncludePaths);
        _disabledPaths = ParsePathString(disabledIncludePaths);
        _scopePaths = scopePaths ?? [];

        Loaded += async (_, _) => await BuildDirectoryTreeAsync();
    }

    /// <summary>将分号分隔的路径字符串解析为 HashSet</summary>
    private static HashSet<string> ParsePathString(string paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(paths))
        {
            foreach (var p in paths.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = p.Trim().Replace('/', '\\');
                if (!string.IsNullOrEmpty(normalized))
                    set.Add(normalized);
            }
        }
        return set;
    }

    /// <summary>构建目录树</summary>
    private async Task BuildDirectoryTreeAsync()
    {
        try
        {
            if (_scopePaths.Count > 0)
            {
                // 子选项模式：每个父选项 Include 目录作为根节点（不可勾选）
                foreach (var scopePath in _scopePaths)
                {
                    var fullScopePath = Path.Combine(_sourceDirectory, scopePath);
                    if (!Directory.Exists(fullScopePath))
                        continue;

                    var dirInfo = new DirectoryInfo(fullScopePath);
                    var rootNode = new TreeViewItem
                    {
                        Header = new TextBlock
                        {
                            Text = dirInfo.Name,
                            Foreground = Brushes.White,
                            FontWeight = FontWeights.SemiBold,
                        },
                        IsExpanded = true,
                        Tag = scopePath,
                    };

                    // 递归添加子目录（路径相对于源目录）
                    AddSubDirectories(rootNode, fullScopePath, scopePath);

                    DirectoryTree.Items.Add(rootNode);
                }

                // 如果没有任何可浏览的目录，显示提示
                if (DirectoryTree.Items.Count == 0)
                {
                    DirectoryTree.Items.Add(new TreeViewItem
                    {
                        Header = new TextBlock
                        {
                            Text = _localizationService["IncludePicker.ParentOptionMissing"],
                            Foreground = Brushes.Gray,
                        },
                    });
                }
            }
            else
            {
                // 选项模式：展示源目录的完整子目录树
                var rootInfo = new DirectoryInfo(_sourceDirectory);

                var rootNode = new TreeViewItem
                {
                    Header = new TextBlock
                    {
                        Text = rootInfo.Name,
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.SemiBold,
                    },
                    IsExpanded = true,
                    Tag = "",
                };

                AddSubDirectories(rootNode, _sourceDirectory, "");

                DirectoryTree.Items.Add(rootNode);
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(
                new MessageDialogRequest(
                    _localizationService["MessageBox.Error"],
                    _localizationService.Format("IncludePicker.ReadDirectoryError", new { message = ex.Message }),
                    MessageDialogSeverity.Error),
                CancellationToken.None);
        }
    }

    /// <summary>递归添加子目录节点</summary>
    private void AddSubDirectories(TreeViewItem parentNode, string fullPath, string relativePath)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullPath))
            {
                var dirInfo = new DirectoryInfo(dir);
                var dirRelativePath = string.IsNullOrEmpty(relativePath)
                    ? dirInfo.Name
                    : relativePath + "\\" + dirInfo.Name;

                var node = new TreeViewItem
                {
                    Header = CreateItemHeader(dirInfo.Name, dirRelativePath),
                    Tag = dirRelativePath,
                    IsExpanded = ShouldExpand(dirRelativePath),
                };

                // 递归添加子目录
                AddSubDirectories(node, dir, dirRelativePath);

                parentNode.Items.Add(node);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 无权限访问的目录跳过
        }
        catch (Exception ex) when (ex is IOException or PathTooLongException)
        {
            // 其他 IO 异常静默跳过，不影响目录树展示
        }
    }

    /// <summary>判断指定相对路径的节点是否需要自动展开</summary>
    private bool ShouldExpand(string relativePath)
    {
        var prefix = relativePath + "\\";
        foreach (var existing in _existingPaths)
        {
            if (existing.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                existing.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var disabled in _disabledPaths)
        {
            if (disabled.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                disabled.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 创建目录项的 Header。
    /// 如果目录被父选项占用，显示为禁用状态的复选框；
    /// 如果目录已在已有路径中，显示为已勾选的复选框；
    /// 否则显示为未勾选的复选框。
    /// </summary>
    private object CreateItemHeader(string displayName, string relativePath)
    {
        // 被占用的目录，显示为禁用复选框并标注提示
        if (_disabledPaths.Contains(relativePath))
        {
            var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var checkBox = new CheckBox
            {
                IsChecked = false,
                IsEnabled = false,
                Tag = relativePath,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var nameText = new TextBlock
            {
                Text = displayName,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = TextDecorations.Strikethrough,
            };
            var hint = new TextBlock
            {
                Text = _localizationService["IncludePicker.OptionOccupied"],
                Foreground = Brushes.Gray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            stackPanel.Children.Add(checkBox);
            stackPanel.Children.Add(nameText);
            stackPanel.Children.Add(hint);
            return stackPanel;
        }

        // 正常目录，显示可勾选的复选框
        var isChecked = _existingPaths.Contains(relativePath);
        return new CheckBox
        {
            Content = displayName,
            Tag = relativePath,
            Foreground = Brushes.White,
            IsChecked = isChecked,
        };
    }

    /// <summary>确定按钮：收集所有勾选的目录相对路径</summary>
    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        CollectCheckedItems(DirectoryTree.Items);
        DialogResult = true;
    }

    /// <summary>递归收集所有勾选的 TreeViewItem</summary>
    private void CollectCheckedItems(ItemCollection items)
    {
        foreach (var item in items)
        {
            if (item is TreeViewItem treeItem)
            {
                // 检查 Header 为 CheckBox 的情况（正常可勾选目录）
                if (treeItem.Header is CheckBox checkBox && checkBox.IsChecked == true && checkBox.IsEnabled)
                {
                    var path = checkBox.Tag as string;
                    if (!string.IsNullOrEmpty(path))
                        SelectedRelativePaths.Add(path);
                }

                // 递归检查子节点
                if (treeItem.Items.Count > 0)
                    CollectCheckedItems(treeItem.Items);
            }
        }
    }

    /// <summary>取消按钮</summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
