using System.Runtime.CompilerServices;
using Helldivers2ModManager.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// Dashboard 多选逻辑测试：范围选择（Shift+单击）、反选。
/// 核心逻辑抽为 DashboardPageViewModel 的静态纯方法，用未初始化对象绕过
/// ModViewModel 的重型构造函数（IsSelected 为 ObservableProperty，无订阅者时安全）。
/// </summary>
[TestClass]
public sealed class MultiSelectTests
{
    private static ModViewModel CreateMod() =>
        (ModViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ModViewModel));

    // ========== ApplyRangeSelection：Shift+单击范围选择 ==========

    [TestMethod]
    public void ApplyRangeSelection_ForwardRange_SelectsAllBetween()
    {
        var mods = new[] { CreateMod(), CreateMod(), CreateMod(), CreateMod(), CreateMod() };

        DashboardPageViewModel.ApplyRangeSelection(mods, mods[0], mods[3], additive: false);

        Assert.IsTrue(mods[0].IsSelected && mods[1].IsSelected && mods[2].IsSelected && mods[3].IsSelected,
            "锚点到目标之间的所有项都应选中");
        Assert.IsFalse(mods[4].IsSelected);
    }

    [TestMethod]
    public void ApplyRangeSelection_ReverseRange_SelectsAllBetween()
    {
        var mods = new[] { CreateMod(), CreateMod(), CreateMod(), CreateMod() };

        DashboardPageViewModel.ApplyRangeSelection(mods, mods[3], mods[1], additive: false);

        Assert.IsTrue(mods[1].IsSelected && mods[2].IsSelected && mods[3].IsSelected,
            "反向范围（锚点在目标之后）同样应选中两者之间的所有项");
        Assert.IsFalse(mods[0].IsSelected);
    }

    [TestMethod]
    public void ApplyRangeSelection_NonAdditive_ClearsPriorSelection()
    {
        var mods = new[] { CreateMod(), CreateMod(), CreateMod(), CreateMod(), CreateMod() };
        mods[0].IsSelected = true;
        mods[4].IsSelected = true;

        DashboardPageViewModel.ApplyRangeSelection(mods, mods[1], mods[2], additive: false);

        Assert.IsTrue(mods[1].IsSelected && mods[2].IsSelected);
        Assert.IsFalse(mods[0].IsSelected, "非追加模式应清空原有选择");
        Assert.IsFalse(mods[4].IsSelected, "非追加模式应清空原有选择");
    }

    [TestMethod]
    public void ApplyRangeSelection_Additive_PreservesPriorSelection()
    {
        var mods = new[] { CreateMod(), CreateMod(), CreateMod(), CreateMod(), CreateMod() };
        mods[0].IsSelected = true;
        mods[4].IsSelected = true;

        DashboardPageViewModel.ApplyRangeSelection(mods, mods[1], mods[3], additive: true);

        Assert.IsTrue(mods[0].IsSelected && mods[4].IsSelected, "追加模式（Ctrl+Shift）应保留原有选择");
        Assert.IsTrue(mods[1].IsSelected && mods[2].IsSelected && mods[3].IsSelected);
    }

    [TestMethod]
    public void ApplyRangeSelection_AnchorNotVisible_FallsBackToTarget()
    {
        var visible = new[] { CreateMod(), CreateMod(), CreateMod() };
        var hiddenAnchor = CreateMod();
        var target = visible[2];
        visible[0].IsSelected = true;

        DashboardPageViewModel.ApplyRangeSelection(visible, hiddenAnchor, target, additive: false);

        Assert.IsTrue(target.IsSelected, "锚点不在当前过滤视图时应退化为至少选中目标");
        Assert.IsFalse(visible[0].IsSelected, "非追加模式应清空原有选择");
    }

    [TestMethod]
    public void ApplyRangeSelection_SameAnchorAndTarget_SelectsOnlyOne()
    {
        var mods = new[] { CreateMod(), CreateMod(), CreateMod() };

        DashboardPageViewModel.ApplyRangeSelection(mods, mods[1], mods[1], additive: false);

        Assert.IsTrue(mods[1].IsSelected);
        Assert.IsFalse(mods[0].IsSelected && mods[2].IsSelected);
    }

    // ========== ApplyInvertSelection：反选 ==========

    [TestMethod]
    public void ApplyInvertSelection_FlipsAllStates()
    {
        var mods = new[] { CreateMod(), CreateMod(), CreateMod(), CreateMod() };
        mods[1].IsSelected = true;

        DashboardPageViewModel.ApplyInvertSelection(mods);

        Assert.IsFalse(mods[1].IsSelected);
        Assert.IsTrue(mods[0].IsSelected && mods[2].IsSelected && mods[3].IsSelected);
    }

    [TestMethod]
    public void ApplyInvertSelection_EmptyList_DoesNotThrow()
    {
        DashboardPageViewModel.ApplyInvertSelection([]);
    }
}
