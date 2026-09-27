using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class HelpPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Surface = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private readonly LocalizationService _localization;
    private readonly Action _back;
    private readonly Action<string> _openLink;
    private readonly TextBlock _title = new();
    private readonly StackPanel _content = new();

    public HelpPageView(LocalizationService localization, Action back, Action<string> openLink)
    {
        _localization = localization;
        _back = back;
        _openLink = openLink;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var card = new Border { Background = Surface, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 16),
            Child = new ScrollViewer { Content = _content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Grid.SetRow(card, 0);
        Children.Add(card);

        var footer = new Border { Background = Surface, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12) };
        var backButton = new Button { MinWidth = 110, Height = 38, Content = new TextBlock {
            Text = "\uE72B  " + _localization["Common.Back"], FontFamily = new FontFamily("Segoe UI"),
            Foreground = Foreground }, HorizontalAlignment = HorizontalAlignment.Right };
        backButton.Click += (_, _) => _back();
        footer.Child = backButton;
        Grid.SetRow(footer, 1);
        Children.Add(footer);

        _localization.PropertyChanged += OnLocalizationChanged;
        Refresh();
    }

    private void Refresh()
    {
        _title.Text = _localization["HelpPage.Title"];
        _content.Children.Clear();
        _content.Children.Add(_title);
        _title.FontSize = 26;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Foreground;
        _title.Margin = new Thickness(0, 0, 0, 18);
        AddParagraph("HelpPage.Intro");
        AddSection("HelpPage.WorkflowTitle", "HelpPage.Workflow");
        AddSection("HelpPage.DataTitle", "HelpPage.Data");
        var community = new TextBlock { Text = _localization["HelpPage.Community"], FontSize = 18,
            FontWeight = FontWeights.SemiBold, Foreground = Foreground, Margin = new Thickness(0, 18, 0, 8) };
        _content.Children.Add(community);
        AddLink("HelpPage.Repository", "https://github.com/TYHH100/Helldivers2ModManager");
        AddLink("HelpPage.Issues", "https://github.com/TYHH100/Helldivers2ModManager/issues");
        AddLink("HelpPage.Discord", "https://discord.gg/helldiversmodding");
    }

    private void AddParagraph(string key) => _content.Children.Add(new TextBlock { Text = _localization[key],
        FontSize = 14, Foreground = Secondary, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 12) });

    private void AddSection(string titleKey, string bodyKey)
    {
        _content.Children.Add(new TextBlock { Text = _localization[titleKey], FontSize = 18,
            FontWeight = FontWeights.SemiBold, Foreground = Foreground, Margin = new Thickness(0, 14, 0, 8) });
        AddParagraph(bodyKey);
    }

    private void AddLink(string key, string url)
    {
        var link = new Button { Content = _localization[key], MinHeight = 34, Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        link.Click += (_, _) => _openLink(url);
        _content.Children.Add(link);
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private static SolidColorBrush Paint(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    public void Dispose() => _localization.PropertyChanged -= OnLocalizationChanged;
}
