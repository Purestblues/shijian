using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using ShiJian.Client.Librarys;
using Xunit;

namespace ShiJian.Tests;

/// <summary>
/// 换页进退场动效的验收线：区分 Replace / Forward / Back 方向、能播（真的花时间）、
/// 播完一定回到终态、快速连击可取消、关掉动效时零延迟直切。
/// </summary>
public class PageMotionTests
{
    private static (Control Page, Window Window) CreateHost()
    {
        var page = new Border { Opacity = 1 };
        var window = new Window { Width = 300, Height = 200, Content = page };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (page, window);
    }

    [Fact]
    public void DirectionOffsets_MatchNavigationSemantics()
    {
        Assert.Equal((PageMotion.PageEnterOffsetX, 0d), PageMotion.GetEnterOffset(PageTransitionDirection.Forward));
        Assert.Equal((-PageMotion.PageEnterOffsetX, 0d), PageMotion.GetEnterOffset(PageTransitionDirection.Back));
        Assert.Equal((0d, MotionTokens.PageOffsetY), PageMotion.GetEnterOffset(PageTransitionDirection.Replace));

        Assert.Equal((-PageMotion.PageLeaveOffsetX, 0d), PageMotion.GetLeaveOffset(PageTransitionDirection.Forward));
        Assert.Equal((PageMotion.PageLeaveOffsetX, 0d), PageMotion.GetLeaveOffset(PageTransitionDirection.Back));
        Assert.Equal((0d, 0d), PageMotion.GetLeaveOffset(PageTransitionDirection.Replace));
    }

    [AvaloniaFact]
    public async Task PlayEnter_WhenMotionDisabled_SnapsWithNoDelay()
    {
        var (page, window) = CreateHost();

        try
        {
            Motion.SetEnabled(false);

            var watch = Stopwatch.StartNew();
            await PageMotion.PlayEnterAsync(page, PageTransitionDirection.Forward);
            watch.Stop();

            Assert.Equal(1d, page.Opacity);
            Assert.Null(page.RenderTransform);
            Assert.True(watch.ElapsedMilliseconds < 100, $"关闭动效后仍耗时 {watch.ElapsedMilliseconds}ms");
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PlayEnter_StartsInvisible_ReallyAnimates_AndEndsVisible()
    {
        var (page, window) = CreateHost();

        try
        {
            Motion.SetEnabled(true);

            PageMotion.Prepare(page);
            Assert.Equal(0d, page.Opacity);

            var watch = Stopwatch.StartNew();
            await PageMotion.PlayEnterAsync(page);
            watch.Stop();

            var minimum = PageMotion.EnterDuration.TotalMilliseconds * 0.6;
            Assert.True(watch.ElapsedMilliseconds >= minimum,
                $"进场动画只用了 {watch.ElapsedMilliseconds}ms（应 >= {minimum:F0}ms），动画可能被跳过");

            Assert.Equal(1d, page.Opacity);
            Assert.Null(page.RenderTransform);
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PlayEnterAndExit_WithForwardAndBackDirections_CompleteCleanlyAndCanCancel()
    {
        var (page, window) = CreateHost();

        try
        {
            Motion.SetEnabled(true);

            await PageMotion.PlayEnterAsync(page, PageTransitionDirection.Forward);
            Assert.Equal(1d, page.Opacity);
            Assert.Null(page.RenderTransform);

            await PageMotion.PlayExitAsync(page, PageTransitionDirection.Back);
            Assert.Equal(0d, page.Opacity);
            var tt = Assert.IsType<TranslateTransform>(page.RenderTransform);
            Assert.Equal(PageMotion.PageLeaveOffsetX, tt.X, 1);

            using var cts = new CancellationTokenSource();
            var canceledTask = PageMotion.PlayEnterAsync(page, PageTransitionDirection.Back, cts.Token);
            cts.Cancel();
            await canceledTask;
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PlayEnter_ResetsStateLeftBehindByPreviousRun()
    {
        var (page, window) = CreateHost();

        try
        {
            Motion.SetEnabled(false);

            page.Opacity = 0.3;

            await PageMotion.PlayEnterAsync(page);

            Assert.Equal(1d, page.Opacity);
            Assert.Null(page.RenderTransform);
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }
}
