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

internal sealed class CreatePageView : Grid
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xA9, 0xA9, 0xA9);
    private static readonly Brush Card = Paint(0x2D, 0x2D, 0x2D);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private static readonly Brush Accent = Paint(0x12, 0x74, 0xBD);

    private readonly CreateModEditor _editor;
    private readonly LocalizationService _localization;
    private readonly Window _owner;
    private readonly Func<Task<ModProblem[]>> _create;
    private readonly Func<Task> _created;
    private readonly Action _cancel;
    private readonly Action<Exception> _reportError;
    private readonly Func<ModProblem, string> _formatProblem;
    private readonly StackPanel _options = new();
    private readonly TextBox _name = new();
    private readonly TextBox _source = new();
    private readonly TextBox _icon = new();
    private readonly Image _iconPreview = new();
    private readonly Button _createButton = new();
    private readonly TextBlock _error = new();
    private readonly TextBlock _legacyHint = new();
    private readonly Border _banner = new();
    private bool _creating;

    public CreatePageView(CreateModEditor editor, LocalizationService localization, Window owner,
        Func<Task<ModProblem[]>> create, Func<Task> created, Action cancel,
        Func<ModProblem, string> formatProblem, Action<Exception> reportError)
    {
        _editor = editor;
        _localization = localization;
        _owner = owner;
        _create = create;
        _created = created;
        _cancel = cancel;
        _formatProblem = formatProblem;
        _reportError = reportError;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock { Text = localization["CreatePage.Title"], FontSize = 21,
            FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        Children.Add(title);
        _banner.Child = BuildBanner();
        _banner.Background = Paint(0x1A, 0x3A, 0x5C);
        _banner.BorderBrush = Paint(0x2A, 0x5A, 0x8C);
        _banner.BorderThickness = new Thickness(1);
        _banner.CornerRadius = new CornerRadius(8);
        _banner.Padding = new Thickness(14, 12, 14, 12);
        _banner.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_banner, 1);
        Children.Add(_banner);

        var content = new StackPanel();
        content.Children.Add(BuildForm());
        var scroll = new ScrollViewer { Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2);
        Children.Add(scroll);

        var footer = BuildFooter();
        Grid.SetRow(footer, 3);
        Children.Add(footer);
        RenderOptions();
        RefreshCreateEnabled();
    }

    private Grid BuildBanner()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "\uE946", FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 18, Foreground = Paint(0x6A, 0xB0, 0xF3), Margin = new Thickness(0, 0, 10, 0) });
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock { Text = _localization["CreatePage.GuideTitle"],
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Paint(0x9C, 0xCF, 0xF0) });
        foreach (var key in new[] { "CreatePage.GuideDesc1", "CreatePage.GuideStep1",
                     "CreatePage.GuideStep2", "CreatePage.GuideStep3", "CreatePage.GuideHint" })
            copy.Children.Add(new TextBlock { Text = _localization[key], FontSize = 12,
                Foreground = Paint(0x8A, 0xBC, 0xE8), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        var dismiss = IconButton("\u00D7", _localization["CreatePage.CloseHint"],
            () => _banner.Visibility = Visibility.Collapsed);
        Grid.SetColumn(dismiss, 2);
        grid.Children.Add(dismiss);
        return grid;
    }

    private Border BuildForm()
    {
        var form = new StackPanel();
        form.Children.Add(FieldLabel("CreatePage.Name", "CreatePage.NameDesc"));
        _name.Text = _editor.Name;
        _name.Height = 36;
        _name.Margin = new Thickness(0, 0, 0, 16);
        _name.TextChanged += (_, _) => { _editor.Name = _name.Text; RefreshCreateEnabled(); };
        form.Children.Add(_name);

        form.Children.Add(FieldLabel("CreatePage.Description", "CreatePage.DescriptionDesc"));
        var description = new TextBox { Text = _editor.Description, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 60,
            Margin = new Thickness(0, 0, 0, 16) };
        description.TextChanged += (_, _) => _editor.Description = description.Text;
        form.Children.Add(description);

        form.Children.Add(FieldLabel("CreatePage.Icon", "CreatePage.IconDesc"));
        var iconRow = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _iconPreview.Width = 48;
        _iconPreview.Height = 48;
        _iconPreview.Stretch = Stretch.Uniform;
        _iconPreview.Margin = new Thickness(0, 0, 12, 0);
        iconRow.Children.Add(_iconPreview);
        _icon.Text = _editor.IconPath;
        _icon.Height = 36;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        var selectingIcon = false;
        _icon.TextChanged += (_, _) =>
        {
            if (!selectingIcon)
            {
                _editor.SetIconPath(_icon.Text);
                _iconPreview.Source = PreviewImage(_icon.Text, null);
            }
        };
        Grid.SetColumn(_icon, 1);
        iconRow.Children.Add(_icon);
        var browseIcon = CommandButton("CreatePage.BrowseIcon", () =>
        {
            var file = ChooseImage("CreatePage.BrowseIconDialog");
            if (file is null)
                return;
            _editor.SetIconFromFile(file);
            selectingIcon = true;
            _icon.Text = _editor.IconPath;
            selectingIcon = false;
            _iconPreview.Source = PreviewImage(_editor.IconPath, file);
        });
        browseIcon.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browseIcon, 2);
        iconRow.Children.Add(browseIcon);
        form.Children.Add(iconRow);

        form.Children.Add(FieldLabel("CreatePage.SourceDir", "CreatePage.SourceDirDesc1"));
        var sourceRow = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _source.Text = _editor.SourceDirectory;
        _source.Height = 36;
        _source.TextChanged += (_, _) =>
        {
            var wasEmpty = _editor.Options.Count == 0;
            _editor.SetSourceDirectory(_source.Text);
            if (string.IsNullOrWhiteSpace(_name.Text))
                _name.Text = _editor.Name;
            if (string.IsNullOrWhiteSpace(_icon.Text))
                _icon.Text = _editor.IconPath;
            if (wasEmpty && _editor.Options.Count > 0)
                RenderOptions();
            RefreshCreateEnabled();
        };
        sourceRow.Children.Add(_source);
        var browseSource = CommandButton("CreatePage.BrowseIcon", BrowseSource);
        browseSource.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browseSource, 1);
        sourceRow.Children.Add(browseSource);
        form.Children.Add(sourceRow);

        form.Children.Add(FieldLabel("CreatePage.ManifestFormat", "CreatePage.ManifestFormatDesc"));
        var format = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8) };
        var v1 = new RadioButton { Content = _localization["CreatePage.ManifestFormatV1"],
            GroupName = "CreateManifestFormat", IsChecked = _editor.IsV1Manifest,
            Margin = new Thickness(0, 0, 16, 0) };
        var legacy = new RadioButton { Content = _localization["CreatePage.ManifestFormatLegacy"],
            GroupName = "CreateManifestFormat", IsChecked = !_editor.IsV1Manifest };
        v1.Checked += (_, _) => { _editor.IsV1Manifest = true; RenderOptions(); };
        legacy.Checked += (_, _) => { _editor.IsV1Manifest = false; RenderOptions(); };
        format.Children.Add(v1);
        format.Children.Add(legacy);
        form.Children.Add(format);
        _legacyHint.Text = _localization["CreatePage.LegacyHint"];
        _legacyHint.TextWrapping = TextWrapping.Wrap;
        _legacyHint.Foreground = Secondary;
        _legacyHint.Margin = new Thickness(0, 0, 0, 14);
        form.Children.Add(_legacyHint);

        form.Children.Add(FieldLabel("CreatePage.OptionTitle", "CreatePage.OptionsDesc"));
        form.Children.Add(new TextBlock { Text = _localization["CreatePage.DragHint"],
            Foreground = Secondary, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        form.Children.Add(_options);
        var add = CommandButton("CreatePage.AddOption", AddOption);
        add.HorizontalAlignment = HorizontalAlignment.Center;
        add.Margin = new Thickness(0, 8, 0, 0);
        add.ToolTip = _localization["CreatePage.AddOptionHint"];
        form.Children.Add(add);
        return new Border { Child = form, Background = Card, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24) };
    }

    private Border BuildFooter()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = CommandButton("Common.Cancel", _cancel);
        cancel.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(cancel);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        _error.Foreground = Paint(0xF4, 0x78, 0x78);
        _error.VerticalAlignment = VerticalAlignment.Center;
        _error.TextWrapping = TextWrapping.Wrap;
        _error.Margin = new Thickness(0, 0, 12, 0);
        right.Children.Add(_error);
        _createButton.Content = _localization["CreatePage.Create"];
        _createButton.Background = Accent;
        _createButton.Foreground = Foreground;
        _createButton.MinHeight = 38;
        _createButton.Padding = new Thickness(24, 8, 24, 8);
        _createButton.Click += async (_, _) => await CreateAsync();
        right.Children.Add(_createButton);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return new Border { Child = grid, Background = Card, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12), Margin = new Thickness(0, 16, 0, 0) };
    }

    private void RenderOptions()
    {
        _options.Children.Clear();
        _legacyHint.Visibility = _editor.IsV1Manifest ? Visibility.Collapsed : Visibility.Visible;
        foreach (var option in _editor.Options)
            _options.Children.Add(BuildOption(option));
    }

    private Border BuildOption(CreateOptionDraft option)
    {
        var body = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var handle = DragHandle(option);
        header.Children.Add(handle);
        var label = Label("CreatePage.OptionTitle", 14);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        var remove = IconButton("\u00D7", _localization["CreatePage.DeleteOption"], () =>
        {
            _editor.Options.Remove(option);
            RenderOptions();
        });
        Grid.SetColumn(remove, 2);
        header.Children.Add(remove);
        body.Children.Add(header);

        if (_editor.IsV1Manifest)
        {
            body.Children.Add(EditField("CreatePage.Name", option.Name, value => option.Name = value));
            body.Children.Add(EditField("CreatePage.Description", option.Description,
                value => option.Description = value));
            body.Children.Add(PathField("CreatePage.OptionInclude", option.IncludePaths,
                value => option.IncludePaths = value,
                () => BrowseInclude(paths => option.IncludePaths = paths, option.IncludePaths)));
            body.Children.Add(ImageField(option.ImagePath, option.ImageSourceFile,
                value => option.SetImagePath(value), file => option.SetImageFromFile(file),
                "CreateModOption.SelectIconTitle"));
            body.Children.Add(Label("CreatePage.SubOptionTitle", 13));
            foreach (var sub in option.SubOptions)
                body.Children.Add(BuildSubOption(option, sub));
            var addSub = CommandButton("CreatePage.AddSubOption", () =>
            {
                option.SubOptions.Add(new CreateSubOptionDraft());
                RenderOptions();
            });
            addSub.HorizontalAlignment = HorizontalAlignment.Center;
            addSub.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(addSub);
        }
        else
            body.Children.Add(new TextBlock { Text = option.Name, Foreground = Foreground,
                FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });

        var border = new Border { Child = body, Background = Paint(0x33, 0x33, 0x33),
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8), AllowDrop = true };
        border.DragOver += (_, args) =>
        {
            if (args.Data.GetData(typeof(CreateOptionDraft)) is CreateOptionDraft)
            {
                args.Effects = DragDropEffects.Move;
                args.Handled = true;
            }
        };
        border.Drop += (_, args) =>
        {
            if (args.Data.GetData(typeof(CreateOptionDraft)) is not CreateOptionDraft source
                || ReferenceEquals(source, option))
                return;
            _editor.Options.Remove(source);
            _editor.Options.Insert(_editor.Options.IndexOf(option), source);
            RenderOptions();
            args.Handled = true;
        };
        return border;
    }

    private Border BuildSubOption(CreateOptionDraft parent, CreateSubOptionDraft sub)
    {
        var body = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(DragHandle(sub));
        var title = Label("CreatePage.SubOptionTitle", 12);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var remove = IconButton("\u00D7", _localization["CreatePage.DeleteSubOption"], () =>
        {
            parent.SubOptions.Remove(sub);
            RenderOptions();
        });
        Grid.SetColumn(remove, 2);
        header.Children.Add(remove);
        body.Children.Add(header);
        body.Children.Add(EditField("CreatePage.Name", sub.Name, value => sub.Name = value));
        body.Children.Add(EditField("CreatePage.Description", sub.Description,
            value => sub.Description = value));
        body.Children.Add(PathField("CreatePage.OptionInclude", sub.IncludePaths,
            value => sub.IncludePaths = value,
            () => BrowseInclude(paths => sub.IncludePaths = paths, sub.IncludePaths)));
        body.Children.Add(ImageField(sub.ImagePath, sub.ImageSourceFile,
            value => sub.SetImagePath(value), file => sub.SetImageFromFile(file),
            "CreateSubOption.SelectIconTitle"));
        var border = new Border { Child = body, Background = Paint(0x39, 0x39, 0x39),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(12),
            Margin = new Thickness(0, 4, 0, 0), AllowDrop = true };
        border.DragOver += (_, args) =>
        {
            if (args.Data.GetData(typeof(CreateSubOptionDraft)) is CreateSubOptionDraft)
            {
                args.Effects = DragDropEffects.Move;
                args.Handled = true;
            }
        };
        border.Drop += (_, args) =>
        {
            if (args.Data.GetData(typeof(CreateSubOptionDraft)) is not CreateSubOptionDraft source
                || ReferenceEquals(source, sub) || !parent.SubOptions.Contains(source))
                return;
            parent.SubOptions.Remove(source);
            parent.SubOptions.Insert(parent.SubOptions.IndexOf(sub), source);
            RenderOptions();
            args.Handled = true;
        };
        return border;
    }

    private StackPanel EditField(string key, string value, Action<string> changed)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        field.Children.Add(Label(key, 12));
        var edit = new TextBox { Text = value, Height = 32 };
        edit.TextChanged += (_, _) => changed(edit.Text);
        field.Children.Add(edit);
        return field;
    }

    private StackPanel PathField(string key, string value, Action<string> changed, Action browse)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        field.Children.Add(Label(key, 12));
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var edit = new TextBox { Text = value, Height = 32 };
        edit.TextChanged += (_, _) => changed(edit.Text);
        row.Children.Add(edit);
        var button = CommandButton("CreatePage.OptionBrowse", () =>
        {
            browse();
        });
        button.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        field.Children.Add(row);
        return field;
    }

    private StackPanel ImageField(string value, string? source, Action<string> changed,
        Action<string> selected, string dialogTitle)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        field.Children.Add(Label("CreatePage.Icon", 12));
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var preview = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 8, 0), Source = PreviewImage(value, source) };
        row.Children.Add(preview);
        var edit = new TextBox { Text = value, Height = 32,
            VerticalAlignment = VerticalAlignment.Center };
        var selectingImage = false;
        edit.TextChanged += (_, _) =>
        {
            if (!selectingImage)
            {
                changed(edit.Text);
                preview.Source = PreviewImage(edit.Text, null);
            }
        };
        Grid.SetColumn(edit, 1);
        row.Children.Add(edit);
        var browse = CommandButton("CreatePage.SelectIcon", () =>
        {
            var file = ChooseImage(dialogTitle);
            if (file is null)
                return;
            selected(file);
            selectingImage = true;
            edit.Text = Path.GetFileName(file);
            selectingImage = false;
            preview.Source = PreviewImage(edit.Text, file);
        });
        browse.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(browse, 2);
        row.Children.Add(browse);
        field.Children.Add(row);
        return field;
    }

    private void BrowseInclude(Action<string> apply, string current)
    {
        if (!Directory.Exists(_editor.SourceDirectory))
        {
            _reportError(new InvalidOperationException(_localization["CreateModOption.SetSourceDirHint"]));
            return;
        }
        var picker = new IncludeDirectoryPickerView(_editor.SourceDirectory, current,
            _localization, _owner);
        picker.ShowDialog();
        if (picker.Accepted)
        {
            apply(string.Join(';', picker.SelectedPaths));
            RenderOptions();
        }
    }

    private void AddOption()
    {
        if (!_editor.IsV1Manifest)
        {
            BrowseInclude(paths =>
                _editor.AddLegacyDirectories(CreateOptionDraft.ParsePaths(paths)), string.Empty);
            return;
        }
        _editor.AddOption();
        RenderOptions();
    }

    private async Task CreateAsync()
    {
        if (_creating)
            return;
        _creating = true;
        _createButton.IsEnabled = false;
        _error.Text = string.Empty;
        try
        {
            var problems = await _create();
            if (problems.Length > 0)
            {
                _error.Text = string.Join(Environment.NewLine, problems.Select(_formatProblem));
                return;
            }
            await _created();
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
        finally
        {
            _creating = false;
            RefreshCreateEnabled();
        }
    }

    private void BrowseSource()
    {
        var dialog = new FolderPickerDialog(_localization["CreatePage.BrowseSourceDialog"],
            _source.Text, _localization, _owner);
        dialog.ShowDialog();
        if (dialog.Accepted && dialog.SelectedPath is not null)
            _source.Text = dialog.SelectedPath;
    }

    private string? ChooseImage(string title)
    {
        var dialog = new OpenFileDialog { Title = _localization[title],
            Filter = _localization["Common.FileFilterImage"] };
        if (Directory.Exists(_editor.SourceDirectory))
            dialog.InitialDirectory = _editor.SourceDirectory;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private ImageSource? PreviewImage(string relative, string? selectedFile)
    {
        var path = selectedFile;
        if (path is null && !string.IsNullOrWhiteSpace(relative)
            && Directory.Exists(_editor.SourceDirectory))
        {
            try
            {
                var root = Path.GetFullPath(_editor.SourceDirectory);
                var candidate = Path.GetFullPath(Path.Combine(root, relative));
                if (candidate.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                    path = candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException) { }
        }
        if (path is null || !File.Exists(path))
            return null;
        try { return BitmapImage.FromFile(path); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RefreshCreateEnabled() => _createButton.IsEnabled = !_creating
        && !string.IsNullOrWhiteSpace(_name.Text)
        && !string.IsNullOrWhiteSpace(_source.Text);

    private StackPanel FieldLabel(string title, string description)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(Label(title, 16));
        panel.Children.Add(new TextBlock { Text = _localization[description], FontSize = 13,
            Foreground = Secondary, Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private TextBlock Label(string key, double size) => new()
    {
        Text = _localization[key], FontSize = size, FontWeight = FontWeights.SemiBold,
        Foreground = Foreground, Margin = new Thickness(0, 0, 0, 4),
    };

    private Button CommandButton(string key, Action action)
    {
        var button = new Button { Content = _localization[key], MinHeight = 32,
            Foreground = Foreground, Background = Card, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(12, 6, 12, 6) };
        button.Click += (_, _) => action();
        return button;
    }

    private Button IconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button { Width = 30, Height = 30, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), ToolTip = tooltip,
            Content = new TextBlock { Text = glyph, Foreground = Foreground,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 14 } };
        button.Click += (_, _) => action();
        return button;
    }

    private Button DragHandle(object item)
    {
        var handle = IconButton("\u2630", _localization["CreatePage.DragHint"], () => { });
        handle.PreviewMouseMove += (_, args) =>
        {
            if (args.LeftButton == MouseButtonState.Pressed)
                DragDrop.DoDragDrop(handle, item, DragDropEffects.Move);
        };
        return handle;
    }

    private static Brush Paint(byte r, byte g, byte b)
        => new SolidColorBrush(Color.FromRgb(r, g, b));
}
