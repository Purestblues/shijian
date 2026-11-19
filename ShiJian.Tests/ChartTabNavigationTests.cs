using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Charts;
using ShiJian.Client.Controls.Timeline;
using Xunit;

namespace ShiJian.Tests;

/// <summary>
/// G11 回归保护：自绘图表必须能被 Tab 走到（键盘用户唯一的入口）。
/// 图表在页面里的真实位置是 PageContainer 的 ScrollViewer 内部，所以这里套一层 ScrollViewer。
/// </summary>
public class ChartTabNavigationTests
{
    private static Window HostWith(Control chart, Button before, Button after)
    {
        var page = new UserControl
        {
            Content = new StackPanel { Children = { before, chart, after } }
        };

        var window = new Window
        {
            Width = 400,
            Height = 400,
            Content = new ScrollViewer { Content = page }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void AssertTabReachable(Control chart)
    {
        var before = new Button { Content = "before" };
        var after = new Button { Content = "after" };
        var window = HostWith(chart, before, after);

        try
        {
            before.Focus();
            Assert.Same(before, window.FocusManager?.GetFocusedElement());

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Same(chart, window.FocusManager?.GetFocusedElement());

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Same(after, window.FocusManager?.GetFocusedElement());

            // 反向也要走得回去
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.Same(chart, window.FocusManager?.GetFocusedElement());
            Assert.True(FocusVisual.ShouldDraw(chart));

            // 鼠标/指针聚焦不应绘制焦点边框（G27）
            before.Focus(NavigationMethod.Pointer);
            chart.Focus(NavigationMethod.Pointer);
            Assert.Same(chart, window.FocusManager?.GetFocusedElement());
            Assert.False(FocusVisual.ShouldDraw(chart));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ColumnChart_IsReachableByTab() => AssertTabReachable(new ColumnChart());

    [AvaloniaFact]
    public void DonutChart_IsReachableByTab() => AssertTabReachable(new DonutChart());

    [AvaloniaFact]
    public void StackedBarChart_IsReachableByTab() => AssertTabReachable(new StackedBarChart());

    [AvaloniaFact]
    public void ClockRing_IsReachableByTab() => AssertTabReachable(new ClockRing());
}
