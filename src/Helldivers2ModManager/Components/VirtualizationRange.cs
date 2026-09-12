namespace Helldivers2ModManager.Components;

/// <summary>
/// 手动物化窗口的行区间。<see cref="Compute"/> 是纯函数，与 WPF 无关，便于回归测试
/// 断言“物化行数只取决于视口高度，与条目总数无关”。
/// </summary>
internal readonly record struct VirtualizationRange(int First, int Last)
{
    /// <summary>空区间。</summary>
    public static VirtualizationRange Empty => new(0, -1);

    /// <summary>区间内的行数（空区间为 0）。</summary>
    public int Count => Last < First ? 0 : Last - First + 1;

    /// <summary>
    /// 计算需要物化的行区间。
    /// </summary>
    /// <param name="verticalOffset">当前垂直滚动偏移（DIP）。</param>
    /// <param name="viewportHeight">可视区高度（DIP）；非正时按一行处理。</param>
    /// <param name="rowHeight">固定行高（DIP）；非正时按 1 处理。</param>
    /// <param name="itemCount">条目总数。</param>
    /// <param name="overscan">可视区上下额外物化的行数。</param>
    public static VirtualizationRange Compute(
        double verticalOffset,
        double viewportHeight,
        double rowHeight,
        int itemCount,
        int overscan)
    {
        if (itemCount <= 0)
            return Empty;

        var row = rowHeight > 0d ? rowHeight : 1d;
        var offset = double.IsFinite(verticalOffset) && verticalOffset > 0d ? verticalOffset : 0d;
        var viewport = double.IsFinite(viewportHeight) && viewportHeight > 0d ? viewportHeight : row;
        var overscanRows = Math.Max(0, overscan);

        var firstVisibleRow = (int)Math.Floor(offset / row);
        var lastVisibleRow = (int)Math.Ceiling((offset + viewport) / row) - 1;
        if (lastVisibleRow < firstVisibleRow)
            lastVisibleRow = firstVisibleRow;

        var first = Math.Min(Math.Max(0, firstVisibleRow - overscanRows), itemCount - 1);
        var last = Math.Min(itemCount - 1, Math.Max(lastVisibleRow, first) + overscanRows);
        return new VirtualizationRange(first, last);
    }
}
