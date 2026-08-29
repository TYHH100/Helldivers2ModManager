using System.Windows;
using Helldivers2ModManager.Frontend.Common;

namespace Helldivers2ModManager.Frontend.Views;

public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string prompt, string initialText = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialText;
        OkButton.Content = LocalizationSource.Catalog?.GetString("Common.OK") ?? "OK";
        CancelButton.Content = LocalizationSource.Catalog?.GetString("Common.Cancel") ?? "Cancel";
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    public string InputText => InputBox.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
