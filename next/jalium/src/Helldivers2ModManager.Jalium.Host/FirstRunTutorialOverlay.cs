using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class FirstRunTutorialOverlay : Grid, IDisposable
{
    private static readonly string[] TitleKeys =
    [
        "FirstRunTutorial.WelcomeTitle", "FirstRunTutorial.AddModTitle",
        "FirstRunTutorial.ManageTitle", "FirstRunTutorial.CheckTitle",
        "FirstRunTutorial.DeployTitle", "FirstRunTutorial.BackgroundTasksTitle",
        "FirstRunTutorial.ArmorReuseTitle", "FirstRunTutorial.PatchResourceViewerTitle",
        "FirstRunTutorial.BisectTitle", "FirstRunTutorial.TagManagementTitle",
        "FirstRunTutorial.SettingsTitle", "FirstRunTutorial.FinishTitle",
    ];
    private static readonly string[] DescriptionKeys =
    [
        "FirstRunTutorial.WelcomeDescription", "FirstRunTutorial.AddModDescription",
        "FirstRunTutorial.ManageDescription", "FirstRunTutorial.CheckDescription",
        "FirstRunTutorial.DeployDescription", "FirstRunTutorial.BackgroundTasksDescription",
        "FirstRunTutorial.ArmorReuseDescription", "FirstRunTutorial.PatchResourceViewerDescription",
        "FirstRunTutorial.BisectDescription", "FirstRunTutorial.TagManagementDescription",
        "FirstRunTutorial.SettingsDescription", "FirstRunTutorial.FinishDescription",
    ];
    private static readonly string[] TargetNames =
    [
        "", "AddCreatePanel", "TopActionBar", "VersionCheckPanel",
        "BottomActionButtons", "BackgroundTasksButton", "ArmorReuseButton",
        "PatchResourceViewerButton", "BisectButton", "TagManagementButton",
        "SettingsButton", "",
    ];
    private static readonly string[] Glyphs =
    [
        "\uE8F1", "\uE710", "\uE721", "\uE9D9", "\uE896", "\uE9F5",
        "\uE7BA", "\uE9D9", "\uE9E9", "\uE8EC", "\uE713", "\uE73E",
    ];

    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
    private static readonly Brush Surface = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly Brush Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
    private const double Gap = 16;

    private readonly LocalizationService _localization;
    private readonly Func<string, FrameworkElement?> _findTarget;
    private readonly Func<Task> _complete;
    private readonly Action<Exception> _reportError;
    private readonly Canvas _canvas = new();
    private readonly Border[] _dimRegions = new Border[4];
    private readonly Border _highlight = new();
    private readonly Border _card = new();
    private readonly TextBlock _icon = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();
    private readonly TextBlock _position = new();
    private readonly Button _skip = new();
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private readonly Button _finish = new();
    private int _step;
    private bool _closing;

    internal int Step => _step;
    internal int StepCount => TitleKeys.Length;

    public FirstRunTutorialOverlay(LocalizationService localization,
        Func<string, FrameworkElement?> findTarget, Func<Task> complete,
        Action<Exception> reportError)
    {
        _localization = localization;
        _findTarget = findTarget;
        _complete = complete;
        _reportError = reportError;
        Visibility = Visibility.Collapsed;
        Focusable = true;
        Background = Brushes.Transparent;
        Children.Add(_canvas);
        for (var index = 0; index < _dimRegions.Length; index++)
        {
            var region = new Border { Background = Dim, IsHitTestVisible = false };
            _dimRegions[index] = region;
            _canvas.Children.Add(region);
        }
        _highlight.BorderBrush = Accent;
        _highlight.BorderThickness = new Thickness(2);
        _highlight.CornerRadius = new CornerRadius(8);
        _highlight.IsHitTestVisible = false;
        _highlight.Visibility = Visibility.Collapsed;
        _canvas.Children.Add(_highlight);

        _card.Background = Surface;
        _card.BorderBrush = Accent;
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = new CornerRadius(8);
        _card.Padding = new Thickness(20);
        _card.Width = 440;
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconTile = new Border { Width = 56, Height = 56, Background = Accent,
            CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 16, 0),
            Child = _icon };
        _icon.FontFamily = new FontFamily("Segoe Fluent Icons");
        _icon.FontSize = 28;
        _icon.Foreground = Foreground;
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRowSpan(iconTile, 2);
        content.Children.Add(iconTile);
        _title.FontSize = 20;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Foreground;
        _title.TextWrapping = TextWrapping.Wrap;
        _title.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetColumn(_title, 1);
        content.Children.Add(_title);
        _description.FontSize = 14;
        _description.Foreground = Secondary;
        _description.TextWrapping = TextWrapping.Wrap;
        var descriptionScroll = new ScrollViewer { Content = _description,
            MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 20) };
        Grid.SetRow(descriptionScroll, 1);
        Grid.SetColumn(descriptionScroll, 1);
        content.Children.Add(descriptionScroll);
        _position.FontSize = 12;
        _position.Foreground = Secondary;
        _position.HorizontalAlignment = HorizontalAlignment.Center;
        _position.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_position, 2);
        Grid.SetColumnSpan(_position, 2);
        content.Children.Add(_position);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _skip, _previous, _next, _finish })
        {
            button.MinWidth = 86;
            button.Height = 36;
            button.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(button);
        }
        _skip.Click += (_, _) => OnCompleteClick();
        _previous.Click += (_, _) => Move(-1);
        _next.Click += (_, _) => Move(1);
        _finish.Click += (_, _) => OnCompleteClick();
        Grid.SetRow(actions, 3);
        Grid.SetColumnSpan(actions, 2);
        content.Children.Add(actions);
        _card.Child = content;
        _canvas.Children.Add(_card);

        SizeChanged += (_, _) => UpdateSpotlight();
        KeyDown += OnKeyDown;
        _localization.PropertyChanged += OnLocalizationChanged;
        Refresh();
    }

    public void Start()
    {
        _step = 0;
        Visibility = Visibility.Visible;
        Refresh();
        Focus();
    }

    internal void Move(int delta)
    {
        _step = Math.Clamp(_step + delta, 0, StepCount - 1);
        Refresh();
    }

    private void Refresh()
    {
        _icon.Text = Glyphs[_step];
        _title.Text = _localization[TitleKeys[_step]];
        _description.Text = _localization[DescriptionKeys[_step]];
        _position.Text = $"{_step + 1}/{StepCount}";
        _skip.Content = _localization["FirstRunTutorial.Skip"];
        _previous.Content = _localization["FirstRunTutorial.Previous"];
        _next.Content = _localization["FirstRunTutorial.Next"];
        _finish.Content = _localization["FirstRunTutorial.Finish"];
        _previous.IsEnabled = _step > 0;
        _next.Visibility = _step == StepCount - 1 ? Visibility.Collapsed : Visibility.Visible;
        _finish.Visibility = _step == StepCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSpotlight();
    }

    private void UpdateSpotlight()
    {
        if (Visibility != Visibility.Visible || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var target = _findTarget(TargetNames[_step]);
        (double X, double Y, double Width, double Height)? bounds = null;
        if (target is { Visibility: Visibility.Visible, ActualWidth: > 0, ActualHeight: > 0 })
        {
            try
            {
                var transform = target.TransformToVisual(this);
                if (transform is not null)
                {
                    var origin = transform.Transform(new Point(0, 0));
                    bounds = (Math.Max(0, origin.X), Math.Max(0, origin.Y),
                        Math.Min(target.ActualWidth, ActualWidth),
                        Math.Min(target.ActualHeight, ActualHeight));
                }
            }
            catch (InvalidOperationException) { }
        }
        if (bounds is { } area)
        {
            SetRegion(_dimRegions[0], 0, 0, ActualWidth, area.Y);
            SetRegion(_dimRegions[1], 0, area.Y, area.X, area.Height);
            SetRegion(_dimRegions[2], area.X + area.Width, area.Y,
                ActualWidth - area.X - area.Width, area.Height);
            SetRegion(_dimRegions[3], 0, area.Y + area.Height,
                ActualWidth, ActualHeight - area.Y - area.Height);
            SetRegion(_highlight, area.X - 3, area.Y - 3,
                area.Width + 6, area.Height + 6);
            _highlight.Visibility = Visibility.Visible;
        }
        else
        {
            SetRegion(_dimRegions[0], 0, 0, ActualWidth, ActualHeight);
            for (var index = 1; index < _dimRegions.Length; index++)
                SetRegion(_dimRegions[index], 0, 0, 0, 0);
            _highlight.Visibility = Visibility.Collapsed;
        }
        _card.Width = Math.Min(440, Math.Max(280, ActualWidth - 32));
        _card.Measure(new Size(_card.Width, Math.Max(1, ActualHeight - 32)));
        var cardHeight = Math.Min(_card.DesiredSize.Height, Math.Max(1, ActualHeight - 32));
        var cardWidth = _card.Width;
        var x = (ActualWidth - cardWidth) / 2;
        var y = (ActualHeight - cardHeight) / 2;
        if (bounds is { } spotlight)
        {
            var right = spotlight.X + spotlight.Width + Gap;
            var left = spotlight.X - cardWidth - Gap;
            var below = spotlight.Y + spotlight.Height + Gap;
            var above = spotlight.Y - cardHeight - Gap;
            if (right + cardWidth + Gap <= ActualWidth)
            {
                x = right;
                y = spotlight.Y + (spotlight.Height - cardHeight) / 2;
            }
            else if (left >= Gap)
            {
                x = left;
                y = spotlight.Y + (spotlight.Height - cardHeight) / 2;
            }
            else if (below + cardHeight + Gap <= ActualHeight)
            {
                x = spotlight.X + (spotlight.Width - cardWidth) / 2;
                y = below;
            }
            else if (above >= Gap)
            {
                x = spotlight.X + (spotlight.Width - cardWidth) / 2;
                y = above;
            }
        }
        Canvas.SetLeft(_card, Math.Clamp(x, Gap, Math.Max(Gap, ActualWidth - cardWidth - Gap)));
        Canvas.SetTop(_card, Math.Clamp(y, Gap, Math.Max(Gap, ActualHeight - cardHeight - Gap)));
    }

    private static void SetRegion(FrameworkElement element, double x, double y,
        double width, double height)
    {
        Canvas.SetLeft(element, Math.Max(0, x));
        Canvas.SetTop(element, Math.Max(0, y));
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    internal async Task CompleteAsync()
    {
        if (_closing || Visibility != Visibility.Visible)
            return;
        _closing = true;
        Visibility = Visibility.Collapsed;
        try { await _complete(); }
        catch (Exception ex) { _reportError(ex); }
        finally { _closing = false; }
    }

    private async void OnCompleteClick() => await CompleteAsync();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCompleteClick();
        }
    }

    private void OnLocalizationChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    public void Dispose()
    {
        _localization.PropertyChanged -= OnLocalizationChanged;
        KeyDown -= OnKeyDown;
    }
}
