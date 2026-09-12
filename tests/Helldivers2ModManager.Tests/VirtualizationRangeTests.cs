using Helldivers2ModManager.Components;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 下拉列表的手动物化窗口：物化行数只取决于视口高度，与条目总数无关——这是动画列表
/// 从 256 条放宽到全量（未来可能上万）后仍能保持流畅的前提。
/// </summary>
[TestClass]
public sealed class VirtualizationRangeTests
{
    private const double RowHeight = 32d;
    private const double ViewportHeight = 300d;
    private const int Overscan = 3;
    /// <summary>视口 300 / 行高 32 时合理物化行数的上界（可见 ~10 行 + 上下预取）。</summary>
    private const int MaxExpectedRows = 32;

    [TestMethod]
    public void Compute_MaterializedRowsDoNotGrowWithItemCount()
    {
        var small = VirtualizationRange.Compute(0d, ViewportHeight, RowHeight, 256, Overscan);
        var huge = VirtualizationRange.Compute(0d, ViewportHeight, RowHeight, 1_000_000, Overscan);

        Assert.AreEqual(small.Count, huge.Count,
            "256 条与 100 万条必须物化同样多的行，否则虚拟化没有生效。");
        Assert.IsTrue(huge.Count <= MaxExpectedRows,
            $"视口 300 / 行高 32 只应物化十余行，实测 {huge.Count}。");
    }

    [TestMethod]
    public void Compute_AtAnyScrollOffsetStaysInBoundsAndBounded()
    {
        const int itemCount = 100_000;
        for (var step = 0; step <= 64; step++)
        {
            var offset = step * (itemCount * RowHeight - ViewportHeight) / 64d;
            var range = VirtualizationRange.Compute(offset, ViewportHeight, RowHeight, itemCount, Overscan);

            Assert.IsTrue(range.First >= 0, $"offset={offset} 的首行越界。");
            Assert.IsTrue(range.Last < itemCount, $"offset={offset} 的末行越界。");
            Assert.IsTrue(range.Last >= range.First, $"offset={offset} 的区间为空。");
            Assert.IsTrue(range.Count <= MaxExpectedRows, $"offset={offset} 物化了 {range.Count} 行。");
        }
    }

    [TestMethod]
    public void Compute_TopAndBottomCoverTheEdgesOfTheList()
    {
        const int itemCount = 5_000;

        var top = VirtualizationRange.Compute(0d, ViewportHeight, RowHeight, itemCount, Overscan);
        Assert.AreEqual(0, top.First, "滚到顶部时首行必须是第 0 行。");

        var maxOffset = itemCount * RowHeight - ViewportHeight;
        var bottom = VirtualizationRange.Compute(maxOffset, ViewportHeight, RowHeight, itemCount, Overscan);
        Assert.AreEqual(itemCount - 1, bottom.Last, "滚到底部时最后一行必须落在物化区间内。");
    }

    [TestMethod]
    public void Compute_DegenerateInputsFallBackToOneRow()
    {
        Assert.AreEqual(0, VirtualizationRange.Compute(0d, ViewportHeight, RowHeight, 0, Overscan).Count,
            "空列表不物化任何行。");
        Assert.AreEqual(0, VirtualizationRange.Compute(0d, ViewportHeight, RowHeight, -5, Overscan).Count,
            "负条目数不物化任何行（防御性）。");

        // 视口尚未测量（0）时按一行计算，避免布局阶段整段列表被物化。
        var unmeasured = VirtualizationRange.Compute(0d, 0d, RowHeight, 100, Overscan);
        Assert.AreEqual(0, unmeasured.First);
        Assert.AreEqual(1 + Overscan, unmeasured.Count);
    }
}
