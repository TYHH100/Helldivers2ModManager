using Helldivers2ModManager.ViewModels;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Helldivers2ModManager;

internal partial class MainWindow : Window
{
    private bool _isNarrowNavigationOpen;
    private ContextMenu? _uiTestContextMenu;
    private ToolTip? _uiTestToolTip;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ApplyUiTestWindowSize();

        DataContext = viewModel;
        SizeChanged += MainWindow_SizeChanged;
        Loaded += (_, _) =>
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("HD2MM_TEST_OPEN_NAVIGATION"),
                    "1",
                    StringComparison.Ordinal))
            {
                _isNarrowNavigationOpen = true;
            }
            UpdateNavigationLayout();
            if (!BeginDeferredUiTestPageNavigation())
            {
                NavigateUiTestPage();
                CompleteUiTestPageSetup();
            }
        };
    }

    private bool BeginDeferredUiTestPageNavigation()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal) ||
            DataContext is not MainViewModel viewModel)
        {
            return false;
        }

        switch (Environment.GetEnvironmentVariable("HD2MM_TEST_PAGE"))
        {
            case "ManifestEditor":
                var started = DateTime.UtcNow;
                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                timer.Tick += (_, _) =>
                {
                    if (!viewModel.TryNavigateManifestForUiTest() && DateTime.UtcNow - started < TimeSpan.FromSeconds(15))
                        return;
                    timer.Stop();
                    CompleteUiTestPageSetup();
                };
                timer.Start();
                return true;
            case "VersionDetail":
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    () =>
                    {
                        FindVisual<Components.VersionCheckDetailOverlay>()?.ShowUiTestSample();
                        CompleteUiTestPageSetup();
                    });
                return true;
            case "Dialog":
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    () =>
                    {
                        FindVisual<Components.MessageBox>()?.ShowUiTestConfirmation();
                        CompleteUiTestPageSetup();
                    });
                return true;
            case "ContextMenu":
                return BeginDeferredPopupTest(OpenUiTestContextMenu);
            case "Tooltip":
                return BeginDeferredPopupTest(OpenUiTestTooltip);
            default:
                return false;
        }
    }

    private bool BeginDeferredPopupTest(Func<bool> openPopup)
    {
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        timer.Tick += (_, _) =>
        {
            if (!openPopup() && DateTime.UtcNow - started < TimeSpan.FromSeconds(15))
                return;
            timer.Stop();
            CompleteUiTestPageSetup();
        };
        timer.Start();
        return true;
    }

    private bool OpenUiTestContextMenu()
    {
        if (Resources["UiTestContextMenu"] is ContextMenu testMenu)
        {
            _uiTestContextMenu = testMenu;
            _uiTestContextMenu.PlacementTarget = this;
            _uiTestContextMenu.IsOpen = true;
            return true;
        }
        foreach (var element in EnumerateVisuals(this).OfType<FrameworkElement>())
        {
            if (!element.IsVisible || element.ContextMenu is null || element.ContextMenu.Visibility == Visibility.Collapsed)
                continue;
            _uiTestContextMenu = element.ContextMenu;
            _uiTestContextMenu.PlacementTarget = element;
            _uiTestContextMenu.IsOpen = true;
            return true;
        }
        return false;
    }

    private bool OpenUiTestTooltip()
    {
        foreach (var element in EnumerateVisuals(this).OfType<FrameworkElement>())
        {
            if (!element.IsVisible || element.ToolTip is null)
                continue;
            if (element.ToolTip is ToolTip toolTip)
            {
                _uiTestToolTip = toolTip;
            }
            else
            {
                _uiTestToolTip = new ToolTip { Content = element.ToolTip };
                ToolTipService.SetToolTip(element, _uiTestToolTip);
            }
            _uiTestToolTip.PlacementTarget = element;
            _uiTestToolTip.IsOpen = true;
            return true;
        }
        return false;
    }

    private void CompleteUiTestPageSetup()
    {
        WriteUiTestWindowMetrics();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                UpdateLayout();
                WriteUiTestLayoutReport();
                BeginUiTestLocaleSwitch();
            });
    }

    private void ApplyUiTestWindowSize()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal))
            return;

        if (double.TryParse(
                Environment.GetEnvironmentVariable("HD2MM_TEST_WINDOW_WIDTH"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var width) && width >= MinWidth)
            Width = width;
        if (double.TryParse(
                Environment.GetEnvironmentVariable("HD2MM_TEST_WINDOW_HEIGHT"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var height) && height >= MinHeight)
            Height = height;
    }

    private void WriteUiTestWindowMetrics()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal))
            return;

        var metricsPath = Environment.GetEnvironmentVariable("HD2MM_TEST_WINDOW_METRICS_PATH");
        if (string.IsNullOrWhiteSpace(metricsPath))
            return;

        WriteUiTestFileAtomically(
            metricsPath,
            FormattableString.Invariant($"{ActualWidth}|{ActualHeight}"));
    }

    private void NavigateUiTestPage()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal) ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        switch (Environment.GetEnvironmentVariable("HD2MM_TEST_PAGE"))
        {
            case "Create":
                viewModel.NavigateCreateCommand.Execute(null);
                break;
            case "Downloads":
                viewModel.NavigateDownloadsCommand.Execute(null);
                break;
            case "Tasks":
                viewModel.NavigateBackgroundTasksCommand.Execute(null);
                break;
            case "Settings":
                viewModel.NavigateSettingsCommand.Execute(null);
                break;
            case "Help":
                viewModel.NavigateHelpCommand.Execute(null);
                break;
            case "Tags":
                viewModel.NavigateTagManagementForUiTest();
                break;
            case "DeploymentOrder":
                viewModel.NavigateDeploymentOrderForUiTest();
                break;
        }
    }

    private void BeginUiTestLocaleSwitch()
    {
        var targetLocale = Environment.GetEnvironmentVariable("HD2MM_TEST_SWITCH_LOCALE");
        var reportPath = Environment.GetEnvironmentVariable("HD2MM_TEST_SWITCH_LAYOUT_REPORT_PATH");
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(targetLocale) ||
            string.IsNullOrWhiteSpace(reportPath) ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var pageBefore = viewModel.CurrentViewModel;
        var pageIdentity = RuntimeHelpers.GetHashCode(pageBefore);
        var serviceIdentity = viewModel.UiTestBusinessServiceIdentity;
        var sourceLocale = viewModel.UiTestCurrentLocale;
        var layoutUpdated = false;
        EventHandler layoutHandler = (_, _) => layoutUpdated = true;
        LayoutUpdated += layoutHandler;

        viewModel.SwitchLocaleForUiTest(targetLocale);
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () =>
            {
                LayoutUpdated -= layoutHandler;
                var runtimeState = new UiTestRuntimeState(
                    sourceLocale,
                    targetLocale,
                    viewModel.UiTestCurrentLocale,
                    CultureInfo.CurrentCulture.Name,
                    CultureInfo.CurrentUICulture.Name,
                    pageBefore.GetType().FullName ?? pageBefore.GetType().Name,
                    ReferenceEquals(pageBefore, viewModel.CurrentViewModel) &&
                    pageIdentity == RuntimeHelpers.GetHashCode(viewModel.CurrentViewModel),
                    serviceIdentity == viewModel.UiTestBusinessServiceIdentity,
                    layoutUpdated);
                UpdateLayout();
                WriteUiTestLayoutReport(reportPath, runtimeState);
            });
    }

    private void WriteUiTestLayoutReport(
        string? explicitReportPath = null,
        UiTestRuntimeState? runtimeState = null)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal))
            return;
        var reportPath = explicitReportPath ?? Environment.GetEnvironmentVariable("HD2MM_TEST_LAYOUT_REPORT_PATH");
        if (string.IsNullOrWhiteSpace(reportPath))
            return;

        var controls = new List<UiTestControlReport>();
        var issues = new List<string>();
        foreach (var element in GetUiTestVisualRoots().SelectMany(EnumerateVisuals).OfType<FrameworkElement>())
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                continue;

            if (element is Button button)
            {
                AddButtonLayoutIssues(button, issues);
                var automationId = AutomationProperties.GetAutomationId(button);
                if (automationId.StartsWith("Navigation.", StringComparison.Ordinal) ||
                    automationId.StartsWith("Window.", StringComparison.Ordinal))
                {
                    var name = AutomationProperties.GetName(button);
                    var position = GetPosition(button);
                    controls.Add(new UiTestControlReport(
                        automationId,
                        name,
                        button.IsEnabled,
                        button.Focusable,
                        position.X,
                        position.Y,
                        button.ActualWidth,
                        button.ActualHeight));
                    if (string.IsNullOrWhiteSpace(name))
                        issues.Add($"{automationId}: missing accessible name");
                    if (button.IsEnabled && !button.Focusable)
                        issues.Add($"{automationId}: enabled button is not keyboard focusable");
                }
            }

            if (element is TextBlock textBlock &&
                textBlock.TextTrimming == TextTrimming.None &&
                !string.IsNullOrWhiteSpace(textBlock.Text))
            {
                AddTextLayoutIssue(textBlock, issues);
            }
        }

        AddControlOverlapIssues(controls, issues);
        var themeState = new UiTestThemeState(
            Application.Current.Resources["EffectiveThemeName"] as string ?? "Unknown",
            Application.Current.Resources["AnimationsEnabled"] is true);
        var dpi = VisualTreeHelper.GetDpi(this);
        var report = new UiTestLayoutReport(
            controls,
            issues,
            runtimeState,
            themeState,
            GetUiTestSurface(),
            new UiTestDpiState(dpi.DpiScaleX, dpi.DpiScaleY, dpi.PixelsPerDip));
        WriteUiTestFileAtomically(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        WriteUiTestVisualTreeArtifact();
        WriteUiTestScreenshotArtifact();
    }

    private void WriteUiTestVisualTreeArtifact()
    {
        var path = Environment.GetEnvironmentVariable("HD2MM_TEST_VISUAL_TREE_PATH");
        if (string.IsNullOrWhiteSpace(path))
            return;
        var elements = GetUiTestVisualRoots()
            .SelectMany(EnumerateVisuals)
            .OfType<FrameworkElement>()
            .Where(static element => element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0)
            .Select(element =>
            {
                var position = GetPosition(element);
                return new UiTestVisualElement(
                    element.GetType().FullName ?? element.GetType().Name,
                    AutomationProperties.GetAutomationId(element),
                    AutomationProperties.GetName(element),
                    element is TextBlock textBlock
                        ? textBlock.Text
                        : element is ContentControl { Content: string content } ? content : null,
                    position.X,
                    position.Y,
                    element.ActualWidth,
                    element.ActualHeight,
                    element.IsEnabled,
                    element.Focusable);
            })
            .ToArray();
        WriteUiTestFileAtomically(
            path,
            JsonSerializer.Serialize(elements, new JsonSerializerOptions { WriteIndented = true }));
    }

    private IEnumerable<DependencyObject> GetUiTestVisualRoots()
    {
        yield return this;
        if (_uiTestContextMenu?.IsOpen == true)
            yield return _uiTestContextMenu;
        if (_uiTestToolTip?.IsOpen == true)
            yield return _uiTestToolTip;
    }

    private void WriteUiTestScreenshotArtifact()
    {
        var path = Environment.GetEnvironmentVariable("HD2MM_TEST_SCREENSHOT_PATH");
        if (string.IsNullOrWhiteSpace(path) || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var temporaryPath = path + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            encoder.Save(stream);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private string GetUiTestSurface()
    {
        if (FindVisual<Components.MessageBox>()?.IsVisible == true)
            return "Dialog";
        if (FindVisual<Components.VersionCheckDetailOverlay>()?.IsVisible == true)
            return "VersionDetail";
        if (_uiTestContextMenu?.IsOpen == true)
            return "ContextMenu";
        if (_uiTestToolTip?.IsOpen == true)
            return "Tooltip";
        return DataContext is MainViewModel viewModel
            ? viewModel.CurrentViewModel.GetType().Name
            : "Unknown";
    }

    private T? FindVisual<T>() where T : DependencyObject =>
        EnumerateVisuals(this).OfType<T>().FirstOrDefault();

    private static void WriteUiTestFileAtomically(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private Point GetPosition(FrameworkElement element)
    {
        try
        {
            return element.TransformToAncestor(this).Transform(new Point());
        }
        catch (InvalidOperationException)
        {
            return new Point(double.NaN, double.NaN);
        }
    }

    private static void AddTextLayoutIssue(TextBlock textBlock, List<string> issues)
    {
        var dpi = VisualTreeHelper.GetDpi(textBlock);
        var formatted = new FormattedText(
            textBlock.Text,
            CultureInfo.CurrentUICulture,
            textBlock.FlowDirection,
            new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch),
            textBlock.FontSize,
            textBlock.Foreground,
            dpi.PixelsPerDip);
        const double tolerance = 2;
        if (textBlock.TextWrapping == TextWrapping.NoWrap)
        {
            if (formatted.WidthIncludingTrailingWhitespace > textBlock.ActualWidth + tolerance)
            {
                issues.Add(
                    $"Text '{Shorten(textBlock.Text)}' needs {formatted.WidthIncludingTrailingWhitespace:F1}px " +
                    $"but has {textBlock.ActualWidth:F1}px");
            }
            return;
        }

        formatted.MaxTextWidth = Math.Max(1, textBlock.ActualWidth);
        if (formatted.Height > textBlock.ActualHeight + tolerance)
        {
            issues.Add(
                $"Wrapped text '{Shorten(textBlock.Text)}' needs {formatted.Height:F1}px " +
                $"but has {textBlock.ActualHeight:F1}px");
        }
    }

    private void AddButtonLayoutIssues(Button button, List<string> issues)
    {
        const double tolerance = 4;
        var activeOverlay = FindActiveUiTestOverlay();
        if (activeOverlay is not null && !IsVisualDescendantOf(button, activeOverlay))
            return;
        var groupSidebar = FindVisual<Views.ModGroupSidebarView>();
        if (groupSidebar is not null && IsVisualDescendantOf(button, groupSidebar))
            return;
        var buttonText = button.Content as string;
        if (!string.IsNullOrWhiteSpace(buttonText))
        {
            var dpi = VisualTreeHelper.GetDpi(button);
            var formatted = new FormattedText(
                buttonText,
                CultureInfo.CurrentUICulture,
                button.FlowDirection,
                new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
                button.FontSize,
                button.Foreground,
                dpi.PixelsPerDip);
            var availableWidth = Math.Max(
                0,
                button.ActualWidth - button.Padding.Left - button.Padding.Right -
                button.BorderThickness.Left - button.BorderThickness.Right);
            if (formatted.WidthIncludingTrailingWhitespace > availableWidth + tolerance)
            {
                issues.Add(
                    $"Button '{Shorten(buttonText)}' needs {formatted.WidthIncludingTrailingWhitespace:F1}px " +
                    $"but has {availableWidth:F1}px of content width");
            }
        }

        if (HasScrollableAncestor(button))
            return;
        var position = GetPosition(button);
        if (double.IsNaN(position.X) || double.IsNaN(position.Y))
            return;
        if (position.X < -tolerance || position.Y < -tolerance ||
            position.X + button.ActualWidth > ActualWidth + tolerance ||
            position.Y + button.ActualHeight > ActualHeight + tolerance)
        {
            issues.Add($"Button '{Shorten(buttonText ?? AutomationProperties.GetName(button) ?? button.GetType().Name)}' is outside the visible window bounds");
        }
    }

    private DependencyObject? FindActiveUiTestOverlay()
    {
        var dialog = FindVisual<Components.MessageBox>();
        if (dialog?.IsVisible == true)
            return dialog;
        var versionDetail = FindVisual<Components.VersionCheckDetailOverlay>();
        return versionDetail?.IsVisible == true ? versionDetail : null;
    }

    private static bool IsVisualDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }
        return false;
    }

    private static bool HasScrollableAncestor(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer)
                return true;
        }
        return false;
    }

    private static void AddControlOverlapIssues(
        IReadOnlyList<UiTestControlReport> controls,
        List<string> issues)
    {
        for (var leftIndex = 0; leftIndex < controls.Count; leftIndex++)
        {
            var left = controls[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < controls.Count; rightIndex++)
            {
                var right = controls[rightIndex];
                var width = Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X);
                var height = Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y);
                if (width > 1 && height > 1)
                    issues.Add($"{left.AutomationId} overlaps {right.AutomationId} by {width:F1}x{height:F1}");
            }
        }
    }

    private static IEnumerable<DependencyObject> EnumerateVisuals(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var descendant in EnumerateVisuals(VisualTreeHelper.GetChild(root, index)))
                yield return descendant;
        }
    }

    private static string Shorten(string text) =>
        text.Length <= 48 ? text : text[..45] + "...";

    private sealed record UiTestControlReport(
        string AutomationId,
        string Name,
        bool IsEnabled,
        bool IsFocusable,
        double X,
        double Y,
        double Width,
        double Height);

    private sealed record UiTestLayoutReport(
        IReadOnlyList<UiTestControlReport> Controls,
        IReadOnlyList<string> Issues,
        UiTestRuntimeState? RuntimeState,
        UiTestThemeState ThemeState,
        string Surface,
        UiTestDpiState DpiState);

    private sealed record UiTestThemeState(
        string EffectiveTheme,
        bool AnimationsEnabled);

    private sealed record UiTestDpiState(
        double ScaleX,
        double ScaleY,
        double PixelsPerDip);

    private sealed record UiTestVisualElement(
        string Type,
        string AutomationId,
        string AccessibleName,
        string? Text,
        double X,
        double Y,
        double Width,
        double Height,
        bool IsEnabled,
        bool IsFocusable);

    private sealed record UiTestRuntimeState(
        string SourceLocale,
        string RequestedLocale,
        string EffectiveLocale,
        string CurrentCulture,
        string CurrentUiCulture,
        string PageType,
        bool PageInstancePreserved,
        bool BusinessServiceInstancePreserved,
        bool LayoutUpdatedBeforeNextIdle);

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateNavigationLayout();

    private void NavigationToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isNarrowNavigationOpen = !_isNarrowNavigationOpen;
        UpdateNavigationLayout();
    }

    private void UpdateNavigationLayout()
    {
        if (!IsInitialized)
            return;
        var labels = new[] { BackLabel, HomeLabel, CreateLabel, DownloadsLabel, TasksLabel, SettingsLabel, HelpLabel };
        if (ActualWidth < 900)
        {
            NavigationColumn.Width = new GridLength(0);
            Grid.SetColumn(ContentRegion, 0);
            Grid.SetColumnSpan(ContentRegion, 2);
            Grid.SetColumn(NavigationPane, 0);
            Grid.SetColumnSpan(NavigationPane, 2);
            NavigationPane.Width = 280;
            NavigationPane.Margin = new Thickness(0, 52, 0, 0);
            NavigationPane.Visibility = _isNarrowNavigationOpen ? Visibility.Visible : Visibility.Collapsed;
            NavigationToggleButton.Visibility = Visibility.Visible;
            foreach (var label in labels)
                label.Visibility = Visibility.Visible;
        }
        else if (ActualWidth < 1400)
        {
            NavigationColumn.Width = new GridLength(56);
            Grid.SetColumn(ContentRegion, 1);
            Grid.SetColumnSpan(ContentRegion, 1);
            Grid.SetColumn(NavigationPane, 0);
            Grid.SetColumnSpan(NavigationPane, 1);
            NavigationPane.Width = 56;
            NavigationPane.Margin = new Thickness(0);
            NavigationPane.Visibility = Visibility.Visible;
            NavigationToggleButton.Visibility = Visibility.Collapsed;
            foreach (var label in labels)
                label.Visibility = Visibility.Collapsed;
        }
        else
        {
            NavigationColumn.Width = new GridLength(220);
            Grid.SetColumn(ContentRegion, 1);
            Grid.SetColumnSpan(ContentRegion, 1);
            Grid.SetColumn(NavigationPane, 0);
            Grid.SetColumnSpan(NavigationPane, 1);
            NavigationPane.Width = 220;
            NavigationPane.Margin = new Thickness(0);
            NavigationPane.Visibility = Visibility.Visible;
            NavigationToggleButton.Visibility = Visibility.Collapsed;
            foreach (var label in labels)
                label.Visibility = Visibility.Visible;
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, 1, sizeof(int));
        base.OnActivated(e);
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.HelpCommand.Execute(null);
    }

    private void MinButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        else
            WindowState = WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    [LibraryImport("dwmapi.dll")]
    private static partial void DwmSetWindowAttribute(nint hwnd, uint dwAttribute, in int pvAttribute, uint cbAttribute);
}
