using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class VirtualizedAnimationPicker : Grid
{
    private const double RowHeight = 32;
    private const double ListHeight = 300;
    private const int Overscan = 3;
    private readonly Button _header = new();
    private readonly TextBlock _headerText = new();
    private readonly Popup _popup = new();
    private readonly TextBox _search = new();
    private readonly ScrollViewer _scroll = new();
    private readonly Canvas _rows = new();
    private readonly TextBlock _count = new();
    private string[] _items = [];
    private int[] _visible = [];
    private int _selectedIndex = -1;
    private string _placeholder = string.Empty;
    private string _emptyText = string.Empty;
    private bool _refreshing;

    internal event Action<int>? SelectionChanged;
    internal int SelectedIndex => _selectedIndex;
    internal int MaterializedRowCount => _rows.Children.Count;
    internal int VisibleItemCount => _visible.Length;
    internal bool IsOpen => _popup.IsOpen;

    internal VirtualizedAnimationPicker()
    {
        MinWidth = 210;
        MaxWidth = 300;
        _header.Height = 30;
        _header.Padding = new Thickness(8, 4, 8, 4);
        var headerContent = new Grid();
        headerContent.ColumnDefinitions.Add(new ColumnDefinition
        { Width = new GridLength(1, GridUnitType.Star) });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        _headerText.TextTrimming = TextTrimming.CharacterEllipsis;
        _headerText.VerticalAlignment = VerticalAlignment.Center;
        headerContent.Children.Add(_headerText);
        var arrow = new TextBlock { Text = "\uE70D", FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 1);
        headerContent.Children.Add(arrow);
        _header.Content = headerContent;
        _header.Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;
        Children.Add(_header);

        var content = new Grid { Width = 300 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ListHeight) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _search.Margin = new Thickness(6);
        _search.Height = 30;
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += (_, args) =>
        {
            if (args.Key == global::Jalium.UI.Input.Key.Escape)
            {
                _popup.IsOpen = false;
                args.Handled = true;
            }
            else if (args.Key == global::Jalium.UI.Input.Key.Enter && _visible.Length > 0)
            {
                Choose(_visible[0]);
                args.Handled = true;
            }
        };
        content.Children.Add(_search);
        _rows.Width = 296;
        _rows.Background = Brushes.Transparent;
        _scroll.Height = ListHeight;
        _scroll.Content = _rows;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.ScrollChanged += (_, _) => RefreshRows();
        Grid.SetRow(_scroll, 1);
        content.Children.Add(_scroll);
        _count.FontSize = 11;
        _count.Margin = new Thickness(9, 5, 9, 6);
        Grid.SetRow(_count, 2);
        content.Children.Add(_count);
        _popup.Child = new Border
        {
            Child = content,
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x46)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        };
        _popup.PlacementTarget = _header;
        _popup.Placement = PlacementMode.Bottom;
        _popup.StaysOpen = false;
        _popup.Opened += (_, _) =>
        {
            _search.Text = string.Empty;
            Filter();
            var visibleIndex = Array.IndexOf(_visible, _selectedIndex);
            _scroll.ScrollToVerticalOffset(Math.Max(visibleIndex, 0) * RowHeight);
            RefreshRows();
            _search.Focus();
        };
        Children.Add(_popup);
    }

    internal void SetLabels(string placeholder, string searchHint, string emptyText)
    {
        _placeholder = placeholder;
        _emptyText = emptyText;
        _search.ToolTip = searchHint;
        UpdateHeader();
        UpdateCount();
    }

    internal void SetItems(IReadOnlyList<string> items, int selectedIndex = -1)
    {
        _items = items as string[] ?? [.. items];
        _selectedIndex = selectedIndex >= 0 && selectedIndex < _items.Length
            ? selectedIndex : -1;
        _search.Text = string.Empty;
        Filter();
        UpdateHeader();
    }

    internal void SelectIndex(int index)
    {
        Choose(index);
    }

    internal void Open() => _popup.IsOpen = true;

    internal void Search(string query) => _search.Text = query;

    internal void ScrollToItem(int visibleIndex)
    {
        _scroll.ScrollToVerticalOffset(visibleIndex * RowHeight);
        RefreshRows();
    }

    private void Choose(int index)
    {
        if (index < -1 || index >= _items.Length)
            return;
        var changed = _selectedIndex != index;
        _selectedIndex = index;
        _popup.IsOpen = false;
        UpdateHeader();
        if (changed)
            SelectionChanged?.Invoke(index);
    }

    private void UpdateHeader() => _headerText.Text = _selectedIndex >= 0
        ? _items[_selectedIndex] : _placeholder;

    private void Filter()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        _visible = string.IsNullOrEmpty(query)
            ? Enumerable.Range(0, _items.Length).ToArray()
            : Enumerable.Range(0, _items.Length)
                .Where(index => _items[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        _rows.Height = _visible.Length * RowHeight;
        _scroll.ScrollToVerticalOffset(0);
        UpdateCount();
        RefreshRows();
    }

    private void UpdateCount() => _count.Text = _visible.Length == 0
        ? _emptyText : $"{_visible.Length:N0} / {_items.Length:N0}";

    private void RefreshRows()
    {
        if (_refreshing || !_popup.IsOpen)
            return;
        _refreshing = true;
        try
        {
            _rows.Children.Clear();
            var first = Math.Max(0, (int)(_scroll.VerticalOffset / RowHeight) - Overscan);
            var last = Math.Min(_visible.Length,
                (int)Math.Ceiling((_scroll.VerticalOffset + ListHeight) / RowHeight) + Overscan);
            for (var index = first; index < last; index++)
            {
                var sourceIndex = _visible[index];
                var row = new Button
                {
                    Content = _items[sourceIndex],
                    Width = 294, Height = RowHeight,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(9, 0, 9, 0),
                    ToolTip = _items[sourceIndex],
                };
                row.Click += (_, _) => Choose(sourceIndex);
                Canvas.SetTop(row, index * RowHeight);
                _rows.Children.Add(row);
            }
        }
        finally { _refreshing = false; }
    }
}
