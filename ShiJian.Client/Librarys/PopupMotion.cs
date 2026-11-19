using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ShiJian.Client.Librarys;

/// <summary>
/// 全库统一 Popup / ContextMenu / Flyout 弹出进场动效（Pillar 4）。
///
/// 借助 Win32 顶层 Popup HWND 的透明边距缓冲区域，从比终态小 4% 的 <c>scale(0.96)</c>
/// 与朝向放置锚点方向偏移 <c>4px</c> 缓动回归至 <c>scale(1.0)</c> + <c>translate(0, 0)</c> + <c>Opacity 1</c>
/// （150ms Enter 减速曲线），全程不越出弹窗边界，无裁剪且尊重 <see cref="Motion.IsEnabled"/>。
/// </summary>
public static class PopupMotion
{
    public const double InitialScale = 0.96;
    public const double OffsetPx = 4.0;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, AvaloniaObject, bool>(
            "IsEnabled",
            defaultValue: true,
            inherits: true);

    public static bool GetIsEnabled(AvaloniaObject obj) => obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(AvaloniaObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private sealed class PopupAnimState
    {
        public CancellationTokenSource? Cts;
        public long LastOpenGeneration;
        public bool IsOpen;
    }

    private static readonly ConditionalWeakTable<Control, PopupAnimState> States = new();
    private static readonly object RegistrationLock = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (_registered) return;
            _registered = true;

            Popup.IsOpenProperty.Changed.AddClassHandler<Popup>(OnPopupIsOpenChanged);
            ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>(OnContextMenuOpened);
            ContextMenu.ClosedEvent.AddClassHandler<ContextMenu>(OnContextMenuClosed);
        }
    }

    /// <summary>根据 Popup 的 Placement 计算进场位移与缩放锚点（从锚点侧轻弹伸展）</summary>
    public static (double FromX, double FromY, RelativePoint Origin) ResolveEntryOffset(PlacementMode placement)
    {
        return placement switch
        {
            PlacementMode.Top or PlacementMode.TopEdgeAlignedLeft =>
                (0, OffsetPx, new RelativePoint(placement == PlacementMode.TopEdgeAlignedLeft ? 0.25 : 0.5, 1.0, RelativeUnit.Relative)),
            PlacementMode.TopEdgeAlignedRight =>
                (0, OffsetPx, new RelativePoint(0.75, 1.0, RelativeUnit.Relative)),
            PlacementMode.BottomEdgeAlignedLeft =>
                (0, -OffsetPx, new RelativePoint(0.25, 0.0, RelativeUnit.Relative)),
            PlacementMode.BottomEdgeAlignedRight =>
                (0, -OffsetPx, new RelativePoint(0.75, 0.0, RelativeUnit.Relative)),
            PlacementMode.Left or PlacementMode.LeftEdgeAlignedTop =>
                (OffsetPx, 0, new RelativePoint(1.0, 0.25, RelativeUnit.Relative)),
            PlacementMode.LeftEdgeAlignedBottom =>
                (OffsetPx, 0, new RelativePoint(1.0, 0.75, RelativeUnit.Relative)),
            PlacementMode.Right or PlacementMode.RightEdgeAlignedTop =>
                (-OffsetPx, 0, new RelativePoint(0.0, 0.25, RelativeUnit.Relative)),
            PlacementMode.RightEdgeAlignedBottom =>
                (-OffsetPx, 0, new RelativePoint(0.0, 0.75, RelativeUnit.Relative)),
            PlacementMode.Center =>
                (0, -OffsetPx, RelativePoint.Center),
            _ =>
                (0, -OffsetPx, new RelativePoint(0.5, 0.0, RelativeUnit.Relative))
        };
    }

    private static void OnPopupIsOpenChanged(Popup popup, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is not bool isOpen) return;
        if (!GetIsEnabled(popup)) return;
        if (popup.Child is not Control target) return;
        if (IsToolTipPopup(popup, target)) return;

        if (!isOpen)
        {
            ResetTarget(target);
            return;
        }

        PrepareOpen(target, popup.Placement, popup);
    }

    private static void OnContextMenuOpened(ContextMenu menu, RoutedEventArgs _)
    {
        if (!GetIsEnabled(menu)) return;

        var state = States.GetOrCreateValue(menu);
        if (state.IsOpen) return;

        PrepareOpen(menu, PlacementMode.Pointer, hostPopup: null);
    }

    private static void OnContextMenuClosed(ContextMenu menu, RoutedEventArgs _)
    {
        ResetTarget(menu);
    }

    private static bool IsToolTipPopup(Popup popup, Control target)
        => target is ToolTip || popup.TemplatedParent is ToolTip;

    private static void PrepareOpen(Control target, PlacementMode placement, Popup? hostPopup)
    {
        var state = States.GetOrCreateValue(target);
        CancelState(state);
        state.IsOpen = true;

        if (!Motion.IsEnabled)
        {
            target.Opacity = 1d;
            SlideMotion.Reset(target);
            return;
        }

        var (fromX, fromY, origin) = ResolveEntryOffset(placement);
        var gen = ++state.LastOpenGeneration;
        var cts = new CancellationTokenSource();
        state.Cts = cts;

        target.RenderTransformOrigin = origin;
        target.Opacity = 0d;

        void LaunchAnimation()
        {
            if (cts.IsCancellationRequested || state.LastOpenGeneration != gen || !state.IsOpen)
                return;

            var tl = TopLevel.GetTopLevel(target);
            if (tl == null)
            {
                // 若视觉树尚未挂好，等一帧 Render 再尝试；依然拿不到则保底直接显示
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested || state.LastOpenGeneration != gen || !state.IsOpen)
                        return;

                    if (TopLevel.GetTopLevel(target) == null)
                    {
                        target.Opacity = 1d;
                        SlideMotion.Reset(target);
                        return;
                    }

                    RunEnter(target, fromX, fromY, origin, cts.Token);
                }, DispatcherPriority.Loaded);
                return;
            }

            RunEnter(target, fromX, fromY, origin, cts.Token);
        }

        if (TopLevel.GetTopLevel(target) != null || hostPopup == null)
        {
            LaunchAnimation();
        }
        else
        {
            void OnPopupOpened(object? s, EventArgs e)
            {
                hostPopup.Opened -= OnPopupOpened;
                LaunchAnimation();
            }

            hostPopup.Opened += OnPopupOpened;
            Dispatcher.UIThread.Post(() =>
            {
                hostPopup.Opened -= OnPopupOpened;
                if (!cts.IsCancellationRequested && state.LastOpenGeneration == gen && state.IsOpen
                    && target.Opacity < 0.01)
                {
                    LaunchAnimation();
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private static void RunEnter(
        Control target,
        double fromX,
        double fromY,
        RelativePoint origin,
        CancellationToken token)
    {
        _ = SlideMotion.AnimateTransformAndOpacityAsync(
            target,
            fromX, 0,
            fromY, 0,
            InitialScale, 1d,
            0d, 1d,
            MotionTokens.S,
            MotionTokens.Enter(),
            resetTransformOnComplete: true,
            renderTransformOrigin: origin,
            cancellationToken: token);
    }

    private static void ResetTarget(Control target)
    {
        if (States.TryGetValue(target, out var state))
        {
            CancelState(state);
            state.IsOpen = false;
        }

        target.Opacity = 1d;
        SlideMotion.Reset(target);
    }

    private static void CancelState(PopupAnimState state)
    {
        state.LastOpenGeneration++;
        var previous = state.Cts;
        state.Cts = null;
        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }
}
