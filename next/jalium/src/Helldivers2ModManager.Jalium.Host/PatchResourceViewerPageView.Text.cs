using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class PatchResourceViewerPageView
{
    private readonly TextBox _textFilter = new();
    private readonly CheckBox _textModifiedOnly = new();
    private readonly TextBlock _textCount = new();
    private readonly TextBlock _textModifiedCount = new();
    private readonly ListBox _textEntries = new();
    private TextInventoryResult _textInventory = TextInventoryResult.Empty;
    private TextRow[] _textRows = [];

    private Grid BuildTextTab()
    {
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var controls = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _textFilter.Height = 30;
        _textFilter.Margin = new Thickness(0, 0, 10, 0);
        _textFilter.TextChanged += (_, _) => ApplyTextFilter();
        controls.Children.Add(_textFilter);
        _textCount.FontSize = 11;
        _textCount.Foreground = Secondary;
        _textCount.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_textCount, 1);
        controls.Children.Add(_textCount);
        _textModifiedCount.FontSize = 11;
        _textModifiedCount.Foreground = Secondary;
        _textModifiedCount.Margin = new Thickness(0, 0, 8, 0);
        _textModifiedCount.VerticalAlignment = VerticalAlignment.Center;
        var comparison = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
        comparison.Children.Add(_textModifiedCount);
        _textModifiedOnly.Click += (_, _) => ApplyTextFilter();
        comparison.Children.Add(_textModifiedOnly);
        Grid.SetRow(comparison, 1);
        Grid.SetColumnSpan(comparison, 3);
        controls.Children.Add(comparison);
        root.Children.Add(controls);

        ConfigureGroupedList(_textEntries);
        _textEntries.ItemTemplate = CreateTextEntryTemplate();
        _textEntries.SelectionChanged += (_, _) =>
        {
            if (_textEntries.SelectedItem is GroupedListRow<TextRow> { Entry: null })
                _textEntries.SelectedItem = null;
        };
        Grid.SetRow(_textEntries, 1);
        root.Children.Add(_textEntries);
        return root;
    }

    private static DataTemplate CreateTextEntryTemplate()
    {
        var template = new DataTemplate();
        template.SetVisualTree(() =>
        {
            var container = new Grid();
            var header = CreateGroupHeader();
            header.SetBinding(VisibilityProperty, "HeaderVisibility");
            container.Children.Add(header);
            var row = new Grid { Margin = new Thickness(6, 3, 6, 3) };
            row.SetBinding(VisibilityProperty, "EntryVisibility");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var id = new TextBlock { FontSize = 11, Foreground = Secondary,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 10, 0) };
            id.SetBinding(TextBlock.TextProperty, "Entry.Id");
            row.Children.Add(id);
            var state = new TextBlock { FontSize = 11, Foreground = Foreground,
                Margin = new Thickness(0, 0, 10, 0) };
            state.SetBinding(TextBlock.TextProperty, "Entry.State");
            Grid.SetColumn(state, 1);
            row.Children.Add(state);
            var content = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center };
            content.SetBinding(TextBlock.TextProperty, "Entry.Content");
            content.SetBinding(TextBlock.ToolTipProperty, "Entry.OriginalTooltip");
            Grid.SetColumn(content, 2);
            row.Children.Add(content);
            container.Children.Add(row);
            return container;
        });
        return template;
    }

    private void ClearText()
    {
        _textInventory = TextInventoryResult.Empty;
        _textRows = [];
        _textEntries.ItemsSource = null;
        _textModifiedOnly.IsChecked = false;
        _tabs[4].Visibility = Visibility.Collapsed;
        if (_previewTabs.SelectedIndex == 4)
            _previewTabs.SelectedIndex = 0;
    }

    private void ApplyText(TextInventoryResult inventory, PatchResourceInspectionResult resources)
    {
        _textInventory = inventory;
        _textRows = inventory.Groups.SelectMany(group => group.Entries)
            .Select(entry => new TextRow(entry, CreateTextGroupKey(entry),
                entry.PatchRelativePath,
                $"{entry.TextBankFileId:X16}",
                TextBankFormat.GetLanguageName(entry.Language),
                entry.StringId.ToString(),
                entry.MatchesOriginal == false
                    ? _localization[entry.IsNewEntry ? "ModelPreviewPage.TextNewTag" : "ModelPreviewPage.TextModifiedTag"]
                    : entry.MatchesOriginal == true ? _localization["ModelPreviewPage.TextOriginalTag"] : string.Empty,
                entry.Text, entry.OriginalText, entry.MatchesOriginal,
                CreateTextTooltip(entry)))
            .ToArray();
        _tabs[4].Visibility = _textRows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var comparable = _textRows.Any(row => row.MatchesOriginal is not null);
        _textModifiedOnly.Visibility = comparable ? Visibility.Visible : Visibility.Collapsed;
        _textModifiedCount.Visibility = _textModifiedOnly.Visibility;
        RefreshTextTexts();
        BindTextRows();
        if (!string.IsNullOrWhiteSpace(inventory.Error))
            _status.Text += " " + _localization["ModelPreviewPage.TextLoadFailed"]
                .Replace("{message}", inventory.Error);
        if (_textRows.Length > 0 && _audioRows.Length == 0
            && !resources.TocEntries.Any(entry => entry.TypeId == PatchResourceTypeIds.Unit))
            _previewTabs.SelectedIndex = 4;
    }

    private void ApplyTextFilter()
    {
        BindTextRows();
    }

    private TextGroupKey CreateTextGroupKey(TextEntry entry) => new(
        _localization["ModelPreviewPage.TextBankGroup"]
            .Replace("{id}", $"0x{entry.TextBankFileId:X16}")
            .Replace("{language}", TextBankFormat.GetLanguageName(entry.Language)),
        entry.PatchRelativePath);

    private string CreateTextTooltip(TextEntry entry) =>
        entry.OriginalText is { } original && entry.MatchesOriginal == false
            ? _localization["ModelPreviewPage.TextOriginalTooltip"]
                .Replace("{text}", original) + "\n" + entry.Text
            : entry.Text;

    private void BindTextRows()
    {
        _textEntries.ItemsSource = _textRows.Where(FilterTextRow)
            .GroupBy(row => row.GroupKey)
            .SelectMany(group => new[] { new GroupedListRow<TextRow>(null,
                    group.Key.Header, group.Key.PatchPath) }
                .Concat(group.Select(row => new GroupedListRow<TextRow>(row,
                    group.Key.Header, group.Key.PatchPath))))
            .ToArray();
    }

    private bool FilterTextRow(TextRow row)
    {
        var query = _textFilter.Text.Trim();
        return (_textModifiedOnly.IsChecked != true || row.MatchesOriginal == false)
            && (query.Length == 0 || row.Content.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Original?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true
                || row.Patch.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RefreshTextRowsLocalization()
    {
        if (_textRows.Length == 0)
            return;
        _textRows = _textRows.Select(row => row with
        {
            GroupKey = CreateTextGroupKey(row.Model),
            OriginalTooltip = CreateTextTooltip(row.Model),
            State = row.Model.MatchesOriginal == false
                ? _localization[row.Model.IsNewEntry ? "ModelPreviewPage.TextNewTag" : "ModelPreviewPage.TextModifiedTag"]
                : row.Model.MatchesOriginal == true ? _localization["ModelPreviewPage.TextOriginalTag"] : string.Empty,
        }).ToArray();
        BindTextRows();
    }

    private void RefreshTextTexts()
    {
        _textFilter.ToolTip = _localization["ModelPreviewPage.TextFilterTip"];
        _textModifiedOnly.Content = _localization["ModelPreviewPage.TextOnlyShowModified"];
        _textModifiedOnly.ToolTip = _localization["ModelPreviewPage.TextOnlyShowModifiedTip"];
        _textCount.Text = _localization["ModelPreviewPage.TextEntryCount"]
            .Replace("{count}", _textRows.Length.ToString("N0"))
            .Replace("{banks}", _textInventory.Groups.Count.ToString("N0"));
        _textModifiedCount.Text = _localization["ModelPreviewPage.TextModifiedCount"]
            .Replace("{modified}", _textRows.Count(row => row.MatchesOriginal == false).ToString("N0"))
            .Replace("{total}", _textRows.Length.ToString("N0"));
    }

    private sealed record TextGroupKey(string Header, string PatchPath);

    private sealed record TextRow(TextEntry Model, TextGroupKey GroupKey, string Patch, string Bank,
        string Language, string Id, string State, string Content, string? Original,
        bool? MatchesOriginal, string OriginalTooltip);
}
