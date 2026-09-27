using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class SettingsPageView
{
    private StackPanel BuildMods()
    {
        var page = new StackPanel();
        var removal = Card("SettingsPage.DeleteMethod", "SettingsPage.MoveToRecycleBin");
        removal.Children.Add(Toggle("SettingsPage.MoveToRecycleBin",
            () => _editor.Settings.DeleteToRecycleBin,
            value => _editor.Settings.DeleteToRecycleBin = value));
        page.Children.Add(WrapCard(removal));

        var missing = Card("SettingsPage.MissingMods", "SettingsPage.MissingModsDesc");
        missing.Children.Add(Toggle("SettingsPage.AutoRemoveMissing",
            () => _editor.Settings.AutoRemoveMissingMods,
            value => _editor.Settings.AutoRemoveMissingMods = value));
        page.Children.Add(WrapCard(missing));

        var import = Card("SettingsPage.ModImport", "SettingsPage.ModImportDesc");
        import.Children.Add(Toggle("SettingsPage.AutoAddImportedMods",
            () => _editor.Settings.AutoAddImportedModsToActiveProfile,
            value => _editor.Settings.AutoAddImportedModsToActiveProfile = value));
        import.Children.Add(Description("SettingsPage.AutoAddImportedModsDesc"));
        page.Children.Add(WrapCard(import));

        var check = Card("SettingsPage.VersionCheck", "SettingsPage.VersionCheckDesc");
        check.Children.Add(Toggle("SettingsPage.AutoCheckOnStartup",
            () => _editor.Settings.AutoCheckVersionOnStartup,
            value => _editor.Settings.AutoCheckVersionOnStartup = value));
        check.Children.Add(Toggle("SettingsPage.EnableBatchRepair",
            () => _editor.Settings.EnableBatchRepair,
            value => _editor.Settings.EnableBatchRepair = value));
        check.Children.Add(Description("SettingsPage.EnableBatchRepairDesc"));
        page.Children.Add(WrapCard(check));
        return page;
    }

    private StackPanel BuildLogs()
    {
        var page = new StackPanel();
        var level = Card("SettingsPage.LogLevel", "SettingsPage.LogLevelDesc1");
        level.Children.Add(Description("SettingsPage.LogLevelDesc2"));
        level.Children.Add(Description("SettingsPage.LogLevelDesc3"));
        var levels = new ComboBox
        {
            ItemsSource = Enum.GetValues<LogLevel>(),
            SelectedItem = _editor.Settings.LogLevel,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        levels.SelectionChanged += (_, _) =>
        {
            if (levels.SelectedItem is LogLevel selected)
                _editor.Settings.LogLevel = selected;
        };
        level.Children.Add(levels);
        page.Children.Add(WrapCard(level));

        var cleanup = Card("SettingsPage.LogAutoClean", "SettingsPage.LogAutoCleanDesc");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Toggle("SettingsPage.LogAutoClean",
            () => _editor.Settings.AutoCleanLogs,
            value => _editor.Settings.AutoCleanLogs = value));
        var maximum = new TextBox
        {
            Text = _editor.Settings.MaxLogFiles.ToString(), Width = 60,
            Margin = new Thickness(12, 4, 8, 0),
        };
        maximum.TextChanged += (_, _) =>
        {
            if (int.TryParse(maximum.Text, out var count) && count > 0)
                _editor.Settings.MaxLogFiles = count;
        };
        row.Children.Add(maximum);
        row.Children.Add(new TextBlock
        {
            Text = _localization["SettingsPage.LogAutoCleanFiles"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        cleanup.Children.Add(row);
        page.Children.Add(WrapCard(cleanup));
        return page;
    }

    private StackPanel BuildHome()
    {
        var page = new StackPanel();
        var separators = Card("SettingsPage.Separators", "SettingsPage.SeparatorsDesc");
        separators.Children.Add(Toggle("SettingsPage.EnableSeparators",
            () => _editor.Settings.ShowSeparator,
            value => _editor.Settings.ShowSeparator = value));
        page.Children.Add(WrapCard(separators));

        var tagging = Card("SettingsPage.AutoTagging", "SettingsPage.AutoTaggingDesc");
        tagging.Children.Add(Toggle("SettingsPage.EnableAutoTagging",
            () => _editor.Settings.EnableAutoTagging,
            value => { _editor.Settings.EnableAutoTagging = value; Refresh(); }));
        if (_editor.Settings.EnableAutoTagging)
        {
            tagging.Children.Add(Toggle("SettingsPage.AutoTagCreateMissing",
                () => _editor.Settings.AutoTagCreateMissingTags,
                value => _editor.Settings.AutoTagCreateMissingTags = value));
            var pairing = CommandButton("\uE8D4", "SettingsPage.EditAutoTagPairing");
            pairing.Margin = new Thickness(0, 8, 0, 0);
            pairing.HorizontalAlignment = HorizontalAlignment.Left;
            pairing.Click += (_, _) =>
            {
                try { _openAutoTagPairing(); }
                catch (Exception ex) { _reportError(ex); }
            };
            tagging.Children.Add(pairing);
        }
        page.Children.Add(WrapCard(tagging));

        var search = Card("SettingsPage.Search", "SettingsPage.EnableFuzzySearch");
        search.Children.Add(Toggle("SettingsPage.EnableFuzzySearch",
            () => _editor.Settings.EnableFuzzySearch,
            value => _editor.Settings.EnableFuzzySearch = value));
        search.Children.Add(Toggle("SettingsPage.CaseSensitive",
            () => _editor.Settings.CaseSensitiveSearch,
            value => _editor.Settings.CaseSensitiveSearch = value));
        page.Children.Add(WrapCard(search));

        var language = Card("SettingsPage.Language", "SettingsPage.LanguageDesc");
        var available = _localization.AvailableLanguages.ToArray();
        var choices = new ComboBox
        {
            ItemsSource = available.Select(item => item.DisplayName).ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(available,
                item => item.LocaleCode == _editor.Settings.Language)),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedIndex < 0 || choices.SelectedIndex >= available.Length)
                return;
            var code = available[choices.SelectedIndex].LocaleCode;
            _editor.Settings.Language = code;
            _localization.SelectedLanguage = code;
        };
        language.Children.Add(choices);
        page.Children.Add(WrapCard(language));

        var music = Card("SettingsPage.BackgroundMusic", "SettingsPage.BackgroundMusicDesc");
        music.Children.Add(Toggle("SettingsPage.EnableMusicPlayer",
            () => _editor.Settings.EnableMusicPlayer,
            value => { _editor.Settings.EnableMusicPlayer = value; _setMusicEnabled?.Invoke(value); Refresh(); }));
        music.Children.Add(Description("SettingsPage.EnableMusicPlayerDesc"));
        if (_editor.Settings.EnableMusicPlayer)
        {
            music.Children.Add(Toggle("SettingsPage.AutoPlayBackgroundMusic",
                () => _editor.Settings.AutoPlayBackgroundMusic,
                value => _editor.Settings.AutoPlayBackgroundMusic = value));
            music.Children.Add(Description("SettingsPage.AutoPlayBackgroundMusicDesc"));
        }
        page.Children.Add(WrapCard(music));
        return page;
    }

    private StackPanel BuildAppearance()
    {
        var page = new StackPanel();
        var background = Card("SettingsPage.Background", "SettingsPage.BackgroundDesc");
        background.Children.Add(Toggle("SettingsPage.UseCustomBackground",
            () => _editor.Settings.BackgroundMode == BackgroundMode.Image,
            value =>
            {
                _editor.Settings.BackgroundMode = value ? BackgroundMode.Image : BackgroundMode.Default;
                Refresh();
            }));

        var imageRow = new Grid { Margin = new Thickness(0, 12, 0, 0),
            IsEnabled = _editor.Settings.BackgroundMode == BackgroundMode.Image };
        imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var imageLabel = new TextBlock { Text = _localization["SettingsPage.BackgroundImage"],
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        imageRow.Children.Add(imageLabel);
        var imagePath = new TextBox { Text = _editor.Settings.BackgroundImagePath,
            IsReadOnly = true, Height = 36 };
        Grid.SetColumn(imagePath, 1);
        imageRow.Children.Add(imagePath);
        var browse = new Button { Content = _localization["CreatePage.OptionBrowse"],
            Height = 36, Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6) };
        browse.Click += (_, _) =>
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = _localization["SettingsPage.BrowseBackgroundImageDialog"],
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
                };
                if (dialog.ShowDialog() == true)
                {
                    _editor.Settings.BackgroundImagePath = dialog.FileName;
                    _editor.Settings.BackgroundMode = BackgroundMode.Image;
                    imagePath.Text = dialog.FileName;
                }
            }
            catch (Exception ex) { _reportError(ex); }
        };
        Grid.SetColumn(browse, 2);
        imageRow.Children.Add(browse);
        var clear = new Button { Content = _localization["SettingsPage.Clear"],
            Height = 36, Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6) };
        clear.Click += (_, _) =>
        {
            _editor.Settings.BackgroundImagePath = string.Empty;
            _editor.Settings.BackgroundMode = BackgroundMode.Default;
            Refresh();
        };
        Grid.SetColumn(clear, 3);
        imageRow.Children.Add(clear);
        background.Children.Add(imageRow);
        background.Children.Add(OpacitySlider("SettingsPage.BackgroundOpacity", 0, 1,
            _editor.Settings.BackgroundOpacity,
            value => _editor.Settings.BackgroundOpacity = (float)value));
        background.Children.Add(OpacitySlider("SettingsPage.CardOpacity", 0.3, 1,
            _editor.Settings.CardOpacity,
            value => _editor.Settings.CardOpacity = (float)value));
        page.Children.Add(WrapCard(background));
        return page;
    }

    private Grid OpacitySlider(string labelKey, double minimum, double maximum,
        double initialValue, Action<double> set)
    {
        var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = _localization[labelKey],
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var slider = new Slider { Minimum = minimum, Maximum = maximum,
            Value = initialValue, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        var valueText = new TextBlock { Text = initialValue.ToString("P0"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(valueText, 2);
        row.Children.Add(valueText);
        slider.ValueChanged += (_, _) =>
        {
            set(slider.Value);
            valueText.Text = slider.Value.ToString("P0");
        };
        return row;
    }
}
