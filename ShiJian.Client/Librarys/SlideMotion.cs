using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ShiJian.Client.Librarys;

/// <summary>
/// 逐帧插值做「位移」。
///
/// 为什么不用 Animation：Avalonia 12 移除了 <c>Animation.Animators</c>，
/// <c>TransformAnimator</c> / <c>TransformOperationsAnimator</c> 都是 internal，
/// 关键帧里只要出现 <see cref="Visual.RenderTransformProperty"/>，
/// <c>RunAsync</c> 会直接抛「No animator registered for the property RenderTransform」
/// 并把整个动画跳过（详见 docs/ROADMAP.md 的 N4 备注）。
///
/// 这里只改 <see cref="TranslateTransform.Y"/>：走渲染变换，不触发 layout，
/// 也不重采样内容，成本就是一次属性写入 + 一次重绘。
/// </summary>
public static class SlideMotion
{
    /// <summary>一次位移动画。结束后把变换清干净，绝不让控件停在偏移位置。</summary>
    public static async Task SlideYAsync(
        Control target,
        double fromY,
        double toY,
        TimeSpan duration,
        IEasing easing,
        TimeSpan delay = default,
        CancellationToken cancellationToken = default)
    {
        if (!Motion.IsEnabled)
        {
            Reset(target);
            return;
        }

        var topLevel = TopLevel.GetTopLevel(target);
        if (topLevel == null || duration <= TimeSpan.Zero)
        {
            Reset(target);
            return;
        }

        var transform = GetOrCreateTranslateTransform(target);
        transform.Y = fromY;

        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);

            // 延迟期间控件可能已被卸载，不能再拿旧 topLevel 启动动画
            var currentTopLevel = TopLevel.GetTopLevel(target);
            if (currentTopLevel == null) return;

            await AnimateAsync(target, currentTopLevel, duration, easing, progress =>
            {
                transform.Y = fromY + (toY - fromY) * progress;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 控件在动画途中被取消：直接落到终态，不能留在半路
        }
        finally
        {
            Reset(target);
        }
    }

    /// <summary>
    /// 逐帧同时插值 Y 位移与透明度（用于顶部横幅通知滑入/滑出等连续动效）。
    /// </summary>
    public static async Task AnimateYAndOpacityAsync(
        Control target,
        double fromY,
        double toY,
        double fromOpacity,
        double toOpacity,
        TimeSpan duration,
        IEasing easing,
        bool resetTransformOnComplete = false,
        CancellationToken cancellationToken = default)
    {
        var topLevel = TopLevel.GetTopLevel(target);
        if (!Motion.IsEnabled || topLevel == null || duration <= TimeSpan.Zero)
        {
            ApplyYAndOpacity(target, toY, toOpacity, resetTransformOnComplete);
            return;
        }

        var transform = GetOrCreateTranslateTransform(target);
        transform.Y = fromY;
        target.Opacity = fromOpacity;

        await AnimateAsync(target, topLevel, duration, easing, progress =>
        {
            transform.Y = fromY + (toY - fromY) * progress;
            target.Opacity = fromOpacity + (toOpacity - fromOpacity) * progress;
        }, cancellationToken);

        if (!cancellationToken.IsCancellationRequested && TopLevel.GetTopLevel(target) != null)
        {
            ApplyYAndOpacity(target, toY, toOpacity, resetTransformOnComplete);
        }
    }

    /// <summary>
    /// 逐帧同时插值二维位移、缩放与透明度（用于页面切换、弹窗进退场、Tab 切换、Popup 等连续动效）。
    /// </summary>
    public static async Task AnimateTransformAndOpacityAsync(
        Control target,
        double fromX,
        double toX,
        double fromY,
        double toY,
        double fromScale,
        double toScale,
        double fromOpacity,
        double toOpacity,
        TimeSpan duration,
        IEasing easing,
        TimeSpan delay = default,
        bool resetTransformOnComplete = true,
        RelativePoint? renderTransformOrigin = null,
        CancellationToken cancellationToken = default)
    {
        var topLevel = TopLevel.GetTopLevel(target);
        if (!Motion.IsEnabled || topLevel == null || duration <= TimeSpan.Zero)
        {
            ApplyTransformAndOpacity(target, toX, toY, toScale, toOpacity, resetTransformOnComplete, renderTransformOrigin);
            return;
        }

        var useScale = Math.Abs(fromScale - 1d) > 1e-4 || Math.Abs(toScale - 1d) > 1e-4;
        ApplyTransformAndOpacity(target, fromX, fromY, fromScale, fromOpacity, resetTransformOnComplete: false,
            useScale ? (renderTransformOrigin ?? RelativePoint.Center) : null);

        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            var currentTopLevel = TopLevel.GetTopLevel(target);
            if (currentTopLevel == null)
            {
                if (!cancellationToken.IsCancellationRequested)
                    ApplyTransformAndOpacity(target, toX, toY, toScale, toOpacity, resetTransformOnComplete, renderTransformOrigin);
                return;
            }

            await AnimateAsync(target, currentTopLevel, duration, easing, progress =>
            {
                var curX = fromX + (toX - fromX) * progress;
                var curY = fromY + (toY - fromY) * progress;
                var curScale = useScale ? fromScale + (toScale - fromScale) * progress : 1d;
                target.Opacity = fromOpacity + (toOpacity - fromOpacity) * progress;

                if (useScale)
                    SetScaleAndTranslate(target, curScale, curX, curY);
                else
                    SetTranslate(target, curX, curY);
            }, cancellationToken);

            if (!cancellationToken.IsCancellationRequested && TopLevel.GetTopLevel(target) != null)
            {
                ApplyTransformAndOpacity(target, toX, toY, toScale, toOpacity, resetTransformOnComplete, renderTransformOrigin);
            }
        }
        catch (OperationCanceledException)
        {
            // 已取消的动画不回写终态，避免覆盖紧接着启动的新一轮过渡
        }
    }

    /// <summary>
    /// 逐帧插值透明度（用于背景遮罩渐显/渐隐）。
    /// </summary>
    public static async Task AnimateOpacityAsync(
        Control target,
        double fromOpacity,
        double toOpacity,
        TimeSpan duration,
        IEasing easing,
        TimeSpan delay = default,
        CancellationToken cancellationToken = default)
    {
        var topLevel = TopLevel.GetTopLevel(target);
        if (!Motion.IsEnabled || topLevel == null || duration <= TimeSpan.Zero)
        {
            target.Opacity = toOpacity;
            return;
        }

        target.Opacity = fromOpacity;

        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            var currentTopLevel = TopLevel.GetTopLevel(target);
            if (currentTopLevel == null)
            {
                if (!cancellationToken.IsCancellationRequested)
                    target.Opacity = toOpacity;
                return;
            }

            await AnimateAsync(target, currentTopLevel, duration, easing, progress =>
            {
                target.Opacity = fromOpacity + (toOpacity - fromOpacity) * progress;
            }, cancellationToken);

            if (!cancellationToken.IsCancellationRequested && TopLevel.GetTopLevel(target) != null)
            {
                target.Opacity = toOpacity;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static double GetTranslateY(Control target)
    {
        if (target.RenderTransform is TranslateTransform transform) return transform.Y;
        if (target.RenderTransform is TransformGroup group && group.Children.Count >= 2 && group.Children[1] is TranslateTransform t)
            return t.Y;
        return 0d;
    }

    private static TranslateTransform GetOrCreateTranslateTransform(Control target)
    {
        if (target.RenderTransform is TranslateTransform existing)
            return existing;

        var transform = new TranslateTransform();
        target.RenderTransform = transform;
        return transform;
    }

    private static void SetTranslate(Control target, double x, double y)
    {
        var transform = GetOrCreateTranslateTransform(target);
        transform.X = x;
        transform.Y = y;
    }

    private static void SetScaleAndTranslate(Control target, double scale, double x, double y)
    {
        if (target.RenderTransform is TransformGroup group
            && group.Children.Count == 2
            && group.Children[0] is ScaleTransform scaleTransform
            && group.Children[1] is TranslateTransform translateTransform)
        {
            scaleTransform.ScaleX = scale;
            scaleTransform.ScaleY = scale;
            translateTransform.X = x;
            translateTransform.Y = y;
            return;
        }

        var nextGroup = new TransformGroup();
        nextGroup.Children.Add(new ScaleTransform(scale, scale));
        nextGroup.Children.Add(new TranslateTransform(x, y));
        target.RenderTransform = nextGroup;
    }

    private static void ApplyTransformAndOpacity(
        Control target,
        double x,
        double y,
        double scale,
        double opacity,
        bool resetTransformOnComplete,
        RelativePoint? renderTransformOrigin)
    {
        target.Opacity = opacity;
        if (resetTransformOnComplete)
        {
            Reset(target);
            return;
        }

        if (Math.Abs(scale - 1d) > 1e-4)
        {
            target.RenderTransformOrigin = renderTransformOrigin ?? RelativePoint.Center;
            SetScaleAndTranslate(target, scale, x, y);
        }
        else
        {
            SetTranslate(target, x, y);
        }
    }

    private static void ApplyYAndOpacity(
        Control target,
        double y,
        double opacity,
        bool resetTransformOnComplete)
    {
        target.Opacity = opacity;
        if (resetTransformOnComplete)
        {
            Reset(target);
        }
        else
        {
            GetOrCreateTranslateTransform(target).Y = y;
        }
    }

    /// <summary>把位移/缩放变换清掉（动效关闭 / 动画结束 / 控件卸载）</summary>
    public static void Reset(Control target)
    {
        if (target.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
            transform.Y = 0;
            target.RenderTransform = null;
        }
        else if (target.RenderTransform is TransformGroup)
        {
            target.RenderTransform = null;
        }
    }

    /// <summary>
    /// 用渲染帧驱动进度回调。<see cref="TopLevel.RequestAnimationFrame"/> 与渲染循环同步，
    /// 比 DispatcherTimer 稳（不会在掉帧时堆回调）。
    /// </summary>
    private static async Task AnimateAsync(
        Control target,
        TopLevel topLevel,
        TimeSpan duration,
        IEasing easing,
        Action<double> apply,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAt = -1d;
        var totalMs = duration.TotalMilliseconds;

        void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            completion.TrySetResult();
        }

        void OnTopLevelClosed(object? sender, EventArgs e)
        {
            completion.TrySetResult();
        }

        void Step(TimeSpan timestamp)
        {
            if (completion.Task.IsCompleted) return;

            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            // 控件在帧回调前被卸载时直接结束，不能在 RequestAnimationFrame 回调里抛异常
            if (TopLevel.GetTopLevel(target) == null)
            {
                completion.TrySetResult();
                return;
            }

            try
            {
                var now = timestamp.TotalMilliseconds;
                if (startedAt < 0) startedAt = now;

                var elapsed = now - startedAt;
                var raw = totalMs <= 0 ? 1d : Math.Clamp(elapsed / totalMs, 0d, 1d);

                apply(easing.Ease(raw));

                if (raw >= 1d)
                {
                    completion.TrySetResult();
                    return;
                }

                topLevel.RequestAnimationFrame(Step);
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        target.DetachedFromVisualTree += OnDetached;
        topLevel.Closed += OnTopLevelClosed;
        using var cancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken))
            : default;

        try
        {
            topLevel.RequestAnimationFrame(Step);
            await completion.Task;
        }
        finally
        {
            target.DetachedFromVisualTree -= OnDetached;
            topLevel.Closed -= OnTopLevelClosed;
        }
    }
}
