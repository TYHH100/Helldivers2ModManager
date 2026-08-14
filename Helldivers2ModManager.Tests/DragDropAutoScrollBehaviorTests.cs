using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Interop;
using GongSolutions.Wpf.DragDrop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using GongDragDrop = GongSolutions.Wpf.DragDrop.DragDrop;
using WpfDragDrop = System.Windows.DragDrop;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class DragDropAutoScrollBehaviorTests
{
    // ========== FindScrollViewer：祖先与后代查找 ==========

    [STATestMethod]
    public void FindScrollViewer_ItemsControlInsideScrollViewer_ReturnsAncestor()
    {
        var itemsControl = new ItemsControl();
        var scrollViewer = CreateScrollViewerWithContent(itemsControl);
        scrollViewer.Measure(new Size(200, 300));
        scrollViewer.Arrange(new Rect(0, 0, 200, 300));

        var found = (ScrollViewer?)typeof(DragDropAutoScrollBehavior)
            .GetMethod("FindScrollViewer", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [itemsControl]);

        Assert.AreSame(scrollViewer, found);
    }

    [STATestMethod]
    public void FindScrollViewer_ListBoxWithInternalScrollViewer_ReturnsDescendant()
    {
        var listBox = CreateListBoxWithScrollViewerTemplate();
        listBox.Measure(new Size(200, 300));
        listBox.Arrange(new Rect(0, 0, 200, 300));

        var expected = VisualTreeHelper.GetChild(listBox, 0) as ScrollViewer;
        Assert.IsNotNull(expected, "前置条件：模板根节点应为 ScrollViewer");

        var found = (ScrollViewer?)typeof(DragDropAutoScrollBehavior)
            .GetMethod("FindScrollViewer", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [listBox]);

        Assert.AreSame(expected, found, "ListBox 的 ScrollViewer 是视觉后代，必须通过后代查找命中");
    }

    // ========== 拖拽开始/结束的钩子生命周期 ==========

    [STATestMethod]
    public void DragOverThenDrop_InstallsAndUninstallsWheelHook()
    {
        var itemsControl = new ItemsControl();
        CreateScrollViewerWithContent(itemsControl);
        DragDropAutoScrollBehavior.SetIsEnabled(itemsControl, true);

        var hookHandleField = typeof(DragDropAutoScrollBehavior)
            .GetField("s_wheelHookHandle", BindingFlags.NonPublic | BindingFlags.Static)!;

        // 模拟拖拽进入：行为会激活状态、查找 ScrollViewer 并安装滚轮钩子
        RaiseDragEvent(itemsControl, WpfDragDrop.PreviewDragOverEvent, "payload");

        Assert.IsTrue((IntPtr)hookHandleField.GetValue(null)! != IntPtr.Zero,
            "拖拽进入后应安装 WH_MOUSE_LL 钩子");

        // 模拟放下：行为应卸载钩子
        RaiseDragEvent(itemsControl, WpfDragDrop.DropEvent, "payload");

        Assert.IsTrue((IntPtr)hookHandleField.GetValue(null)! == IntPtr.Zero,
            "拖拽结束后应卸载 WH_MOUSE_LL 钩子");

        DragDropAutoScrollBehavior.SetIsEnabled(itemsControl, false);
    }

    [STATestMethod]
    public void DeactivateLastActiveState_UninstallsWheelHook()
    {
        var itemsControl = new ItemsControl();
        CreateScrollViewerWithContent(itemsControl);
        DragDropAutoScrollBehavior.SetIsEnabled(itemsControl, true);

        var hookHandleField = typeof(DragDropAutoScrollBehavior)
            .GetField("s_wheelHookHandle", BindingFlags.NonPublic | BindingFlags.Static)!;
        var statesField = typeof(DragDropAutoScrollBehavior)
            .GetField("s_states", BindingFlags.NonPublic | BindingFlags.Static)!;

        RaiseDragEvent(itemsControl, WpfDragDrop.PreviewDragOverEvent, "payload");
        Assert.IsTrue((IntPtr)hookHandleField.GetValue(null)! != IntPtr.Zero);

        // 不触发 Drop，直接停用最后一个活动状态（等价于看门狗/渲染回调的清理路径）
        var states = (System.Collections.IDictionary)statesField.GetValue(null)!;
        var state = states.Values.Cast<object>().Single();
        typeof(DragDropAutoScrollBehavior)
            .GetMethod("Deactivate", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [state]);

        Assert.IsTrue((IntPtr)hookHandleField.GetValue(null)! == IntPtr.Zero,
            "所有状态停用后应卸载 WH_MOUSE_LL 钩子");

        DragDropAutoScrollBehavior.SetIsEnabled(itemsControl, false);
    }

    // ========== 合成指示线刷新依赖的内部构造函数 ==========

    [TestMethod]
    public void DragEventArgsInternalConstructor_Resolvable()
    {
        // RefreshDropIndicator 依赖 DragEventArgs 的 internal 构造函数，
        // WPF 升级时若将其移除或改签名，此测试必须失败。
        var ctor = typeof(DragEventArgs).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null);

        Assert.IsNotNull(ctor);
        var args = (DragEventArgs)ctor.Invoke(
            [new DataObject("x", "y"), DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, new DependencyObject(), new Point(1, 2)]);
        Assert.IsNotNull(args.Data);
    }

    // ========== 合成 PreviewDragOver 会重建插入位置 ==========

    [STATestMethod]
    public void RefreshDropIndicator_RebuildsDropInfoForItemUnderCursor()
    {
        // 用隐藏的 HwndSource 提供 PresentationSource（PointFromScreen 需要）
        var parameters = new HwndSourceParameters("DragDropAutoScrollBehaviorTests")
        {
            Width = 300,
            Height = 500,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000) // WS_POPUP
        };
        using (var source = new HwndSource(parameters))
        {
            var itemsControl = new ItemsControl
            {
                ItemsSource = new ObservableCollection<string> { "A", "B", "C", "D" }
            };
            GongDragDrop.SetIsDropTarget(itemsControl, true);
            var handler = new RecordingDropHandler();
            GongDragDrop.SetDropHandler(itemsControl, handler);
            source.RootVisual = itemsControl;
            itemsControl.Measure(new Size(300, 500));
            itemsControl.Arrange(new Rect(0, 0, 300, 500));
            itemsControl.UpdateLayout();

            var state = CreateState(itemsControl);
            SetStateValue(state, "LastDragData", new DataObject("x", "y"));

            // 光标位于 Y=60 处（第 3 项 "C" 附近）
            var screenPoint = itemsControl.PointToScreen(new Point(100, 60));
            InvokeRefresh(state, screenPoint);

            Assert.IsTrue(handler.DragOverCount > 0, "合成 PreviewDragOver 应触发 gong 管线并调用 DropHandler.DragOver");
            Assert.IsNotNull(handler.LastDropInfo, "应携带重建后的 DropInfo");
            Assert.IsTrue(handler.LastDropInfo!.InsertIndex >= 0);
        }
    }

    // ── 辅助 ──

    /// <summary>MSTest 环境不加载默认主题，用显式模板构造 ScrollViewer(ContentPresenter) → 内容的视觉链</summary>
    private static ScrollViewer CreateScrollViewerWithContent(ItemsControl itemsControl)
    {
        var scrollViewer = new ScrollViewer { Content = itemsControl };
        var contentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenterFactory.SetBinding(ContentPresenter.ContentProperty,
            new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        scrollViewer.Template = new ControlTemplate(typeof(ScrollViewer)) { VisualTree = contentPresenterFactory };
        scrollViewer.ApplyTemplate();
        scrollViewer.Measure(new Size(200, 300));
        scrollViewer.Arrange(new Rect(0, 0, 200, 300));
        return scrollViewer;
    }

    /// <summary>构造带内部 ScrollViewer 部件的 ListBox（模拟真实 ListBox 模板结构）</summary>
    private static ListBox CreateListBoxWithScrollViewerTemplate()
    {
        var scrollViewerFactory = new FrameworkElementFactory(typeof(ScrollViewer));
        scrollViewerFactory.SetValue(FrameworkElement.NameProperty, "ScrollViewer");
        scrollViewerFactory.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        var template = new ControlTemplate(typeof(ListBox)) { VisualTree = scrollViewerFactory };
        var listBox = new ListBox
        {
            Template = template,
            ItemsSource = new[] { "A", "B", "C" }
        };
        listBox.ApplyTemplate();
        return listBox;
    }

    private static void RaiseDragEvent(ItemsControl itemsControl, RoutedEvent routedEvent, string payload)
    {
        var ctor = typeof(DragEventArgs).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
        var args = (DragEventArgs)ctor.Invoke(
            [new DataObject("test", payload), DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, itemsControl, new Point(0, 0)]);
        args.RoutedEvent = routedEvent;
        itemsControl.RaiseEvent(args);
    }

    private static object CreateState(ItemsControl itemsControl)
    {
        var stateType = typeof(DragDropAutoScrollBehavior).GetNestedType("AutoScrollState", BindingFlags.NonPublic)!;
        var ctor = stateType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(ItemsControl)], null)!;
        Assert.IsNotNull(ctor, "AutoScrollState 应包含 ItemsControl 构造函数");
        return ctor.Invoke([itemsControl]);
    }

    private static void SetStateValue(object state, string memberName, object? value)
    {
        var type = state.GetType();
        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is not null)
        {
            field.SetValue(state, value);
            return;
        }
        type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(state, value);
    }

    private static void InvokeRefresh(object state, Point point)
    {
        typeof(DragDropAutoScrollBehavior)
            .GetMethod("RefreshDropIndicator", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [state, point]);
    }

    private sealed class RecordingDropHandler : IDropTarget
    {
        public int DragOverCount { get; private set; }

        public IDropInfo? LastDropInfo { get; private set; }

        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            DragOverCount++;
            LastDropInfo = dropInfo;
        }

        void IDropTarget.Drop(IDropInfo dropInfo)
        {
        }
    }
}
