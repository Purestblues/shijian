using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShiJian.Client.Controls.Input;

namespace ShiJian.Client.Librarys;

/// <summary>
/// 全局应用内平滑滚动拦截器（N13 + Interaction Motion Redesign）。
/// 为应用内 <see cref="ScrollViewer"/> 提供双模态（离散滚轮惯性 / 高精度触控板高阻尼）与
/// 程序化曲线跳转（<see cref="AnimateTo"/> / <see cref="ScrollBy"/>），
/// 遵循 <see cref="Motion.IsEnabled"/> 系统减动效偏好，并处理嵌套边界穿透与滑块拖拽打断。
/// </summary>
public static class SmoothScroll
{
    /// <summary>单格离散鼠标滚轮的基础滚动步长（像素）</summary>
    public const double WheelStepPx = 96.0;

    /// <summary>离散鼠标滚轮指数衰减速率（1/秒）：约 260ms 达到 95% 沉降，~300ms 贴合收敛</summary>
    public const double WheelDecayRate = 11.5;

    /// <summary>高精度触控板微增量衰减速率（1/秒）：约 95ms 顺滑跟手不粘滞</summary>
    public const double PrecisionDecayRate = 28.0;

    /// <summary>向后兼容默认衰减常量（等同于离散鼠标滚轮衰减）</summary>
    public const double DecayRate = WheelDecayRate;

    /// <summary>低于该滚轮 delta 幅度判定为高精度触控板/微增量手势</summary>
    public const double PrecisionDeltaThreshold = 0.85;

    /// <summary>距离目标小于该阈值（像素）时直接对齐并结束动画</summary>
    public const double SnapThresholdPx = 0.35;

    /// <summary>连续滚轮同向连击加速时间窗口（毫秒）</summary>
    public const double BurstWindowMs = 140.0;

    /// <summary>连击加速最大倍率（1.0x → 1.28x 线性加速）</summary>
    public const double MaxBurstMultiplier = 1.28;

    /// <summary>未消费累积待滚距离上限（像素），防止长距离连续甩滚尾迹失控</summary>
    public const double MaxPendingDeltaPx = 900.0;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, ScrollViewer, bool>(
            "IsEnabled",
            defaultValue: true,
            inherits: true);

    public static bool GetIsEnabled(AvaloniaObject obj) => obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(AvaloniaObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private enum ScrollAnimMode
    {
        ExponentialDamping,
        TimedCurve
    }

    private sealed class ScrollAnimState
    {
        public ScrollAnimMode Mode;
        public Vector StartOffset;
        public Vector TargetOffset;
        public Vector LastAppliedOffset;
        public Vector LastWheelDelta;
        public long LastWheelTickMs;
        public TimeSpan LastFrameTimestamp;
        public double DecayRate = WheelDecayRate;
        public double CurveElapsedSeconds;
        public double CurveDurationSeconds;
        public Easing CurveEasing = MotionTokens.Standard();
        public bool IsAnimating;
        public TopLevel? AttachedTopLevel;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollAnimState> States = new();
    private static readonly List<WeakReference<ScrollViewer>> ActiveViewers = [];
    private static readonly object RegistrationLock = new();
    private static bool _isRegistered;
    private static bool _frameScheduled;

    /// <summary>幂等注册全局 <see cref="ScrollViewer"/> 滚轮平滑、滑块拖拽打断、键盘翻页与减动效监听</summary>
    public static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (_isRegistered) return;
            _isRegistered = true;

            InputElement.PointerWheelChangedEvent.AddClassHandler<ScrollViewer>(
                OnScrollViewerPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: false);

            InputElement.PointerPressedEvent.AddClassHandler<ScrollViewer>(
                OnScrollViewerPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);

            InputElement.KeyDownEvent.AddClassHandler<ScrollViewer>(
                OnScrollViewerKeyDown,
                RoutingStrategies.Bubble,
                handledEventsToo: false);

            Motion.Changed += OnMotionChanged;
        }
    }

    /// <summary>查询 ScrollViewer 是否处于平滑滚动中</summary>
    public static bool IsAnimating(ScrollViewer? viewer)
        => viewer != null && States.TryGetValue(viewer, out var state) && state.IsAnimating;

    /// <summary>取消指定 ScrollViewer 正在进行的平滑惯性（用于拖拽滚动条、显式置位等场景）</summary>
    public static void CancelAnimation(ScrollViewer? viewer)
    {
        if (viewer == null) return;
        if (States.TryGetValue(viewer, out var state))
        {
            state.IsAnimating = false;
            state.TargetOffset = viewer.Offset;
            state.StartOffset = viewer.Offset;
            state.LastAppliedOffset = viewer.Offset;
            state.LastFrameTimestamp = TimeSpan.Zero;
            state.CurveElapsedSeconds = 0;
        }
    }

    /// <summary>程序化平滑滚动至指定坐标，使用标准减速曲线；减动效开启或距离极小时直接到位</summary>
    public static void AnimateTo(ScrollViewer? viewer, Vector targetOffset, TimeSpan? duration = null)
    {
        if (viewer == null) return;

        var clamped = ClampOffset(viewer, targetOffset);
        var effectiveDuration = duration ?? MotionTokens.M;

        if (!Motion.IsEnabled || !GetIsEnabled(viewer) || effectiveDuration <= TimeSpan.Zero)
        {
            CancelAnimation(viewer);
            viewer.Offset = clamped;
            return;
        }

        if ((clamped - viewer.Offset).Length <= SnapThresholdPx)
        {
            CancelAnimation(viewer);
            viewer.Offset = clamped;
            return;
        }

        var topLevel = TopLevel.GetTopLevel(viewer);
        if (topLevel == null)
        {
            CancelAnimation(viewer);
            viewer.Offset = clamped;
            return;
        }

        var state = States.GetOrCreateValue(viewer);
        state.Mode = ScrollAnimMode.TimedCurve;
        state.StartOffset = viewer.Offset;
        state.TargetOffset = clamped;
        state.LastAppliedOffset = viewer.Offset;
        state.LastFrameTimestamp = TimeSpan.Zero;
        state.CurveElapsedSeconds = 0;
        state.CurveDurationSeconds = effectiveDuration.TotalSeconds;
        state.CurveEasing = MotionTokens.Standard();
        state.AttachedTopLevel = topLevel;

        if (!state.IsAnimating)
        {
            state.IsAnimating = true;
            TrackActiveViewer(viewer);
        }

        ScheduleNextFrame(topLevel);
    }

    /// <summary>程序化平滑相对增量滚动（支持连按叠加未完成目标）</summary>
    public static void ScrollBy(ScrollViewer? viewer, Vector delta, TimeSpan? duration = null)
    {
        if (viewer == null) return;

        var baseOffset = States.TryGetValue(viewer, out var state) && state.IsAnimating
            ? state.TargetOffset
            : viewer.Offset;

        AnimateTo(viewer, baseOffset + delta, duration);
    }

    /// <summary>根据滚轮/触控板增量和连击间隔计算单次目标位移及匹配的指数衰减速率</summary>
    public static Vector ComputeWheelStep(Vector delta, double elapsedMs, Vector previousDelta, out double decayRate)
    {
        var magnitude = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
        if (magnitude > 0 && magnitude < PrecisionDeltaThreshold)
        {
            decayRate = PrecisionDecayRate;
            return new Vector(-delta.X * WheelStepPx, -delta.Y * WheelStepPx);
        }

        decayRate = WheelDecayRate;
        var multiplier = 1.0;

        var sameSignY = Math.Abs(delta.Y) > 1e-6 && Math.Sign(delta.Y) == Math.Sign(previousDelta.Y);
        var sameSignX = Math.Abs(delta.X) > 1e-6 && Math.Sign(delta.X) == Math.Sign(previousDelta.X);
        if ((sameSignY || sameSignX) && elapsedMs > 0 && elapsedMs < BurstWindowMs)
        {
            multiplier = 1.0 + (MaxBurstMultiplier - 1.0) * (1.0 - elapsedMs / BurstWindowMs);
        }

        return new Vector(-delta.X * WheelStepPx * multiplier, -delta.Y * WheelStepPx * multiplier);
    }

    /// <summary>使用默认离散滚轮衰减速率计算指数阻尼下一帧偏移量</summary>
    public static Vector ComputeNextOffset(Vector current, Vector target, double dtSeconds, out bool snapped)
        => ComputeNextOffset(current, target, dtSeconds, WheelDecayRate, out snapped);

    /// <summary>使用指定衰减速率计算指数阻尼下一帧滚动偏移量</summary>
    public static Vector ComputeNextOffset(Vector current, Vector target, double dtSeconds, double decayRate, out bool snapped)
    {
        var dx = target.X - current.X;
        var dy = target.Y - current.Y;

        var snapX = Math.Abs(dx) <= SnapThresholdPx;
        var snapY = Math.Abs(dy) <= SnapThresholdPx;

        if (snapX && snapY)
        {
            snapped = true;
            return target;
        }

        // dt 上限钳位到 100ms，避免切窗/卡顿回来出现瞬跳
        var dt = Math.Clamp(dtSeconds, 0.0, 0.1);
        var factor = 1.0 - Math.Exp(-dt * decayRate);

        var nextX = snapX ? target.X : current.X + dx * factor;
        var nextY = snapY ? target.Y : current.Y + dy * factor;

        snapped = Math.Abs(target.X - nextX) <= SnapThresholdPx
                  && Math.Abs(target.Y - nextY) <= SnapThresholdPx;

        return snapped ? target : new Vector(nextX, nextY);
    }

    /// <summary>判断 ScrollViewer 在指定滚轮方向上是否仍有可滚动空间</summary>
    public static bool CanScrollInDirection(ScrollViewer sv, Vector wheelDelta)
    {
        var baseOffset = States.TryGetValue(sv, out var s) && s.IsAnimating
            ? s.TargetOffset
            : sv.Offset;

        var maxOffset = GetMaxOffset(sv);
        var canVertical = sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled
                          && maxOffset.Y > 0.5
                          && Math.Abs(wheelDelta.Y) > 1e-6;
        if (canVertical)
        {
            if (wheelDelta.Y > 0 && baseOffset.Y > 0.5) return true;
            if (wheelDelta.Y < 0 && baseOffset.Y < maxOffset.Y - 0.5) return true;
        }

        var canHorizontal = sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                            && maxOffset.X > 0.5
                            && Math.Abs(wheelDelta.X) > 1e-6;
        if (canHorizontal)
        {
            if (wheelDelta.X > 0 && baseOffset.X > 0.5) return true;
            if (wheelDelta.X < 0 && baseOffset.X < maxOffset.X - 0.5) return true;
        }

        return false;
    }

    private static void OnMotionChanged(object? sender, EventArgs e)
    {
        if (Motion.IsEnabled) return;

        Dispatcher.UIThread.Post(SnapAllActiveToTarget);
    }

    private static void SnapAllActiveToTarget()
    {
        for (int i = ActiveViewers.Count - 1; i >= 0; i--)
        {
            if (ActiveViewers[i].TryGetTarget(out var sv) && States.TryGetValue(sv, out var state))
            {
                if (state.IsAnimating)
                {
                    sv.Offset = ClampOffset(sv, state.TargetOffset);
                }
                state.IsAnimating = false;
                state.LastAppliedOffset = sv.Offset;
                state.LastFrameTimestamp = TimeSpan.Zero;
                state.CurveElapsedSeconds = 0;
            }
            ActiveViewers.RemoveAt(i);
        }
        _frameScheduled = false;
    }

    private static void OnScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (IsInsideScrollBar(e.Source as Visual, sv))
        {
            CancelAnimation(sv);
        }
    }

    private static void OnScrollViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (!sv.IsEnabled || !sv.IsEffectivelyVisible) return;
        if (!Motion.IsEnabled || !GetIsEnabled(sv)) return;
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is not (Key.PageUp or Key.PageDown or Key.Home or Key.End)) return;
        if (IsEditableOrManagedListSource(e.Source as Visual, sv)) return;
        if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return;

        var maxOffset = GetMaxOffset(sv);
        if (maxOffset.Y <= 0.5) return;

        var currentTargetY = States.TryGetValue(sv, out var st) && st.IsAnimating ? st.TargetOffset.Y : sv.Offset.Y;
        var pageStep = Math.Max(48.0, sv.Viewport.Height * 0.85);
        double targetY = e.Key switch
        {
            Key.PageDown => currentTargetY + pageStep,
            Key.PageUp => currentTargetY - pageStep,
            Key.Home => 0.0,
            Key.End => maxOffset.Y,
            _ => currentTargetY
        };

        targetY = Math.Clamp(targetY, 0.0, maxOffset.Y);
        if (Math.Abs(targetY - sv.Offset.Y) <= SnapThresholdPx && Math.Abs(targetY - currentTargetY) <= SnapThresholdPx)
            return;

        AnimateTo(sv, new Vector(sv.Offset.X, targetY));
        e.Handled = true;
    }

    private static void OnScrollViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (!sv.IsEnabled || !sv.IsEffectivelyVisible) return;
        if (!Motion.IsEnabled || !GetIsEnabled(sv)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        // 优先让更内层的可滚动容器消化本次滚轮；仅当内层无法在该方向继续滚动时才由外层接管
        if (HasInnerScrollableConsumer(sv, e.Source as Visual, e.Delta))
        {
            return;
        }

        if (!CanScrollInDirection(sv, e.Delta))
        {
            return;
        }

        var state = States.GetOrCreateValue(sv);

        // 若外部操作（拖拽滚动条或编程重置）修改了 Offset，则从最新 Offset 重新起算
        if (!state.IsAnimating || state.Mode != ScrollAnimMode.ExponentialDamping || HasExternalOffsetChange(sv, state))
        {
            state.Mode = ScrollAnimMode.ExponentialDamping;
            state.TargetOffset = state.IsAnimating && !HasExternalOffsetChange(sv, state)
                ? state.TargetOffset
                : sv.Offset;
            state.LastAppliedOffset = sv.Offset;
            state.LastFrameTimestamp = TimeSpan.Zero;
        }

        var nowMs = Environment.TickCount64;
        var elapsedMs = state.LastWheelTickMs > 0 ? Math.Max(0.0, nowMs - state.LastWheelTickMs) : BurstWindowMs + 1;
        var step = ComputeWheelStep(e.Delta, elapsedMs, state.LastWheelDelta, out var decayRate);
        state.LastWheelTickMs = nowMs;
        state.LastWheelDelta = e.Delta;
        state.DecayRate = decayRate;

        var maskedStep = new Vector(
            sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled ? step.X : 0,
            sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ? step.Y : 0);

        var rawTarget = state.TargetOffset + maskedStep;
        var clampedPending = new Vector(
            sv.Offset.X + Math.Clamp(rawTarget.X - sv.Offset.X, -MaxPendingDeltaPx, MaxPendingDeltaPx),
            sv.Offset.Y + Math.Clamp(rawTarget.Y - sv.Offset.Y, -MaxPendingDeltaPx, MaxPendingDeltaPx));

        var nextTarget = ClampOffset(sv, clampedPending);
        if ((nextTarget - sv.Offset).Length <= SnapThresholdPx)
        {
            return;
        }

        state.TargetOffset = nextTarget;
        state.LastAppliedOffset = sv.Offset;
        state.AttachedTopLevel = TopLevel.GetTopLevel(sv);

        if (!state.IsAnimating)
        {
            state.IsAnimating = true;
            state.LastFrameTimestamp = TimeSpan.Zero;
            TrackActiveViewer(sv);
        }

        ScheduleNextFrame(state.AttachedTopLevel);
        e.Handled = true;
    }

    private static bool IsInsideScrollBar(Visual? source, ScrollViewer owner)
    {
        var cursor = source;
        while (cursor != null && !ReferenceEquals(cursor, owner))
        {
            if (cursor is Thumb or ScrollBar) return true;
            cursor = cursor.GetVisualParent();
        }
        return false;
    }

    private static bool IsEditableOrManagedListSource(Visual? source, ScrollViewer owner)
    {
        var cursor = source;
        while (cursor != null && !ReferenceEquals(cursor, owner))
        {
            if (cursor is TextBox or InputBox or KeyGestureCapture or ListBoxItem or ComboBox)
                return true;
            cursor = cursor.GetVisualParent();
        }
        return false;
    }

    private static bool HasInnerScrollableConsumer(ScrollViewer outer, Visual? source, Vector delta)
    {
        var cursor = source;
        while (cursor != null && !ReferenceEquals(cursor, outer))
        {
            if (cursor is ScrollViewer inner
                && inner.IsEnabled
                && inner.IsEffectivelyVisible
                && CanScrollInDirection(inner, delta))
            {
                return true;
            }
            cursor = cursor.GetVisualParent();
        }
        return false;
    }

    private static bool HasExternalOffsetChange(ScrollViewer sv, ScrollAnimState state)
    {
        return (sv.Offset - state.LastAppliedOffset).Length > 1.5;
    }

    private static Vector GetMaxOffset(ScrollViewer sv)
    {
        var maxX = Math.Max(0.0, sv.Extent.Width - sv.Viewport.Width);
        var maxY = Math.Max(0.0, sv.Extent.Height - sv.Viewport.Height);
        return new Vector(maxX, maxY);
    }

    private static Vector ClampOffset(ScrollViewer sv, Vector offset)
    {
        var max = GetMaxOffset(sv);
        return new Vector(
            Math.Clamp(offset.X, 0.0, max.X),
            Math.Clamp(offset.Y, 0.0, max.Y));
    }

    private static void TrackActiveViewer(ScrollViewer sv)
    {
        for (int i = ActiveViewers.Count - 1; i >= 0; i--)
        {
            if (!ActiveViewers[i].TryGetTarget(out var existing))
            {
                ActiveViewers.RemoveAt(i);
            }
            else if (ReferenceEquals(existing, sv))
            {
                return;
            }
        }
        ActiveViewers.Add(new WeakReference<ScrollViewer>(sv));
    }

    private static void ScheduleNextFrame(TopLevel? preferredTopLevel)
    {
        if (_frameScheduled) return;

        var topLevel = preferredTopLevel ?? FindActiveTopLevel();
        if (topLevel == null)
        {
            SnapAllActiveToTarget();
            return;
        }

        _frameScheduled = true;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private static TopLevel? FindActiveTopLevel()
    {
        for (int i = ActiveViewers.Count - 1; i >= 0; i--)
        {
            if (ActiveViewers[i].TryGetTarget(out var sv) && States.TryGetValue(sv, out var state))
            {
                var tl = TopLevel.GetTopLevel(sv) ?? state.AttachedTopLevel;
                if (tl != null) return tl;
            }
        }
        return null;
    }

    private static void OnAnimationFrame(TimeSpan timestamp)
    {
        _frameScheduled = false;

        if (!Motion.IsEnabled)
        {
            SnapAllActiveToTarget();
            return;
        }

        TopLevel? nextTopLevel = null;

        for (int i = ActiveViewers.Count - 1; i >= 0; i--)
        {
            if (!ActiveViewers[i].TryGetTarget(out var sv)
                || !States.TryGetValue(sv, out var state)
                || !state.IsAnimating)
            {
                ActiveViewers.RemoveAt(i);
                continue;
            }

            if (!sv.IsEffectivelyVisible || TopLevel.GetTopLevel(sv) == null)
            {
                state.IsAnimating = false;
                ActiveViewers.RemoveAt(i);
                continue;
            }

            if (HasExternalOffsetChange(sv, state))
            {
                // 用户拖拽了滚动条或业务代码显式复位 Offset，停止惯性追踪
                state.IsAnimating = false;
                state.LastAppliedOffset = sv.Offset;
                ActiveViewers.RemoveAt(i);
                continue;
            }

            var target = ClampOffset(sv, state.TargetOffset);
            state.TargetOffset = target;

            var dt = state.LastFrameTimestamp == TimeSpan.Zero
                ? 1.0 / 60.0
                : Math.Max(0.001, (timestamp - state.LastFrameTimestamp).TotalSeconds);
            state.LastFrameTimestamp = timestamp;

            Vector next;
            bool snapped;

            if (state.Mode == ScrollAnimMode.TimedCurve)
            {
                state.CurveElapsedSeconds += Math.Clamp(dt, 0.0, 0.1);
                var raw = state.CurveDurationSeconds <= 0
                    ? 1.0
                    : Math.Clamp(state.CurveElapsedSeconds / state.CurveDurationSeconds, 0.0, 1.0);
                var eased = state.CurveEasing.Ease(raw);
                next = new Vector(
                    state.StartOffset.X + (target.X - state.StartOffset.X) * eased,
                    state.StartOffset.Y + (target.Y - state.StartOffset.Y) * eased);
                snapped = raw >= 1.0;
                if (snapped) next = target;
            }
            else
            {
                next = ComputeNextOffset(sv.Offset, target, dt, state.DecayRate, out snapped);
            }

            sv.Offset = next;
            state.LastAppliedOffset = sv.Offset;

            if (snapped)
            {
                state.IsAnimating = false;
                state.LastFrameTimestamp = TimeSpan.Zero;
                state.CurveElapsedSeconds = 0;
                ActiveViewers.RemoveAt(i);
            }
            else
            {
                nextTopLevel ??= TopLevel.GetTopLevel(sv) ?? state.AttachedTopLevel;
            }
        }

        if (ActiveViewers.Count > 0)
        {
            ScheduleNextFrame(nextTopLevel);
        }
    }
}
