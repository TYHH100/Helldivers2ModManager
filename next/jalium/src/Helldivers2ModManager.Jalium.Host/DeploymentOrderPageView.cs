using System.ComponentModel;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Data;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class DeploymentOrderPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Brush(0xFF, 0xFF, 0xFF);
    private static readonly Brush Secondary = Brush(0xB3, 0xB3, 0xB3);
    private static readonly Brush Tertiary = Brush(0x80, 0x80, 0x80);
    private static readonly Brush Stroke = Brush(0x3A, 0x3A, 0x3A);
    private static readonly Brush FillSecondary = Brush(0x45, 0x45, 0x45);
    private static readonly Brush ButtonBorder = Brush(0x9D, 0x9D, 0x9D);

    private readonly DeploymentOrderEditor _editor;
    private readonly LocalizationService _localization;
    private readonly Action<Exception> _reportError;
    private readonly TextBlock _title;
    private readonly TextBlock _description;
    private readonly TextBlock _hint;
    private readonly Button _back;
    private readonly Button _moveTop;
    private readonly Button _moveBottom;
    private readonly Button _sync;
    private readonly Button _clear;
    private readonly Button _selectAll;
    private readonly Button _deselectAll;
    private readonly Button _invert;
    private readonly ListBox _list;
    private DeploymentOrderItem? _dragItem;
    private Point _dragStart;
    private bool _disposed;

    public DeploymentOrderPageView(
        DeploymentOrderEditor editor,
        LocalizationService localization,
        Action navigateBack,
        Action<Exception> reportError)
    {
        _editor = editor;
        _localization = localization;
        _reportError = reportError;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _back = new Button
        {
            Width = 36,
            Height = 36,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = new TextBlock
            {
                Text = "\uE72B",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 16,
            },
        };
        _back.Click += (_, _) => navigateBack();
        header.Children.Add(_back);
        _title = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI Variable, Segoe UI, Arial"),
            Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);
        Children.Add(header);

        _description = new TextBlock { FontSize = 13, Foreground = Secondary };
        var direction = new Border
        {
            Background = FillSecondary,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = _description,
        };
        Grid.SetRow(direction, 1);
        Children.Add(direction);

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        _moveTop = AddToolbarButton(toolbar, () => RunAsync(_editor.MoveToTopAsync));
        _moveBottom = AddToolbarButton(toolbar, () => RunAsync(_editor.MoveToBottomAsync));
        AddSeparator(toolbar);
        _sync = AddToolbarButton(toolbar, () => RunAsync(_editor.SyncFromDashboardAsync));
        _clear = AddToolbarButton(toolbar, () => RunAsync(_editor.ClearOrderAsync));
        AddSeparator(toolbar);
        _selectAll = AddToolbarButton(toolbar, _editor.SelectAll);
        _deselectAll = AddToolbarButton(toolbar, _editor.DeselectAll);
        _invert = AddToolbarButton(toolbar, _editor.InvertSelection);
        var toolbarBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = toolbar,
        };
        Grid.SetRow(toolbarBorder, 2);
        Children.Add(toolbarBorder);

        _list = new ListBox
        {
            ItemsSource = _editor.Items,
            ItemTemplate = CreateItemTemplate(),
            SelectionMode = SelectionMode.Extended,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            AllowDrop = true,
        };
        _list.PreviewMouseLeftButtonDown += OnMouseDown;
        _list.PreviewMouseMove += OnMouseMove;
        _list.DragOver += OnDragOver;
        _list.Drop += OnDrop;
        var listBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = _list,
        };
        Grid.SetRow(listBorder, 3);
        Children.Add(listBorder);

        _hint = new TextBlock
        {
            FontSize = 12,
            Foreground = Tertiary,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(_hint, 4);
        Children.Add(_hint);

        _editor.PropertyChanged += OnEditorChanged;
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshTexts();
        RefreshActions();
    }

    private static DataTemplate CreateItemTemplate()
    {
        var template = new DataTemplate(typeof(DeploymentOrderItem));
        template.SetVisualTree(() =>
        {
            var row = new Grid { Margin = new Thickness(4, 2, 4, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var check = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
            };
            BindingOperations.SetBinding(check, CheckBox.IsCheckedProperty,
                new Binding(nameof(DeploymentOrderItem.IsSelected)) { Mode = BindingMode.TwoWay });
            row.Children.Add(check);
            var handle = new TextBlock
            {
                Text = "\uE763",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
                Foreground = Tertiary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetColumn(handle, 1);
            row.Children.Add(handle);
            var name = new TextBlock
            {
                FontSize = 14,
                Foreground = Foreground,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetBinding(TextBlock.TextProperty, nameof(DeploymentOrderItem.Name));
            Grid.SetColumn(name, 2);
            row.Children.Add(name);
            return row;
        });
        return template;
    }

    private static Button AddToolbarButton(WrapPanel toolbar, Action action)
    {
        var button = new Button
        {
            Margin = new Thickness(0, 0, 6, 4),
            MinHeight = 36,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI, Arial"),
            Foreground = Foreground,
            Background = Brushes.Transparent,
            BorderBrush = ButtonBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 8, 16, 8),
        };
        button.Click += (_, _) => action();
        toolbar.Children.Add(button);
        return button;
    }

    private static void AddSeparator(WrapPanel toolbar) => toolbar.Children.Add(new Border
    {
        Width = 1,
        Height = 20,
        Background = Stroke,
        Margin = new Thickness(12, 4, 12, 4),
    });

    private async void RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
    }

    private void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        _dragItem = FindItem(e.OriginalSource as DependencyObject)?.DataContext as DeploymentOrderItem;
        _dragStart = e.GetPosition(_list);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var current = e.GetPosition(_list);
        if (Math.Abs(current.X - _dragStart.X) < 6 && Math.Abs(current.Y - _dragStart.Y) < 6)
            return;
        var source = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(_list, source, DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(typeof(DeploymentOrderItem)) is DeploymentOrderItem)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(typeof(DeploymentOrderItem)) is not DeploymentOrderItem source)
            return;
        var container = FindItem(e.OriginalSource as DependencyObject);
        var target = container?.DataContext as DeploymentOrderItem;
        var index = target is null ? _editor.Items.Count : _editor.Items.IndexOf(target);
        if (container is not null && e.GetPosition(container).Y >= container.ActualHeight / 2)
            index++;
        RunAsync(() => _editor.MoveByDropAsync(source, index));
        e.Handled = true;
    }

    private static ListBoxItem? FindItem(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ListBoxItem item)
                return item;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeploymentOrderEditor.Title))
            _title.Text = _editor.Title;
        if (e.PropertyName == nameof(DeploymentOrderEditor.OrderDescription))
            _description.Text = _editor.OrderDescription;
        if (e.PropertyName is nameof(DeploymentOrderEditor.CanMoveToTop) or nameof(DeploymentOrderEditor.CanMoveToBottom))
            RefreshActions();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => RefreshTexts();

    private void RefreshTexts()
    {
        _back.ToolTip = _localization["Common.Back"];
        _title.Text = _editor.Title;
        _description.Text = _editor.OrderDescription;
        _moveTop.Content = _localization["DashboardPage.MoveToTop"];
        _moveBottom.Content = _localization["DashboardPage.MoveToBottom"];
        _sync.Content = _localization["DeploymentOrderPage.SyncFromHome"];
        _clear.Content = _localization["DeploymentOrderPage.ClearList"];
        _selectAll.Content = _localization["DashboardPage.SelectAll"];
        _deselectAll.Content = _localization["DeploymentOrderPage.DeselectAll"];
        _invert.Content = _localization["DeploymentOrderPage.InvertSelect"];
        _hint.Text = _localization["DeploymentOrderPage.HintText"];
    }

    private void RefreshActions()
    {
        _moveTop.IsEnabled = _editor.CanMoveToTop;
        _moveBottom.IsEnabled = _editor.CanMoveToBottom;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _editor.PropertyChanged -= OnEditorChanged;
        _localization.PropertyChanged -= OnLocalizationChanged;
        _list.PreviewMouseLeftButtonDown -= OnMouseDown;
        _list.PreviewMouseMove -= OnMouseMove;
        _list.DragOver -= OnDragOver;
        _list.Drop -= OnDrop;
    }

    private static Brush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
}
