using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services.Parsing;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class PatchResourceViewerPageView
{
    private readonly TextBlock _luaCount = new();
    private readonly TextBlock _luaNotice = new();
    private readonly TextBlock _luaMessage = new();
    private readonly TextBox _luaReport = new();
    private readonly Button _luaExtractButton = new();
    private readonly Button _luaCopyAllButton = new();
    private readonly Button _luaCopyButton = new();

    private Grid BuildLuaTab()
    {
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _luaCount.FontSize = 11;
        _luaCount.Foreground = Secondary;
        _luaCount.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_luaCount);
        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _luaExtractButton, _luaCopyAllButton, _luaCopyButton })
        {
            button.Height = 30;
            button.Padding = new Thickness(10, 4, 10, 4);
            button.Margin = new Thickness(0, 0, 6, 0);
            commands.Children.Add(button);
        }
        _luaExtractButton.Click += (_, _) => _ = ExtractLuaAsync();
        _luaCopyAllButton.Click += (_, _) => CopyLua(all: true);
        _luaCopyButton.Click += (_, _) => CopyLua(all: false);
        Grid.SetColumn(commands, 1);
        top.Children.Add(commands);
        root.Children.Add(top);

        _luaMessage.FontSize = 11;
        _luaMessage.Foreground = Foreground;
        _luaMessage.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_luaMessage, 1);
        root.Children.Add(_luaMessage);
        _luaNotice.FontSize = 11;
        _luaNotice.Foreground = Secondary;
        _luaNotice.TextWrapping = TextWrapping.Wrap;
        _luaNotice.Margin = new Thickness(0, 0, 0, 5);
        Grid.SetRow(_luaNotice, 2);
        root.Children.Add(_luaNotice);

        var template = new DataTemplate();
        template.SetVisualTree(() =>
        {
            var title = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 3, 6, 3) };
            title.SetBinding(TextBlock.TextProperty, "Title");
            return title;
        });
        _luaEntries.ItemTemplate = template;
        _luaEntries.SelectionChanged += (_, _) =>
            _luaReport.Text = (_luaEntries.SelectedItem as LuaScriptEntry)?.Report ?? string.Empty;
        _luaEntries.Margin = new Thickness(0, 0, 0, 5);
        Grid.SetRow(_luaEntries, 3);
        root.Children.Add(_luaEntries);

        _luaReport.IsReadOnly = true;
        _luaReport.AcceptsReturn = true;
        _luaReport.FontFamily = new FontFamily("Consolas");
        _luaReport.FontSize = 12;
        _luaReport.TextWrapping = TextWrapping.NoWrap;
        _luaReport.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _luaReport.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Grid.SetRow(_luaReport, 4);
        root.Children.Add(_luaReport);
        return root;
    }

    private void ClearLua()
    {
        _luaInventory = LuaScriptInventoryResult.Empty;
        _luaEntries.ItemsSource = null;
        _luaReport.Text = string.Empty;
        _luaMessage.Text = string.Empty;
        _luaExtractButton.IsEnabled = false;
        _tabs[5].Visibility = Visibility.Collapsed;
        if (_previewTabs.SelectedIndex == 5)
            _previewTabs.SelectedIndex = 0;
    }

    private void ApplyLua(LuaScriptInventoryResult inventory, PatchResourceInspectionResult resources)
    {
        _luaInventory = inventory;
        var entries = inventory.Groups.SelectMany(group => group.Entries).ToArray();
        _luaEntries.ItemsSource = entries;
        _luaEntries.SelectedItem = entries.FirstOrDefault();
        _tabs[5].Visibility = entries.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _luaExtractButton.IsEnabled = entries.Length > 0;
        _luaCount.Text = _localization["ModelPreviewPage.LuaEntryCount"]
            .Replace("{count}", entries.Length.ToString("N0"))
            .Replace("{patches}", inventory.PatchCount.ToString("N0"));
        if (!string.IsNullOrWhiteSpace(inventory.Error))
        {
            _luaMessage.Text = _localization["ModelPreviewPage.LuaLoadFailed"]
                .Replace("{message}", inventory.Error);
            _status.Text += " " + _luaMessage.Text;
        }
        if (entries.Length > 0 && _audioRows.Length == 0 && _textRows.Length == 0
            && !resources.TocEntries.Any(entry => entry.TypeId == PatchResourceTypeIds.Unit))
            _previewTabs.SelectedIndex = 5;
    }

    private void RefreshLuaTexts()
    {
        _luaNotice.Text = _localization["ModelPreviewPage.LuaStaticNotice"];
        _luaExtractButton.Content = _localization["ModelPreviewPage.LuaExtract"];
        _luaExtractButton.ToolTip = _localization["ModelPreviewPage.LuaExtractTip"];
        _luaCopyAllButton.Content = _localization["ModelPreviewPage.LuaCopyAll"];
        _luaCopyAllButton.ToolTip = _localization["ModelPreviewPage.LuaCopyAllTip"];
        _luaCopyButton.Content = _localization["ModelPreviewPage.LuaCopyCurrent"];
        _luaCopyButton.ToolTip = _localization["ModelPreviewPage.LuaCopyCurrentTip"];
        _luaCount.Text = _localization["ModelPreviewPage.LuaEntryCount"]
            .Replace("{count}", _luaInventory.Groups.Sum(group => group.Entries.Count).ToString("N0"))
            .Replace("{patches}", _luaInventory.PatchCount.ToString("N0"));
    }

    private void CopyLua(bool all)
    {
        var entries = _luaInventory.Groups.SelectMany(group => group.Entries).ToArray();
        var report = all
            ? string.Join(Environment.NewLine + Environment.NewLine, entries.Select(entry => entry.Report))
            : (_luaEntries.SelectedItem as LuaScriptEntry)?.Report;
        if (string.IsNullOrEmpty(report))
            return;
        try
        {
            NativeClipboard.SetText(report);
            _luaMessage.Text = _localization["ModelPreviewPage.LuaCopied"];
        }
        catch (Exception ex)
        {
            _luaMessage.Text = _localization["ModelPreviewPage.LuaCopyFailed"]
                .Replace("{message}", ex.Message);
        }
    }

    private async Task ExtractLuaAsync()
    {
        var mod = _selectedMod;
        if (_disposed || mod is null || _luaExtractButton.IsEnabled == false)
            return;
        var dialog = new FolderPickerDialog(_localization["ModelPreviewPage.LuaExtractDialogTitle"],
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), _localization,
            Window.GetWindow(this)!);
        dialog.ShowDialog();
        if (!dialog.Accepted || dialog.SelectedPath is null)
            return;
        var generation = _generation;
        _luaExtractButton.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        _luaExtractionCancellation = cancellation;
        try
        {
            var result = await _extractLua(mod, dialog.SelectedPath, cancellation.Token);
            if (_disposed || generation != _generation || !ReferenceEquals(mod, _selectedMod))
                return;
            _luaMessage.Text = result.Error is not null
                ? _localization["ModelPreviewPage.LuaExtractFailed"].Replace("{message}", result.Error)
                : result.FileCount == 0
                    ? _localization["ModelPreviewPage.LuaExtractEmpty"]
                    : _localization["ModelPreviewPage.LuaExtracted"]
                        .Replace("{count}", result.FileCount.ToString("N0"))
                        .Replace("{path}", result.DestinationDirectory);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && generation == _generation)
                _luaMessage.Text = _localization["ModelPreviewPage.LuaExtractFailed"]
                    .Replace("{message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_luaExtractionCancellation, cancellation))
                _luaExtractionCancellation = null;
            if (!_disposed && generation == _generation)
                _luaExtractButton.IsEnabled = true;
        }
    }
}
