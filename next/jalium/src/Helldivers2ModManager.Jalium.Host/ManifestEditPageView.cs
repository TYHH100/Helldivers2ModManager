using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Microsoft.Win32;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class ManifestEditPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Card = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private static readonly Brush Accent = Paint(0x12, 0x74, 0xBD);
    private readonly ManifestEditor _editor;
    private readonly LocalizationService _localization;
    private readonly Window _owner;
    private readonly Action _back;
    private readonly Action<Exception> _reportError;
    private readonly Action<ImageSource>? _previewImage;
    private readonly StackPanel _options = new();
    private readonly TextBox _name = new();
    private readonly TextBox _description = new();
    private readonly TextBox _icon = new();
    private readonly Image _iconPreview = new();
    private bool _settingIcon;

    public ManifestEditPageView(ManifestEditor editor, LocalizationService localization, Window owner,
        Action back, Action<Exception> reportError, Action<ImageSource>? previewImage = null)
    {
        _editor = editor;
        _localization = localization;
        _owner = owner;
        _back = back;
        _reportError = reportError;
        _previewImage = previewImage;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var backButton = new Button { Width = 36, Height = 36, Content = Icon("\uE72B", 16),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        backButton.Click += (_, _) => _back();
        header.Children.Add(backButton);
        var title = new TextBlock { Text = _localization["DashboardPage.EditManifest"], FontSize = 24,
            FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Children.Add(header);

        var body = new StackPanel();
        body.Children.Add(Field("DashboardPage.EditNameTitle", _name, _editor.Name,
            value => _editor.Name = value));
        body.Children.Add(Field("DashboardPage.EditDescTitle", _description, _editor.Description,
            value => _editor.Description = value, multiline: true));
        body.Children.Add(BuildIconField());
        body.Children.Add(BuildFormatField());
        body.Children.Add(new TextBlock { Text = _localization["CreatePage.OptionTitle"], FontSize = 18,
            FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            Margin = new Thickness(0, 8, 0, 4) });
        body.Children.Add(new TextBlock { Text = _localization["CreatePage.OptionsDesc"], FontSize = 13,
            Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(_options);
        var add = Button(_localization["CreatePage.AddOption"], AddOption);
        add.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(add);
        var scroll = new ScrollViewer { Content = new Border { Child = body, Background = Card,
            BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24) }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = Button(_localization["Common.Cancel"], _back);
        footer.Children.Add(cancel);
        var save = Button(_localization["EditPage.Done"], Save);
        save.Background = Accent; save.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(save, 1); footer.Children.Add(save);
        Grid.SetRow(footer, 2); Children.Add(footer);
        RenderOptions();
    }

    private StackPanel Field(string key, TextBox box, string value, Action<string> changed, bool multiline = false)
    {
        box.Text = value;
        box.MinHeight = multiline ? 64 : 36;
        box.AcceptsReturn = multiline;
        box.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        box.Margin = new Thickness(0, 4, 0, 12);
        box.TextChanged += (_, _) => changed(box.Text);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = _localization[key], Foreground = Foreground,
            FontSize = 14, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(box);
        return panel;
    }

    private StackPanel BuildIconField()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = _localization["CreatePage.Icon"], Foreground = Foreground,
            FontSize = 14, FontWeight = FontWeights.SemiBold });
        var row = new Grid { Margin = new Thickness(0, 4, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _iconPreview.Width = 80; _iconPreview.Height = 80; _iconPreview.Stretch = Stretch.Uniform;
        _iconPreview.Margin = new Thickness(0, 0, 10, 0);
        _iconPreview.MouseLeftButtonDown += (_, e) => ShowPreview(_iconPreview, e);
        row.Children.Add(_iconPreview);
        _icon.Text = _editor.IconPath; _icon.MinHeight = 36;
        _icon.TextChanged += (_, _) => { if (!_settingIcon) { _editor.SetIconPath(_icon.Text); RefreshIcon(null); } };
        Grid.SetColumn(_icon, 1); row.Children.Add(_icon);
        var browse = Button(_localization["CreatePage.BrowseIcon"], () =>
        {
            var dialog = new OpenFileDialog { Filter = _localization["Common.FileFilterImage"],
                Title = _localization["CreatePage.BrowseIconDialog"] };
            if (dialog.ShowDialog() != true) return;
            _editor.SetIconFromFile(dialog.FileName);
            _settingIcon = true; _icon.Text = _editor.IconPath; _settingIcon = false;
            RefreshIcon(dialog.FileName);
        });
        Grid.SetColumn(browse, 2); row.Children.Add(browse);
        panel.Children.Add(row);
        RefreshIcon(null);
        return panel;
    }

    private StackPanel BuildFormatField()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = _localization["CreatePage.ManifestFormat"], Foreground = Foreground,
            FontSize = 14, FontWeight = FontWeights.SemiBold });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var v1 = new RadioButton { Content = _localization["CreatePage.ManifestFormatV1"],
            IsChecked = _editor.IsV1, GroupName = "ManifestEditFormat", Margin = new Thickness(0, 4, 16, 0) };
        var legacy = new RadioButton { Content = _localization["CreatePage.ManifestFormatLegacy"],
            IsChecked = !_editor.IsV1, GroupName = "ManifestEditFormat", Margin = new Thickness(0, 4, 0, 0) };
        v1.Checked += (_, _) => { _editor.SwitchFormat(true); RenderOptions(); };
        legacy.Checked += (_, _) => { _editor.SwitchFormat(false); RenderOptions(); };
        row.Children.Add(v1); row.Children.Add(legacy); panel.Children.Add(row);
        return panel;
    }

    private void RenderOptions()
    {
        _options.Children.Clear();
        foreach (var option in _editor.Options)
            _options.Children.Add(BuildOption(option));
    }

    private Border BuildOption(ManifestOptionDraft option)
    {
        var body = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Icon("\u2630", 14));
        var label = new TextBlock { Text = _localization["CreatePage.OptionTitle"], Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(label, 1); head.Children.Add(label);
        var remove = new Button { Content = Icon("\u00D7", 14), Width = 30, Height = 30,
            ToolTip = _localization["CreatePage.DeleteOption"] };
        remove.Click += (_, _) => { _editor.Options.Remove(option); RenderOptions(); };
        Grid.SetColumn(remove, 2); head.Children.Add(remove); body.Children.Add(head);
        body.Children.Add(TextField("CreatePage.Name", option.Name, value => option.Name = value));
        body.Children.Add(TextField("CreatePage.Description", option.Description, value => option.Description = value));
        body.Children.Add(PathField(option.IncludePaths, value => option.IncludePaths = value));
        if (_editor.IsV1)
        {
            body.Children.Add(ImagePathField(option.ImagePath, option.ExternalImagePath,
                option.SetImageFromFile, value => option.ImagePath = value, "CreateModOption.SelectIconTitle"));
            body.Children.Add(new TextBlock { Text = _localization["CreatePage.SubOptionTitle"], Foreground = Foreground,
                Margin = new Thickness(0, 8, 0, 4) });
            foreach (var sub in option.SubOptions)
                body.Children.Add(BuildSubOption(option, sub));
            var addSub = Button(_localization["CreatePage.AddSubOption"], () =>
            { option.SubOptions.Add(new ManifestSubOptionDraft()); RenderOptions(); });
            addSub.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(addSub);
        }
        return new Border { Child = body, Background = Paint(0x39, 0x39, 0x39), BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8) };
    }

    private Border BuildSubOption(ManifestOptionDraft parent, ManifestSubOptionDraft sub)
    {
        var body = new StackPanel();
        var head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = _localization["CreatePage.SubOptionTitle"], Foreground = Secondary });
        var remove = new Button { Content = Icon("\u00D7", 13), Width = 28, Height = 28,
            ToolTip = _localization["CreatePage.DeleteSubOption"] };
        remove.Click += (_, _) => { parent.SubOptions.Remove(sub); RenderOptions(); };
        Grid.SetColumn(remove, 1); head.Children.Add(remove); body.Children.Add(head);
        body.Children.Add(TextField("CreatePage.Name", sub.Name, value => sub.Name = value));
        body.Children.Add(TextField("CreatePage.Description", sub.Description, value => sub.Description = value));
        body.Children.Add(PathField(sub.IncludePaths, value => sub.IncludePaths = value));
        body.Children.Add(ImagePathField(sub.ImagePath, sub.ExternalImagePath,
            sub.SetImageFromFile, value => sub.ImagePath = value, "CreateSubOption.SelectIconTitle"));
        return new Border { Child = body, Background = Paint(0x43, 0x43, 0x43), Padding = new Thickness(10),
            CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 4, 0, 0) };
    }

    private StackPanel TextField(string key, string value, Action<string> changed)
    {
        var field = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        field.Children.Add(new TextBlock { Text = _localization[key], Foreground = Secondary, FontSize = 12 });
        var edit = new TextBox { Text = value, MinHeight = 32 };
        edit.TextChanged += (_, _) => changed(edit.Text); field.Children.Add(edit); return field;
    }

    private StackPanel PathField(string value, Action<string> changed)
    {
        var field = TextField("CreatePage.OptionInclude", value, changed);
        return field;
    }

    private StackPanel ImagePathField(string value, string? source, Action<string> selected,
        Action<string> changed, string dialogTitle)
    {
        var field = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        field.Children.Add(new TextBlock { Text = _localization["CreatePage.Icon"], Foreground = Secondary, FontSize = 12 });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var edit = new TextBox { Text = value, MinHeight = 32 };
        edit.TextChanged += (_, _) => changed(edit.Text);
        row.Children.Add(edit);
        var browse = Button(_localization["CreatePage.SelectIcon"], () =>
        {
            var dialog = new OpenFileDialog { Filter = _localization["Common.FileFilterImage"], Title = _localization[dialogTitle] };
            if (dialog.ShowDialog() != true) return;
            selected(dialog.FileName); edit.Text = Path.GetFileName(dialog.FileName);
        });
        Grid.SetColumn(browse, 1); row.Children.Add(browse); field.Children.Add(row); return field;
    }

    private void AddOption()
    {
        if (!_editor.IsV1)
        {
            var picker = new IncludeDirectoryPickerView(_editor.Mod.Directory.FullName, string.Empty,
                _localization, _owner);
            picker.ShowDialog();
            if (picker.Accepted) _editor.AddLegacyOptions(picker.SelectedPaths);
        }
        else _editor.AddOption();
        RenderOptions();
    }

    private void Save()
    {
        try { _editor.Save(); _back(); }
        catch (Exception ex) { _reportError(ex); }
    }

    private void RefreshIcon(string? selected)
    {
        _iconPreview.Source = LoadPreviewImage(_editor.IconPath, selected);
    }

    private ImageSource? LoadPreviewImage(string relative, string? selectedFile)
    {
        try
        {
            string? path = selectedFile;
            if (path is null && !string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative))
            {
                var root = Path.GetFullPath(_editor.Mod.Directory.FullName);
                path = Path.GetFullPath(Path.Combine(root, relative));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            return path is not null && File.Exists(path) ? BitmapImage.FromFile(path) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { return null; }
    }

    private void ShowPreview(Image image, MouseButtonEventArgs e)
    {
        if (image.Source is null || _previewImage is null) return;
        _previewImage(image.Source);
        e.Handled = true;
    }

    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, MinHeight = 34, Padding = new Thickness(12, 6, 12, 6) };
        button.Click += (_, _) => action(); return button;
    }

    private static TextBlock Icon(string glyph, double size) => new() { Text = glyph,
        FontFamily = new FontFamily("Segoe UI"), FontSize = size, Foreground = Foreground };
    private static Brush Paint(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
    public void Dispose() { }
}
