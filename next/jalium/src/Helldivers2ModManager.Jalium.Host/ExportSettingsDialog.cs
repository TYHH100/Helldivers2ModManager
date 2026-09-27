using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed record ExportSettingsResult(string Format, string Level,
    bool UsePassword, string Password, string Encryption);

internal sealed class ExportSettingsDialog : Window
{
    private readonly LocalizationService _localization;
    private readonly ComboBox _format = new();
    private readonly ComboBox _level = new();
    private readonly CheckBox _usePassword = new();
    private readonly PasswordBox _password = new();
    private readonly ComboBox _encryption = new();
    private ExportSettingsResult? _result;

    private ExportSettingsDialog(LocalizationService localization, Window owner)
    {
        _localization = localization;
        Title = localization["DashboardPage.ExportTitle"];
        Width = 480;
        Height = 420;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("DashboardPage.ExportMsg"));
        _format.ItemsSource = new[] { localization["DashboardPage.ExportZip"],
            localization["DashboardPage.Export7zStandard"] };
        _format.SelectedIndex = 0;
        panel.Children.Add(Labeled("DashboardPage.ExportFormat", _format));
        _level.ItemsSource = new[] { localization["DashboardPage.Export7zFast"],
            localization["DashboardPage.Export7zStandard"], localization["DashboardPage.Export7zHigh"],
            localization["DashboardPage.Export7zUltra"] };
        _level.SelectedIndex = 1;
        panel.Children.Add(Labeled("DashboardPage.ExportCompression", _level));
        _usePassword.Content = localization["DashboardPage.ExportPassword"];
        _usePassword.Margin = new Thickness(0, 8, 0, 4);
        _usePassword.Click += (_, _) => RefreshPasswordState();
        panel.Children.Add(_usePassword);
        _password.Height = 36;
        _password.Visibility = Visibility.Collapsed;
        panel.Children.Add(_password);
        _encryption.ItemsSource = new[] { localization["DashboardPage.ExportZipCrypto"],
            localization["DashboardPage.ExportAes128"], localization["DashboardPage.ExportAes192"],
            localization["DashboardPage.ExportAes256"] };
        _encryption.SelectedIndex = 3;
        _encryption.Visibility = Visibility.Collapsed;
        panel.Children.Add(Labeled("DashboardPage.ExportEncryption", _encryption));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = localization["Common.Cancel"], Width = 92, Height = 36 };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        var confirm = new Button { Content = localization["Common.Confirm"], Width = 92,
            Height = 36, Margin = new Thickness(8, 0, 0, 0) };
        confirm.Click += (_, _) => Accept();
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static ExportSettingsResult? Show(Window owner, LocalizationService localization)
    {
        var dialog = new ExportSettingsDialog(localization, owner);
        dialog.ShowDialog();
        return dialog._result;
    }

    private void RefreshPasswordState()
    {
        var visible = _usePassword.IsChecked == true;
        _password.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _encryption.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Accept()
    {
        if (_usePassword.IsChecked == true && string.IsNullOrEmpty(_password.Password))
            return;
        _result = new ExportSettingsResult(_format.SelectedItem?.ToString() ?? string.Empty,
            _level.SelectedItem?.ToString() ?? string.Empty, _usePassword.IsChecked == true,
            _password.Password, _encryption.SelectedItem?.ToString() ?? string.Empty);
        Close();
    }

    private TextBlock Label(string key) => new()
    {
        Text = _localization[key],
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    };

    private StackPanel Labeled(string key, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = _localization[key], Margin = new Thickness(0, 0, 0, 4) });
        control.Width = 320;
        panel.Children.Add(control);
        return panel;
    }
}
