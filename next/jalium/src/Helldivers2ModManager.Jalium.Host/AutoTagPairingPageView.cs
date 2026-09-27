using System.ComponentModel;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class AutoTagPairingPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
    private static readonly Brush Surface = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
    private static readonly Brush Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private readonly AutoTagPairingEditor _editor;
    private readonly LocalizationService _localization;
    private readonly MessageBoxOverlay _messageBoxOverlay;
    private readonly Action _back;
    private readonly Action<Exception> _reportError;
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();
    private readonly StackPanel _rows = new();
    private readonly Button _backButton = new();
    private readonly Button _saveButton = new();
    private bool _busy;

    public AutoTagPairingPageView(AutoTagPairingEditor editor, LocalizationService localization,
        MessageBoxOverlay messageBoxOverlay, Action back, Action<Exception> reportError)
    {
        _editor = editor;
        _localization = localization;
        _messageBoxOverlay = messageBoxOverlay;
        _back = back;
        _reportError = reportError;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _title.FontSize = 28;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Foreground;
        _title.Margin = new Thickness(0, 0, 0, 16);
        Children.Add(_title);

        var content = new StackPanel();
        _description.Foreground = Secondary;
        _description.TextWrapping = TextWrapping.Wrap;
        _description.Margin = new Thickness(12, 12, 12, 4);
        content.Children.Add(_description);
        content.Children.Add(_rows);
        var scroll = new ScrollViewer { Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new Border { Child = scroll, Background = Surface,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(body, 1);
        Children.Add(body);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _backButton.Height = 38;
        _backButton.MinWidth = 96;
        _backButton.Padding = new Thickness(16, 8, 16, 8);
        _backButton.Margin = new Thickness(0, 0, 8, 0);
        _backButton.Click += (_, _) => { if (!_busy) _back(); };
        actions.Children.Add(_backButton);
        _saveButton.Height = 38;
        _saveButton.MinWidth = 96;
        _saveButton.Padding = new Thickness(16, 8, 16, 8);
        _saveButton.Click += (_, _) => RunAsync(async () =>
        {
            await _editor.SaveAsync();
            _messageBoxOverlay.ShowInfo(_localization["AutoTagPairingPage.Title"],
                _localization["AutoTagPairingPage.SaveSuccess"]);
        });
        actions.Children.Add(_saveButton);
        var footer = new Border { Child = actions, Background = Surface,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8) };
        Grid.SetRow(footer, 2);
        Children.Add(footer);

        _localization.PropertyChanged += OnLocalizationChanged;
        Refresh();
    }

    private void Refresh()
    {
        _title.Text = _localization["AutoTagPairingPage.Title"];
        _description.Text = _localization["AutoTagPairingPage.Desc"];
        _backButton.Content = IconLabel("\uE72B", _localization["Common.Back"]);
        _saveButton.Content = IconLabel("\uE74E", _localization["AutoTagPairingPage.Save"]);
        _rows.Children.Clear();

        foreach (var row in _editor.Rows)
        {
            var line = new Grid { Margin = new Thickness(12, 6, 12, 6) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(new TextBlock
            {
                Text = _localization[row.NameKey], Foreground = Foreground,
                FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            });
            var choices = new List<TagChoice>
            {
                new(null, _localization["AutoTagPairingPage.UnsetOption"]),
            };
            choices.AddRange(_editor.Tags.Select(tag => new TagChoice(tag.Id, tag.Name)));
            choices.Add(new TagChoice(null, _localization["AutoTagPairingPage.CreateNewOption"], true));
            var combo = new ComboBox
            {
                ItemsSource = choices,
                SelectedItem = choices.FirstOrDefault(choice => !choice.IsCreateNew
                    && choice.TagId == row.SelectedTagId) ?? choices[0],
                MaxDropDownHeight = 360,
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not TagChoice choice)
                    return;
                if (choice.IsCreateNew)
                    RunAsync(async () =>
                    {
                        var name = await PromptTagNameAsync();
                        if (name is not null)
                            await _editor.CreateTagAsync(row, name);
                        Refresh();
                    });
                else
                    _editor.Select(row, choice.TagId);
            };
            Grid.SetColumn(combo, 1);
            line.Children.Add(combo);
            _rows.Children.Add(line);
        }
    }

    private Task<string?> PromptTagNameAsync() =>
        _messageBoxOverlay.PromptAsync(_localization["TagManagementPage.CreateTitle"],
            _localization["TagManagementPage.CreateMsg"], string.Empty,
            TagManagementEditor.MaxNameLength,
            value => string.IsNullOrWhiteSpace(value)
                ? _localization["TagManagementPage.CreateEmptyError"] : null);

    private async void RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _saveButton.IsEnabled = false;
        _backButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { _reportError(ex); Refresh(); }
        finally
        {
            _busy = false;
            _saveButton.IsEnabled = true;
            _backButton.IsEnabled = true;
        }
    }

    private static StackPanel IconLabel(string glyph, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    public void Dispose() => _localization.PropertyChanged -= OnLocalizationChanged;

    private sealed record TagChoice(Guid? TagId, string Display, bool IsCreateNew = false)
    {
        public override string ToString() => Display;
    }
}
