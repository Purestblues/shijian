using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Librarys;
using ShiJian.Client.Servicers;
using Xunit;
using Expander = ShiJian.Client.Controls.Expander.Expander;

namespace ShiJian.Tests;

/// <summary>
/// G13 回归保护：折叠头必须点哪儿都能展开/收起。
/// 原来的模板只给右侧箭头绑了命令（而且那个命令从来没被赋值），点标题毫无反应。
/// </summary>
public class ExpanderHeaderTests
{
    private static (Expander Expander, Window Window) Create()
    {
        var expander = new Expander
        {
            Header = "规则",
            Width = 320,
            Content = new Border { Height = 40, Background = Brushes.Red }
        };
        var window = new Window
        {
            Width = 400,
            Height = 400,
            Content = new StackPanel { Children = { expander } }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (expander, window);
    }

    private static Border Header(Expander expander) =>
        expander.GetVisualDescendants().OfType<Border>().First(x => x.Name == "HeaderBorder");

    private static Icon Chevron(Expander expander) =>
        expander.GetVisualDescendants().OfType<Icon>().First(x => x.Name == "Icon");

    private static void ClickHeader(Window window, Expander expander)
    {
        // 点在标题文字所在的左侧区域（不是箭头那一列），这才是「点标题」的真实场景
        var point = Header(expander).TranslatePoint(new Point(12, 8), window);
        Assert.NotNull(point);
        window.MouseDown(point!.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ClickingHeaderTitle_TogglesExpanded()
    {
        var (expander, window) = Create();

        try
        {
            Motion.SetEnabled(false);

            Assert.False(expander.IsExpanded, "默认应是收起状态");
            ClickHeader(window, expander);
            Assert.True(expander.IsExpanded, "点标题应展开");

            ClickHeader(window, expander);
            Assert.False(expander.IsExpanded, "再点一次应收起");
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Chevron_FollowsExpandedState()
    {
        var (expander, window) = Create();

        try
        {
            MotionTransitions.Attach(window);
            Motion.SetEnabled(false);
            Dispatcher.UIThread.RunJobs();

            var collapsedMatrix = Assert.IsAssignableFrom<ITransform>(Chevron(expander).RenderTransform).Value;
            Assert.Equal(0d, collapsedMatrix.M11, 2);
            Assert.Equal(-1d, collapsedMatrix.M12, 2);

            ClickHeader(window, expander);
            var expandedMatrix = Assert.IsAssignableFrom<ITransform>(Chevron(expander).RenderTransform).Value;
            Assert.Equal(1d, expandedMatrix.M11, 2);
            Assert.Equal(0d, expandedMatrix.M12, 2);
        }
        finally
        {
            MotionTransitions.Detach();
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    /// <summary>
    /// 新增的 token 必须三套主题（暗色 / 亮色 / Azure）都齐。
    /// 漏一套不会报错，只会静默回落到 Default——「浅色模式卡片全黑」就是这么来的。
    /// </summary>
    [AvaloniaFact]
    public void DesignTokens_ExistInEveryThemeVariant()
    {
        string[] keys =
        [
            "Accent2Color", "Accent2Brush", "ChartCompareBrush",
            "SelectRingBrush", "WebBadgeBgBrush", "WebBadgeFgBrush", "ScrimBrush"
        ];
        ThemeVariant[] variants = [ThemeVariant.Dark, ThemeVariant.Light, ThemeServicer.Azure];

        foreach (var variant in variants)
        {
            foreach (var key in keys)
            {
                Assert.True(Application.Current!.TryGetResource(key, variant, out var value) && value != null,
                    $"主题 {variant} 缺少 token：{key}");
            }
        }
    }
}
