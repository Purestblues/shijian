using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ShiJian.Client.Librarys;
using Xunit;

namespace ShiJian.Tests;

public class SmoothScrollTests
{
    [Fact]
    public void ComputeNextOffset_WheelDecayIsPerceptibleAndConvergesWithinBudget()
    {
        var current = new Vector(0, 0);
        var target = new Vector(0, SmoothScroll.WheelStepPx);
        var snapped = false;

        var firstFrame = SmoothScroll.ComputeNextOffset(current, target, 1.0 / 60.0, SmoothScroll.WheelDecayRate, out _);
        var firstFrameFraction = firstFrame.Y / target.Y;
        // 离散滚轮每帧温和起步（~17.5%），避免单帧骤跳超过 22%
        Assert.InRange(firstFrameFraction, 0.12, 0.22);

        for (var frame = 0; frame < 45; frame++)
        {
            var next = SmoothScroll.ComputeNextOffset(current, target, 1.0 / 60.0, SmoothScroll.WheelDecayRate, out snapped);
            Assert.True(next.Y >= current.Y, "Offset should move monotonically toward target");
            Assert.True(next.Y <= target.Y, "Exponential decay must not overshoot target");
            current = next;
            if (snapped)
            {
                break;
            }
        }

        Assert.True(snapped, "Wheel smooth scroll should snap within ~45 frames at 60Hz");
        Assert.Equal(target, current);
    }

    [Fact]
    public void ComputeWheelStep_DistinguishesPrecisionTrackpadAndBurstAcceleration()
    {
        var precision = SmoothScroll.ComputeWheelStep(new Vector(0, -0.3), 16, new Vector(0, -0.3), out var precisionDecay);
        Assert.Equal(SmoothScroll.PrecisionDecayRate, precisionDecay);
        Assert.Equal(0.3 * SmoothScroll.WheelStepPx, precision.Y, 3);

        var singleWheel = SmoothScroll.ComputeWheelStep(new Vector(0, -1.0), 300, Vector.Zero, out var wheelDecay);
        Assert.Equal(SmoothScroll.WheelDecayRate, wheelDecay);
        Assert.Equal(SmoothScroll.WheelStepPx, singleWheel.Y, 3);

        var burstWheel = SmoothScroll.ComputeWheelStep(new Vector(0, -1.0), 40, new Vector(0, -1.0), out var burstDecay);
        Assert.Equal(SmoothScroll.WheelDecayRate, burstDecay);
        Assert.True(burstWheel.Y > SmoothScroll.WheelStepPx);
        Assert.True(burstWheel.Y <= SmoothScroll.WheelStepPx * SmoothScroll.MaxBurstMultiplier);
    }

    [Fact]
    public void ComputeNextOffset_AlreadyWithinThreshold_SnapsImmediately()
    {
        var target = new Vector(0, 120);
        var current = new Vector(0, 119.75);

        var next = SmoothScroll.ComputeNextOffset(current, target, 1.0 / 60.0, out var snapped);

        Assert.True(snapped);
        Assert.Equal(target, next);
    }

    [AvaloniaFact]
    public async Task AnimateTo_ScrollBy_And_CancelAnimation_WorkAcrossModes()
    {
        var sv = new ScrollViewer
        {
            Height = 150,
            Content = new Border { Width = 200, Height = 1200 }
        };
        var window = new Window
        {
            Width = 260,
            Height = 160,
            Content = sv
        };

        try
        {
            SmoothScroll.EnsureRegistered();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 减动效开启时：AnimateTo 直接落至目标位置，不启动插值动画
            Motion.SetEnabled(false);
            SmoothScroll.AnimateTo(sv, new Vector(0, 240));
            Assert.False(SmoothScroll.IsAnimating(sv));
            Assert.Equal(240.0, sv.Offset.Y, 1);

            // 开启动效时：AnimateTo / ScrollBy 平滑推进，CancelAnimation 立即停住
            Motion.SetEnabled(true);
            SmoothScroll.AnimateTo(sv, new Vector(0, 0), TimeSpan.FromMilliseconds(160));
            Assert.True(SmoothScroll.IsAnimating(sv));

            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
            SmoothScroll.CancelAnimation(sv);
            Assert.False(SmoothScroll.IsAnimating(sv));

            SmoothScroll.ScrollBy(sv, new Vector(0, 180), TimeSpan.FromMilliseconds(120));
            Assert.True(SmoothScroll.IsAnimating(sv));
        }
        finally
        {
            SmoothScroll.CancelAnimation(sv);
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CanScrollInDirection_RespectsBoundariesAndNestedScrollViewer()
    {
        var inner = new ScrollViewer
        {
            Height = 120,
            Content = new Border { Width = 200, Height = 500 }
        };
        var outer = new ScrollViewer
        {
            Height = 200,
            Content = new StackPanel
            {
                Children =
                {
                    new Border { Height = 100 },
                    inner,
                    new Border { Height = 400 }
                }
            }
        };
        var window = new Window
        {
            Width = 300,
            Height = 220,
            Content = outer
        };

        try
        {
            SmoothScroll.EnsureRegistered();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 顶部位置：只能向下滚（delta.Y < 0），不能继续向上滚（delta.Y > 0）
            Assert.True(SmoothScroll.CanScrollInDirection(outer, new Vector(0, -1)));
            Assert.False(SmoothScroll.CanScrollInDirection(outer, new Vector(0, 1)));
            Assert.True(SmoothScroll.CanScrollInDirection(inner, new Vector(0, -1)));
            Assert.False(SmoothScroll.CanScrollInDirection(inner, new Vector(0, 1)));

            // 触发滚轮向下滚动内层 ScrollViewer，再将外层滚下、内层回到顶部后测试边界穿透判断
            var centerInInner = inner.TranslatePoint(new Point(60, 40), window);
            Assert.NotNull(centerInInner);
            window.MouseWheel(centerInInner!.Value, new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();

            // 将外层滚动到底部、内层归零后：向上的滚轮无法由内层消化，外层应能继续向上滚
            outer.Offset = new Vector(0, 100);
            inner.Offset = Vector.Zero;
            SmoothScroll.CancelAnimation(inner);
            SmoothScroll.CancelAnimation(outer);

            Assert.False(SmoothScroll.CanScrollInDirection(inner, new Vector(0, 1)));
            Assert.True(SmoothScroll.CanScrollInDirection(outer, new Vector(0, 1)));
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MotionDisabled_DoesNotInterceptWheelAndUsesDefaultJump()
    {
        var sv = new ScrollViewer
        {
            Height = 150,
            Content = new Border { Width = 200, Height = 600 }
        };
        var window = new Window
        {
            Width = 260,
            Height = 160,
            Content = sv
        };

        try
        {
            SmoothScroll.EnsureRegistered();
            Motion.SetEnabled(false);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.MouseWheel(new Point(50, 50), new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();

            // 减动效关闭平滑插值后应由默认滚轮逻辑直接完成滚动
            Assert.True(sv.Offset.Y > 0);
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }
}
