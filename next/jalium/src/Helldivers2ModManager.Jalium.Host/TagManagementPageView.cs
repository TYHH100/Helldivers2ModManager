using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class TagManagementPageView : Grid, IDisposable
{
    private readonly TagManagementEditor _editor;
    private readonly LocalizationService _localization;
    private readonly MessageBoxOverlay _messageBoxOverlay;
    private readonly Action _back;
    private readonly Action _tagsChanged;
    private readonly Action<Exception> _reportError;
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly StackPanel _rows;
    private readonly Button _create;
    private readonly Button _backButton;

    public TagManagementPageView(TagManagementEditor editor, LocalizationService localization,
        MessageBoxOverlay messageBoxOverlay, Action back, Action tagsChanged, Action<Exception> reportError)
    {
        _editor = editor;
        _localization = localization;
        _messageBoxOverlay = messageBoxOverlay;
        _back = back;
        _tagsChanged = tagsChanged;
        _reportError = reportError;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _title = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Paint(0xFF, 0xFF, 0xFF),
            Margin = new Thickness(0, 0, 0, 16),
        };
        Children.Add(_title);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new Border
        {
            Background = Paint(0x2E, 0x2E, 0x2E),
            BorderBrush = Paint(0x3A, 0x3A, 0x3A),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 16),
            Child = content,
        };
        Grid.SetRow(panel, 1);
        Children.Add(panel);

        _create = new Button { Height = 38, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8) };
        _create.Click += (_, _) => RunAsync(async () =>
        {
            var name = await PromptAsync(_localization["TagManagementPage.CreateTitle"],
                _localization["TagManagementPage.CreateMsg"], string.Empty, TagManagementEditor.MaxNameLength);
            if (name is null) return;
            await _editor.CreateAsync(name);
            Updated("TagManagementPage.CreateSuccess");
        });
        var toolbar = new Border
        {
            Background = Paint(0x38, 0x38, 0x38),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = _create,
        };
        content.Children.Add(toolbar);

        _rows = new StackPanel();
        var list = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(list, 1);
        content.Children.Add(list);

        _status = new TextBlock
        {
            Foreground = Paint(0xB3, 0xB3, 0xB3),
            FontSize = 13,
            Margin = new Thickness(4, 8, 0, 0),
        };
        Grid.SetRow(_status, 2);
        content.Children.Add(_status);

        _backButton = new Button { Height = 38, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8) };
        _backButton.Click += (_, _) => _back();
        var footer = new Border { Child = _backButton };
        Grid.SetRow(footer, 2);
        Children.Add(footer);

        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshTexts();
    }

    private void RefreshTexts()
    {
        _title.Text = _localization["DashboardPage.TagManagement"];
        _create.Content = IconText("\uE710", _localization["TagManagementPage.CreateTitle"]);
        _backButton.Content = IconText("\uE72B", _localization["Common.Back"]);
        RenderTags();
    }

    private static StackPanel IconText(string icon, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = icon,
            FontFamily = new FontFamily("Segoe Fluent Icons"), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private void RenderTags()
    {
        _rows.Children.Clear();
        foreach (var tag in _editor.Tags)
        {
            var row = new Grid { Margin = new Thickness(2, 2, 2, 2), MinHeight = 56 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var swatch = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(4),
                Background = ParseColor(tag.Color),
                Margin = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(swatch);
            var name = new TextBlock
            {
                Text = tag.Name, FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = Paint(0xFF, 0xFF, 0xFF), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var actions = new StackPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center };
            AddAction(actions, "\uE790", "DashboardPage.ChangeColor", () => RunAsync(async () =>
            {
                var color = await PromptColorAsync(tag);
                if (color is null) return;
                await _editor.SetColorAsync(tag, color);
                Updated("TagManagementPage.ColorUpdated");
            }));
            AddAction(actions, "\uE8AC", "TagManagementPage.RenameTitle", () => RunAsync(async () =>
            {
                var text = await PromptAsync(_localization["TagManagementPage.RenameTitle"],
                    _localization["TagManagementPage.RenameMsg"], tag.Name, TagManagementEditor.MaxNameLength);
                if (text is null) return;
                await _editor.RenameAsync(tag, text);
                Updated("TagManagementPage.RenameSuccess");
            }));
            AddAction(actions, "\uE74D", "TagManagementPage.DeleteTag", () => RunAsync(async () =>
            {
                if (!await ConfirmAsync(_localization["DashboardPage.DeleteConfirmTitle"],
                        _localization["TagManagementPage.DeletePrefix"] + tag.Name
                        + _localization["TagManagementPage.DeleteSuffix"])) return;
                await _editor.DeleteAsync(tag);
                Updated("TagManagementPage.DeleteSuccess");
            }));
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            _rows.Children.Add(new Border
            {
                Child = row,
                BorderBrush = Paint(0x3A, 0x3A, 0x3A),
                BorderThickness = new Thickness(0, 0, 0, 1),
            });
        }
    }

    private void AddAction(StackPanel panel, string icon, string tooltip, Action action)
    {
        var button = new Button
        {
            Width = 36, Height = 36, Margin = new Thickness(0, 0, 8, 0),
            ToolTip = _localization[tooltip],
            Content = new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 16 },
        };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private Task<string?> PromptAsync(string title, string message, string initial, int maxLength) =>
        _messageBoxOverlay.PromptAsync(title, message, initial, maxLength,
            value => string.IsNullOrWhiteSpace(value)
                ? _localization["TagManagementPage.CreateEmptyError"] : null);

    private Task<string?> PromptColorAsync(ModTag tag) =>
        _messageBoxOverlay.PromptAsync(_localization["TagManagementPage.ColorTitle"],
            _localization["TagManagementPage.ColorPrefix"] + tag.Name
                + _localization["TagManagementPage.ColorSuffix"], tag.Color, 9,
            value => IsColorCode(value) ? null : _localization["JaliumMigration.InvalidTagColor"]);

    private Task<bool> ConfirmAsync(string title, string message) =>
        _messageBoxOverlay.ConfirmAsync(title, message);

    private void Updated(string messageKey)
    {
        _status.Text = _localization[messageKey];
        RenderTags();
        _tagsChanged();
    }

    private async void RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { _reportError(error); }
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => RefreshTexts();

    public void Dispose() => _localization.PropertyChanged -= OnLocalizationChanged;

    private static bool IsColorCode(string? value) => value is { Length: 9 } && value[0] == '#'
        && uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static Brush ParseColor(string value)
    {
        if (!IsColorCode(value)) return Paint(0x3B, 0x82, 0xF6);
        var number = Convert.ToUInt32(value[1..], 16);
        return new SolidColorBrush(Color.FromArgb((byte)(number >> 24), (byte)(number >> 16),
            (byte)(number >> 8), (byte)number));
    }

    private static Brush Paint(byte red, byte green, byte blue)
        => new SolidColorBrush(Color.FromRgb(red, green, blue));
}
