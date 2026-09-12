using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Helldivers2ModManager.Components;

/// <summary>
/// 代码驱动的大型文本下拉选择器。刻意绕开 WPF 的列表机制：
/// <list type="bullet">
/// <item>不使用 <c>ItemsSource</c> / <c>ItemTemplate</c> 数据绑定，也不使用
/// <c>ItemContainerGenerator</c>——条目文本直接写到物化出来的 <see cref="TextBlock"/> 上；</item>
/// <item>列表内容由 <see cref="VirtualizationRange"/> 手动物化，只有可视区（含少量预取）
/// 的行存在，滚动时复用容器，因此展开/滚动的成本与条目总数无关（万级条目同样只渲染十余行）；</item>
/// <item>定位选中项用“按行高换算滚动偏移”，绝不使用 <c>BringIntoView</c>——后者在 Item
/// 滚动单位下会从当前位置线性生成沿途所有容器，是历史卡死的根因。</item>
/// </list>
/// 选中项由 <see cref="SelectionChanged"/> 以<b>源列表索引</b>回传（-1 表示清空），
/// 宿主在代码层更新自己的数据，不依赖任何双向绑定。
/// </summary>
internal partial class VirtualizedTextPicker : UserControl
{
    /// <summary>固定行高（DIP）：可视行区间可直接由滚动偏移除以行高算出。</summary>
    private const double RowHeight = 32d;
    /// <summary>可视区上下额外物化的行数，滚动时留出缓冲避免边缘闪白。</summary>
    private const int OverscanRows = 3;
    /// <summary>下拉列表可视高度（DIP）。</summary>
    private const double ListHeight = 300d;
    /// <summary>翻页键一次移动的行数。</summary>
    private const int PageRows = 10;
    /// <summary>下拉面板最小宽度（DIP），避免条目短时面板过窄。</summary>
    private const double MinimumDropDownWidth = 260d;

    private static readonly Brush TransparentBrush = Brushes.Transparent;

    private readonly Brush _rowForeground;
    private readonly Brush _secondaryForeground;
    private readonly Brush _hoverBrush;
    private readonly Brush _selectedBrush;
    /// <summary>回收池：滚动时复用行容器，稳态下容器总数不超过可视行数 + 预取。</summary>
    private readonly List<Border> _rowPool = [];
    private readonly SortedDictionary<int, Border> _liveRows = [];

    private string[] _texts = [];
    private string[] _searchKeys = [];
    private int[] _identity = [];
    private int[] _visible = [];
    private int _visibleCount;
    private int _selectedSourceIndex = -1;
    private int _highlightVisibleIndex = -1;
    private double _rowWidth;
    private bool _suppressSearchTextChanged;
    private bool _isRefreshingRows;
    private string _placeholder = string.Empty;
    private string _searchHint = string.Empty;
    private string _emptyText = string.Empty;

    public VirtualizedTextPicker()
    {
        InitializeComponent();
        _rowForeground = HeaderText.Foreground;
        _secondaryForeground = SearchHint.Foreground;
        _hoverBrush = TryFindResource("NeutralBackgroundFillBrush") as Brush
                      ?? new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80));
        _selectedBrush = TryFindResource("SystemAccentBrush") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        DropDownPopup.PlacementTarget = HeaderButton;
        ListScroll.Height = ListHeight;
        UpdateHeader();
        UpdateCountText();
        UpdateSearchHintVisibility();
    }

    /// <summary>用户确认选择时触发，参数为<b>源列表</b>索引（-1 表示清空）。</summary>
    internal event EventHandler<int>? SelectionChanged;

    /// <summary>当前已物化的行容器数量（诊断用；恒为可视行数量级）。</summary>
    internal int MaterializedRowCount => _liveRows.Count;

    /// <summary>通过过滤条件后可见的条目数量（诊断用）。</summary>
    internal int VisibleItemCount => _visibleCount;

    /// <summary>未选中任何条目时表头显示的提示文本。</summary>
    internal void SetPlaceholder(string? text)
    {
        _placeholder = text ?? string.Empty;
        UpdateHeader();
    }

    /// <summary>搜索框为空时显示的提示文本。</summary>
    internal void SetSearchHint(string? text)
    {
        _searchHint = text ?? string.Empty;
        UpdateSearchHintVisibility();
    }

    /// <summary>过滤结果为空时列表底部显示的提示文本。</summary>
    internal void SetEmptyText(string? text)
    {
        _emptyText = text ?? string.Empty;
        UpdateCountText();
    }

    /// <summary>
    /// 整体替换条目（源列表顺序）。<paramref name="selectedIndex"/> 为源索引，-1 表示无选中。
    /// 该调用只重建文本索引，不创建任何行容器——行容器在需要时按可视区间惰性物化。
    /// </summary>
    internal void SetItems(IReadOnlyList<string>? items, int selectedIndex)
    {
        // 换了模组就丢掉上一份过滤条件，否则旧的搜索词会把新列表整片过滤掉。
        ResetSearch();

        var count = items?.Count ?? 0;
        if (items is null || count == 0)
        {
            _texts = [];
            _searchKeys = [];
            _identity = [];
        }
        else
        {
            _texts = items as string[] ?? [.. items];
            _searchKeys = new string[_texts.Length];
            for (var index = 0; index < _texts.Length; index++)
                _searchKeys[index] = _texts[index].ToLowerInvariant();
            _identity = BuildIdentity(_texts.Length);
        }

        _selectedSourceIndex = selectedIndex >= 0 && selectedIndex < _texts.Length ? selectedIndex : -1;
        ApplyFilter(resetScroll: true);
        UpdateHeader();
    }

    /// <summary>同步宿主侧的选择（源索引，-1 表示无选中），不触发 <see cref="SelectionChanged"/>。</summary>
    internal void SetSelectedIndex(int selectedIndex)
    {
        var clamped = selectedIndex >= 0 && selectedIndex < _texts.Length ? selectedIndex : -1;
        if (clamped == _selectedSourceIndex)
            return;
        _selectedSourceIndex = clamped;
        UpdateHeader();
        RefreshVisibleRows();
    }

    private void HeaderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HeaderButton.IsChecked == true)
        {
            ResetSearch();
            ApplyFilter(resetScroll: true);
            HeaderButton.IsChecked = true;
            DropDownPopup.IsOpen = true;
        }
        else
        {
            DropDownPopup.IsOpen = false;
        }
    }

    private void DropDownPopup_OnOpened(object sender, EventArgs e)
    {
        DropDownBorder.MinWidth = Math.Max(HeaderButton.ActualWidth, MinimumDropDownWidth);
        ListScroll.UpdateLayout();
        _highlightVisibleIndex = VisibleIndexOfSelected();
        if (_highlightVisibleIndex < 0 && _visibleCount > 0)
            _highlightVisibleIndex = 0;
        // 选中项居中：直接设置滚动偏移（O(1)），避免 BringIntoView 的线性生成。
        if (_highlightVisibleIndex >= 0)
        {
            ListScroll.ScrollToVerticalOffset(Math.Max(
                0d,
                _highlightVisibleIndex * RowHeight - ListHeight / 2d + RowHeight / 2d));
        }

        RefreshVisibleRows();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(SearchBox)));
    }

    private void DropDownPopup_OnClosed(object sender, EventArgs e)
    {
        HeaderButton.IsChecked = false;
        _highlightVisibleIndex = -1;
        RefreshVisibleRows();
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchHintVisibility();
        if (_suppressSearchTextChanged)
            return;
        ApplyFilter(resetScroll: true);
    }

    private void SearchBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveHighlight(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveHighlight(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveHighlight(PageRows);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveHighlight(-PageRows);
                e.Handled = true;
                break;
            case Key.Home:
                SetHighlight(0, ensureVisible: true);
                e.Handled = true;
                break;
            case Key.End:
                SetHighlight(_visibleCount - 1, ensureVisible: true);
                e.Handled = true;
                break;
            case Key.Enter:
                CommitHighlighted();
                e.Handled = true;
                break;
            case Key.Escape:
                DropDownPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void ListScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0d && e.ViewportHeightChange == 0d && e.ExtentHeightChange == 0d &&
            e.ViewportWidthChange == 0d)
        {
            return;
        }

        RefreshVisibleRows();
    }

    private void ListScroll_OnSizeChanged(object sender, SizeChangedEventArgs e) => RefreshVisibleRows();

    private void RowHost_OnMouseMove(object sender, MouseEventArgs e)
    {
        var index = VisibleIndexAt(e.GetPosition(RowHost));
        // 鼠标移动只更新高亮，不自动滚动（否则滚动会改变光标下的行，形成抖动）。
        SetHighlight(index, ensureVisible: false);
    }

    private void RowHost_OnMouseLeave(object sender, MouseEventArgs e) =>
        SetHighlight(-1, ensureVisible: false);

    private void RowHost_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var index = VisibleIndexAt(e.GetPosition(RowHost));
        if (index >= 0)
            SelectVisibleIndex(index);
        e.Handled = true;
    }

    private void ResetSearch()
    {
        if (SearchBox.Text.Length == 0)
            return;
        _suppressSearchTextChanged = true;
        try
        {
            SearchBox.Text = string.Empty;
        }
        finally
        {
            _suppressSearchTextChanged = false;
        }
        UpdateSearchHintVisibility();
    }

    private void ApplyFilter(bool resetScroll)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            if (_identity.Length != _texts.Length)
                _identity = BuildIdentity(_texts.Length);
            _visible = _identity;
        }
        else
        {
            var matches = new List<int>();
            var keys = _searchKeys;
            for (var index = 0; index < keys.Length; index++)
            {
                if (keys[index].Contains(query, StringComparison.Ordinal))
                    matches.Add(index);
            }
            _visible = [.. matches];
        }

        _visibleCount = _visible.Length;
        var selectedVisible = VisibleIndexOfSelected();
        _highlightVisibleIndex = _visibleCount == 0
            ? -1
            : (selectedVisible >= 0 ? selectedVisible : 0);

        if (resetScroll)
            ListScroll.ScrollToVerticalOffset(0d);

        UpdateCountText();
        RefreshVisibleRows();
    }

    /// <summary>
    /// 物化/复用可视行。成本只取决于视口高度（十几行），与条目总数无关。
    /// </summary>
    private void RefreshVisibleRows()
    {
        if (_isRefreshingRows)
            return;

        _isRefreshingRows = true;
        try
        {
            RowHost.Height = _visibleCount * RowHeight;
            _rowWidth = ResolveRowWidth();

            var range = VirtualizationRange.Compute(
                ListScroll.VerticalOffset,
                ListScroll.ViewportHeight,
                RowHeight,
                _visibleCount,
                OverscanRows);

            RecycleRowsOutside(range);

            for (var index = range.First; index <= range.Last; index++)
            {
                var row = RentRow(index);
                var sourceIndex = _visible[index];
                ((TextBlock)row.Child!).Text = _texts[sourceIndex];
                ((TextBlock)row.Child!).Foreground = _rowForeground;
                row.Width = _rowWidth;
                row.Background = sourceIndex == _selectedSourceIndex
                    ? _selectedBrush
                    : (index == _highlightVisibleIndex ? _hoverBrush : TransparentBrush);
                Canvas.SetTop(row, index * RowHeight);
            }
        }
        finally
        {
            _isRefreshingRows = false;
        }
    }

    private void RecycleRowsOutside(VirtualizationRange range)
    {
        if (_liveRows.Count == 0)
            return;

        List<int>? stale = null;
        foreach (var index in _liveRows.Keys)
        {
            if (index < range.First || index > range.Last)
                (stale ??= []).Add(index);
        }
        if (stale is null)
            return;

        foreach (var index in stale)
        {
            var row = _liveRows[index];
            _liveRows.Remove(index);
            row.Visibility = Visibility.Collapsed;
            _rowPool.Add(row);
        }
    }

    private Border RentRow(int index)
    {
        if (_liveRows.TryGetValue(index, out var existing))
            return existing;

        Border row;
        if (_rowPool.Count > 0)
        {
            row = _rowPool[^1];
            _rowPool.RemoveAt(_rowPool.Count - 1);
        }
        else
        {
            row = CreateRow();
        }

        row.Visibility = Visibility.Visible;
        _liveRows[index] = row;
        return row;
    }

    private Border CreateRow()
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
            Foreground = _rowForeground,
            Margin = new Thickness(10, 0, 10, 0)
        };
        var row = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(2),
            Background = TransparentBrush,
            // 行本身不参与命中测试：整片区域统一由 RowHost 按 Y 坐标换算行号。
            IsHitTestVisible = false,
            Child = text
        };
        RowHost.Children.Add(row);
        return row;
    }

    private void SetHighlight(int visibleIndex, bool ensureVisible)
    {
        if (_visibleCount == 0)
            visibleIndex = -1;
        else if (visibleIndex >= _visibleCount)
            visibleIndex = _visibleCount - 1;
        else if (visibleIndex < -1)
            visibleIndex = -1;

        if (visibleIndex == _highlightVisibleIndex)
            return;
        _highlightVisibleIndex = visibleIndex;
        if (ensureVisible)
            ScrollHighlightIntoView();
        RefreshVisibleRows();
    }

    private void MoveHighlight(int delta)
    {
        if (_visibleCount == 0)
            return;
        if (_highlightVisibleIndex < 0)
        {
            SetHighlight(0, ensureVisible: true);
            return;
        }
        SetHighlight(Math.Clamp(_highlightVisibleIndex + delta, 0, _visibleCount - 1), ensureVisible: true);
    }

    /// <summary>按键导航时把高亮行滚入视野；offset 直接按行高换算，O(1)。</summary>
    private void ScrollHighlightIntoView()
    {
        if (_highlightVisibleIndex < 0)
            return;
        var top = _highlightVisibleIndex * RowHeight;
        var offset = ListScroll.VerticalOffset;
        var viewport = ListScroll.ViewportHeight > 1d ? ListScroll.ViewportHeight : ListHeight;
        if (top < offset)
            ListScroll.ScrollToVerticalOffset(top);
        else if (top + RowHeight > offset + viewport)
            ListScroll.ScrollToVerticalOffset(top + RowHeight - viewport);
    }

    private void CommitHighlighted()
    {
        if (_highlightVisibleIndex >= 0 && _highlightVisibleIndex < _visibleCount)
        {
            SelectVisibleIndex(_highlightVisibleIndex);
            return;
        }
        DropDownPopup.IsOpen = false;
    }

    private void SelectVisibleIndex(int visibleIndex)
    {
        var sourceIndex = _visible[visibleIndex];
        _selectedSourceIndex = sourceIndex;
        UpdateHeader();
        DropDownPopup.IsOpen = false;
        SelectionChanged?.Invoke(this, sourceIndex);
    }

    private int VisibleIndexAt(Point point)
    {
        if (_visibleCount == 0)
            return -1;
        var index = (int)(point.Y / RowHeight);
        return index >= 0 && index < _visibleCount ? index : -1;
    }

    private int VisibleIndexOfSelected()
    {
        if (_selectedSourceIndex < 0)
            return -1;
        var visible = _visible;
        for (var index = 0; index < visible.Length; index++)
        {
            if (visible[index] == _selectedSourceIndex)
                return index;
        }
        return -1;
    }

    private double ResolveRowWidth()
    {
        var width = ListScroll.ViewportWidth;
        if (!double.IsFinite(width) || width <= 1d)
        {
            width = ListScroll.ActualWidth - (ListScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
                ? SystemParameters.VerticalScrollBarWidth
                : 0d);
        }
        return Math.Max(0d, width);
    }

    private void UpdateHeader()
    {
        var hasSelection = _selectedSourceIndex >= 0 && _selectedSourceIndex < _texts.Length;
        HeaderText.Text = hasSelection ? _texts[_selectedSourceIndex] : _placeholder;
        HeaderText.Foreground = hasSelection ? _rowForeground : _secondaryForeground;
        HeaderButton.ToolTip = hasSelection ? _texts[_selectedSourceIndex] : null;
    }

    private void UpdateSearchHintVisibility()
    {
        SearchHint.Text = _searchHint;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCountText() =>
        CountText.Text = _visibleCount == 0
            ? (_texts.Length == 0 || string.IsNullOrEmpty(_emptyText) ? string.Empty : _emptyText)
            : $"{_visibleCount} / {_texts.Length}";

    private static int[] BuildIdentity(int count)
    {
        var identity = new int[count];
        for (var index = 0; index < count; index++)
            identity[index] = index;
        return identity;
    }
}
