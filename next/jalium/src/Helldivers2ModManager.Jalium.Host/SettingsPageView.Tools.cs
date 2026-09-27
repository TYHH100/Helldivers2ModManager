using Jalium.UI;
using Jalium.UI.Controls;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class SettingsPageView
{
    private StackPanel BuildTools()
    {
        var page = new StackPanel();
        var content = Card("SettingsPage.Tools", "SettingsPage.ToolsDesc");
        content.Children.Add(Heading("SettingsPage.AiTranslation", new Thickness(0, 4, 0, 0)));
        content.Children.Add(Description("SettingsPage.AiTranslationDesc"));
        content.Children.Add(ToolText("SettingsPage.AiEndpoint", _editor.Settings.AiTranslationEndpoint,
            value => _editor.Settings.AiTranslationEndpoint = value));
        content.Children.Add(ToolText("SettingsPage.AiModel", _editor.Settings.AiTranslationModel,
            value => _editor.Settings.AiTranslationModel = value));
        content.Children.Add(ToolText("SettingsPage.AiTargetLanguage", _editor.Settings.AiTranslationTargetLanguage,
            value => _editor.Settings.AiTranslationTargetLanguage = value));

        var keyField = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        keyField.Children.Add(new TextBlock { Text = _localization["SettingsPage.AiApiKey"],
            FontSize = 12, Foreground = Secondary });
        var key = new PasswordBox { Password = _editor.Settings.AiTranslationApiKey ?? string.Empty,
            Height = 36, Margin = new Thickness(0, 4, 0, 0) };
        key.PasswordChanged += (_, _) => _editor.Settings.AiTranslationApiKey = key.Password;
        keyField.Children.Add(key);
        content.Children.Add(keyField);

        content.Children.Add(Toggle("SettingsPage.AiEnableThinking",
            () => _editor.Settings.AiTranslationEnableThinking,
            value => { _editor.Settings.AiTranslationEnableThinking = value; Refresh(); }));
        content.Children.Add(Description("SettingsPage.AiThinkingDesc"));

        var effort = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        effort.Children.Add(new TextBlock { Text = _localization["SettingsPage.AiReasoningEffort"],
            FontSize = 12, Foreground = Secondary });
        var values = new[] { "low", "high", "max" };
        var names = new[] { "SettingsPage.AiReasoningLow", "SettingsPage.AiReasoningHigh",
            "SettingsPage.AiReasoningMax" };
        var choices = new ComboBox { ItemsSource = names.Select(name => _localization[name]).ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(values, _editor.Settings.AiTranslationReasoningEffort)),
            IsEnabled = _editor.Settings.AiTranslationEnableThinking,
            Width = 180, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0) };
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedIndex >= 0 && choices.SelectedIndex < values.Length)
                _editor.Settings.AiTranslationReasoningEffort = values[choices.SelectedIndex];
        };
        effort.Children.Add(choices);
        content.Children.Add(effort);

        var replay = CommandButton("\uE777", "SettingsPage.ReplayTutorial");
        replay.Margin = new Thickness(0, 16, 0, 4);
        replay.Click += (_, _) => RunAsync(_replayTutorial);
        content.Children.Add(replay);
        content.Children.Add(Description("SettingsPage.ReplayTutorialDesc"));

        var hashes = CommandButton("\uE713", "SettingsPage.RecomputeHashes");
        hashes.Margin = new Thickness(0, 0, 0, 4);
        hashes.Click += (_, _) => RunAsync(_recomputeHashes);
        content.Children.Add(hashes);
        content.Children.Add(Description("SettingsPage.RecomputeHashesDesc"));

        var purge = CommandButton("\uE74D", "SettingsPage.ForceCleanPatches");
        purge.Margin = new Thickness(0, 0, 0, 4);
        purge.Click += (_, _) => RunAsync(_hardPurge);
        content.Children.Add(purge);
        content.Children.Add(Description("SettingsPage.ForceCleanPatchesDesc"));

        var reset = CommandButton("\uE777", "SettingsPage.ResetSettings");
        reset.Margin = new Thickness(0, 8, 0, 4);
        reset.Foreground = Paint(0xFF, 0x80, 0x80);
        reset.Click += (_, _) => RunAsync(_resetSettings);
        content.Children.Add(reset);
        content.Children.Add(new TextBlock { Text = _localization["SettingsPage.ResetSettingsWarning"],
            Foreground = Paint(0xE8, 0xB0, 0x58), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        page.Children.Add(WrapCard(content));
        return page;
    }

    private StackPanel ToolText(string key, string value, Action<string> update)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        field.Children.Add(new TextBlock { Text = _localization[key], FontSize = 12, Foreground = Secondary });
        var input = new TextBox { Text = value, MinHeight = 36, Margin = new Thickness(0, 4, 0, 0) };
        input.TextChanged += (_, _) => update(input.Text);
        field.Children.Add(input);
        return field;
    }
}
