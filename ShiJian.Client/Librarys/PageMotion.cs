using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace ShiJian.Client.Librarys;

/// <summary>页面导航方向语义</summary>
public enum PageTransitionDirection
{
    /// <summary>顶层侧栏同级切换：轻量纵向沉降淡入</summary>
    Replace,

    /// <summary>向子页下钻：旧页微向左隐退，新页从右侧平滑滑入</summary>
    Forward,

    /// <summary>返回上级页面：旧页向右轻撤，上级页从左侧平滑复位</summary>
    Back
}

/// <summary>
/// 换页进退场动效。统一由 <see cref="Controls.PageContainer"/> 在页面切换时协调调用，
/// 按 <see cref="PageTransitionDirection"/> 区分“同级切换 / 向前下钻 / 返回上级”的方向语意，
/// 并在快速连击与关闭动效时保持可中断与终态确定性。
/// </summary>
public static class PageMotion
{
    /// <summary>同级切换进场总时长（含 45ms 延迟）</summary>
    public static readonly TimeSpan EnterDuration = MotionTokens.PageEnterDelay + MotionTokens.PageEnter;

    /// <summary>下钻 / 返回时的水平位移距离（px）</summary>
    public const double PageEnterOffsetX = 18;

    /// <summary>旧页退场时的水平撤出位移（px）</summary>
    public const double PageLeaveOffsetX = 12;

    /// <summary>动画结束态 / 动效关闭时的直切态：清干净透明度与位移变换，避免页面滞留偏移或半透明</summary>
    public static void SnapToRest(Control control)
    {
        control.Opacity = 1;
        SlideMotion.Reset(control);
    }

    /// <summary>进场初始态（Replace 方向默认值）</summary>
    internal static void Prepare(Control control)
        => Prepare(control, PageTransitionDirection.Replace);

    /// <summary>按导航方向同步设置进场首帧状态，防止首帧闪出完整内容</summary>
    internal static void Prepare(Control control, PageTransitionDirection direction)
    {
        control.Opacity = 0;
        var (fromX, fromY) = GetEnterOffset(direction);
        if (Math.Abs(fromX) > 1e-4 || Math.Abs(fromY) > 1e-4)
        {
            control.RenderTransform = new TranslateTransform(fromX, fromY);
        }
    }

    public static (double X, double Y) GetEnterOffset(PageTransitionDirection direction) => direction switch
    {
        PageTransitionDirection.Forward => (PageEnterOffsetX, 0),
        PageTransitionDirection.Back => (-PageEnterOffsetX, 0),
        _ => (0, MotionTokens.PageOffsetY)
    };

    public static (double X, double Y) GetLeaveOffset(PageTransitionDirection direction) => direction switch
    {
        PageTransitionDirection.Forward => (-PageLeaveOffsetX, 0),
        PageTransitionDirection.Back => (PageLeaveOffsetX, 0),
        _ => (0, 0)
    };

    /// <summary>兼容旧签名：以默认同级切换语义播放进场动效</summary>
    public static Task PlayEnterAsync(Control control)
        => PlayEnterAsync(control, PageTransitionDirection.Replace, CancellationToken.None);

    /// <summary>按方向语义播放进场动效；动效关闭时同步归位，无任何延迟</summary>
    public static async Task PlayEnterAsync(
        Control control,
        PageTransitionDirection direction,
        CancellationToken cancellationToken = default)
    {
        if (!Motion.IsEnabled)
        {
            SnapToRest(control);
            return;
        }

        var (fromX, fromY) = GetEnterOffset(direction);
        var delay = direction == PageTransitionDirection.Replace ? MotionTokens.PageEnterDelay : TimeSpan.Zero;

        try
        {
            await SlideMotion.AnimateTransformAndOpacityAsync(
                control,
                fromX, 0,
                fromY, 0,
                1d, 1d,
                0d, 1d,
                MotionTokens.PageEnter,
                MotionTokens.Enter(),
                delay: delay,
                resetTransformOnComplete: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logging.Logger.Warn($"[PageMotion] 进场动画失败，已跳过: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SnapToRest(control);
            }
        }
    }

    /// <summary>按方向语义播放旧页退场动效；动效关闭或无 TopLevel 时直接返回</summary>
    public static async Task PlayExitAsync(
        Control control,
        PageTransitionDirection direction,
        CancellationToken cancellationToken = default)
    {
        if (!Motion.IsEnabled || TopLevel.GetTopLevel(control) == null)
            return;

        var (toX, toY) = GetLeaveOffset(direction);

        try
        {
            await SlideMotion.AnimateTransformAndOpacityAsync(
                control,
                0, toX,
                0, toY,
                1d, 1d,
                control.Opacity, 0d,
                MotionTokens.PageLeave,
                MotionTokens.Leave(),
                delay: TimeSpan.Zero,
                resetTransformOnComplete: false,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"[PageMotion] 退场动画失败，已跳过: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
