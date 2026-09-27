using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Shell;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class MainWindowLayout
{
    private static readonly Brush WindowBackground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly Brush WindowBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush SecondaryForeground = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));

    public Window Window { get; }
    public ContentPresenter PagePresenter { get; }
    public Canvas MusicLayer { get; }
    public Border DropHint { get; }
    public StackPanel ValidDropHint { get; }
    public StackPanel InvalidDropHint { get; }
    private readonly Image _backgroundLayer;

    public void SetBackground(ImageSource? source, double opacity)
    {
        _backgroundLayer.Source = source;
        _backgroundLayer.Opacity = Math.Clamp(opacity, 0, 1);
        _backgroundLayer.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetDropHint(bool visible, bool supported)
    {
        DropHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ValidDropHint.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        InvalidDropHint.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
    }

    public MainWindowLayout(
        UIElement initialPage,
        ImageSource logo,
        MainWindowLabels labels,
        Action showHelp,
        Action reportBug,
        IReadOnlyList<UIElement> overlays,
        UIElement? musicPlayer = null,
        ImageSource? backgroundImage = null,
        double backgroundImageOpacity = 0.6)
    {
        ArgumentNullException.ThrowIfNull(initialPage);
        ArgumentNullException.ThrowIfNull(logo);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(showHelp);
        ArgumentNullException.ThrowIfNull(reportBug);
        ArgumentNullException.ThrowIfNull(overlays);
        if (overlays.Count != 6)
            throw new ArgumentException("The original window has six overlay layers.", nameof(overlays));

        Window = new Window
        {
            Title = $"{labels.AppName} {labels.Version}",
            Width = 1000,
            Height = 700,
            MinWidth = 800,
            MinHeight = 600,
            WindowStyle = WindowStyle.None,
            IsShowTitleBar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = WindowBackground,
        };
        WindowChrome.SetWindowChrome(Window, new WindowChrome
        {
            CaptionHeight = 48,
            CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(6),
        });

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _backgroundLayer = new Image
        {
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false,
        };
        SetBackground(backgroundImage, backgroundImageOpacity);
        Grid.SetRowSpan(_backgroundLayer, 2);
        grid.Children.Add(_backgroundLayer);

        grid.Children.Add(CreateTitleBar(logo, labels, showHelp, reportBug));

        PagePresenter = new ContentPresenter { Content = initialPage };
        Grid.SetRow(PagePresenter, 1);
        grid.Children.Add(PagePresenter);

        foreach (var overlay in overlays)
        {
            Grid.SetRow(overlay, 1);
            grid.Children.Add(overlay);
        }

        MusicLayer = new Canvas { Visibility = musicPlayer is null ? Visibility.Collapsed : Visibility.Visible };
        if (musicPlayer is not null)
        {
            // 位置由播放器按归一化设置自定位（可拖动）；布局完成后兜底调一次。
            MusicLayer.Children.Add(musicPlayer);
        }
        Grid.SetRow(MusicLayer, 1);
        Panel.SetZIndex(MusicLayer, 100);
        grid.Children.Add(MusicLayer);

        (DropHint, ValidDropHint, InvalidDropHint) = CreateDropHint(labels);
        Grid.SetRowSpan(DropHint, 2);
        Panel.SetZIndex(DropHint, 200);
        grid.Children.Add(DropHint);

        Window.Content = new Border
        {
            Background = WindowBackground,
            BorderBrush = WindowBorder,
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    private Border CreateTitleBar(ImageSource logo, MainWindowLabels labels, Action showHelp, Action reportBug)
    {
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            Background = Brushes.Transparent,
        };
        identity.Children.Add(new Image
        {
            Source = logo,
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 12, 0),
        });
        identity.Children.Add(new TextBlock
        {
            Text = labels.AppName,
            FontSize = 14,
            Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center,
        });
        identity.Children.Add(new TextBlock
        {
            Text = labels.Version,
            FontSize = 12,
            Foreground = SecondaryForeground,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        AttachTitleBarDrag(identity);
        titleGrid.Children.Add(identity);

        // 中列空白区承接拖动；背景需可命中测试（几乎透明的笔刷）。
        var dragFiller = new Border { Background = new SolidColorBrush(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF)) };
        AttachTitleBarDrag(dragFiller);
        Grid.SetColumn(dragFiller, 1);
        titleGrid.Children.Add(dragFiller);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
        };
        controls.Children.Add(CreateTitleButton("!", labels.ReportBug, reportBug));
        controls.Children.Add(CreateTitleButton("?", labels.Help, showHelp));
        controls.Children.Add(CreateTitleButton("\u2212", labels.Minimize, () => Window.WindowState = WindowState.Minimized));
        controls.Children.Add(CreateTitleButton("\u25A1", labels.Maximize, () => Window.WindowState =
            Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        controls.Children.Add(CreateTitleButton("\u00D7", labels.Close, Window.Close, isClose: true));
        Grid.SetColumn(controls, 2);
        titleGrid.Children.Add(controls);

        return new Border { Background = WindowBackground, Child = titleGrid };
    }

    // 当前包的 WindowChrome.CaptionHeight 未让 caption 命中测试生效（窗口拖不动），
    // 托管侧在标题栏按下时发送 WM_NCLBUTTONDOWN/HTCAPTION 交还原生移动循环。
    private void AttachTitleBarDrag(FrameworkElement element)
    {
        element.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                Window.WindowState = Window.WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
                return;
            }
            BeginWindowDrag();
            e.Handled = true;
        };
    }

    internal void BeginWindowDrag()
    {
        var handle = Window.Handle;
        if (handle == IntPtr.Zero)
            return;
        if (!NativeMethods.ReleaseCapture(handle))
            return;
        NativeMethods.SendMessage(handle, NativeMethods.WM_NCLBUTTONDOWN,
            (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
    }

    private static class NativeMethods
    {
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    private static Button CreateTitleButton(string glyph, string tooltip, Action action, bool isClose = false)
    {
        var button = new Button
        {
            Width = 46,
            Height = 32,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = tooltip,
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 18,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = Foreground,
            },
        };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.MouseEnter += (_, _) => button.Background = isClose
            ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C))
            : new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38));
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.Click += (_, _) => action();
        return button;
    }

    private static (Border Hint, StackPanel Valid, StackPanel Invalid) CreateDropHint(MainWindowLabels labels)
    {
        var valid = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var invalid = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        valid.Children.Add(CreateDropHintIcon("\uE896", Color.FromRgb(0x00, 0x78, 0xD4)));
        valid.Children.Add(CreateDropHintText(labels.DropImportHint));
        invalid.Children.Add(CreateDropHintIcon("\uE711", Color.FromRgb(0xC4, 0x2B, 0x1C)));
        invalid.Children.Add(CreateDropHintText(labels.DropImportInvalid));
        var root = new Grid();
        root.Children.Add(valid);
        root.Children.Add(invalid);

        var hint = new Border
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x14, 0x14, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(32),
            Child = root,
        };
        return (hint, valid, invalid);
    }

    private static TextBlock CreateDropHintIcon(string glyph, Color color) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 48,
        Foreground = new SolidColorBrush(color),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock CreateDropHintText(string text) => new()
    {
        Text = text,
        FontSize = 18,
        Foreground = Foreground,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 16, 0, 0),
    };
}

internal sealed record MainWindowLabels(
    string AppName,
    string Version,
    string ReportBug,
    string Help,
    string Minimize,
    string Maximize,
    string Close,
    string DropImportHint,
    string DropImportInvalid);
