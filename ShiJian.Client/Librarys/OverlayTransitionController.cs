using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace ShiJian.Client.Librarys;

/// <summary>
/// 窗口级遮罩与居中模态卡片（ConfirmDialog / ActionDialog / InputModal / PageModalOverlay / SearchOverlay）
/// 的异步进退场生命周期控制器。
///
/// 通过单调递增版本号 <see cref="Generation"/> 解决快速“开 → 关 → 开”竞态：
/// 关闭动画启动时先禁用命中检测（<see cref="InputElement.IsHitTestVisible"/> = false），
/// 播放完 150ms Leave 动画后，仅当版本号仍匹配本次关闭且未被新一轮 Open 抢占时，才执行 <c>IsVisible = false</c>。
/// </summary>
public sealed class OverlayTransitionController
{
    private const double DefaultScale = 0.97;

    private int _generation;
    private CancellationTokenSource? _cts;

    /// <summary>当前过渡代际（单调递增，供测试与外部诊断验证状态版本）</summary>
    public int Generation => _generation;

    /// <summary>中止当前未完成过渡动画</summary>
    public void Cancel()
    {
        _generation++;
        var previous = _cts;
        _cts = null;
        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    /// <summary>以渐显遮罩 + 轻幅缩放/位移打开浮层容器</summary>
    public void Open(
        Control? container,
        Control? scrim,
        Control? card,
        double fromY = 8,
        double fromScale = DefaultScale)
    {
        if (container == null) return;

        var (_, token) = BeginTransition();

        var wasVisible = container.IsVisible;
        var scrimFrom = wasVisible && scrim != null ? scrim.Opacity : 0d;
        var cardFromOpacity = wasVisible && card != null ? card.Opacity : 0d;

        container.IsVisible = true;
        container.IsHitTestVisible = true;

        if (!Motion.IsEnabled || TopLevel.GetTopLevel(container) == null)
        {
            if (scrim != null) scrim.Opacity = 1d;
            if (card != null)
            {
                card.Opacity = 1d;
                SlideMotion.Reset(card);
            }
            return;
        }

        if (scrim != null)
        {
            _ = SlideMotion.AnimateOpacityAsync(
                scrim,
                scrimFrom,
                1d,
                MotionTokens.S,
                MotionTokens.Standard(),
                cancellationToken: token);
        }

        if (card != null)
        {
            _ = SlideMotion.AnimateTransformAndOpacityAsync(
                card,
                0, 0,
                fromY, 0,
                fromScale, 1d,
                cardFromOpacity, 1d,
                MotionTokens.M,
                MotionTokens.Enter(),
                resetTransformOnComplete: true,
                renderTransformOrigin: RelativePoint.Center,
                cancellationToken: token);
        }
    }

    /// <summary>先拦截点击并播放退场动画，完成后校验代际一致性再隐藏容器</summary>
    public async Task CloseAsync(
        Control? container,
        Control? scrim,
        Control? card,
        double toY = 6,
        double toScale = DefaultScale,
        Action? onHidden = null)
    {
        if (container == null)
        {
            onHidden?.Invoke();
            return;
        }

        var (gen, token) = BeginTransition();

        container.IsHitTestVisible = false;

        if (!Motion.IsEnabled || !container.IsVisible || TopLevel.GetTopLevel(container) == null)
        {
            if (scrim != null) scrim.Opacity = 0d;
            if (card != null)
            {
                card.Opacity = 0d;
                SlideMotion.Reset(card);
            }
            container.IsVisible = false;
            onHidden?.Invoke();
            return;
        }

        var scrimFrom = scrim?.Opacity ?? 0d;
        var cardFromOpacity = card?.Opacity ?? 1d;

        try
        {
            var scrimTask = scrim != null
                ? SlideMotion.AnimateOpacityAsync(
                    scrim,
                    scrimFrom,
                    0d,
                    MotionTokens.S,
                    MotionTokens.Leave(),
                    cancellationToken: token)
                : Task.CompletedTask;

            var cardTask = card != null
                ? SlideMotion.AnimateTransformAndOpacityAsync(
                    card,
                    0, 0,
                    0, toY,
                    1d, toScale,
                    cardFromOpacity, 0d,
                    MotionTokens.S,
                    MotionTokens.Leave(),
                    resetTransformOnComplete: false,
                    renderTransformOrigin: RelativePoint.Center,
                    cancellationToken: token)
                : Task.CompletedTask;

            await Task.WhenAll(scrimTask, cardTask);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_generation != gen || token.IsCancellationRequested)
            return;

        container.IsVisible = false;
        if (card != null) SlideMotion.Reset(card);
        onHidden?.Invoke();
    }

    private (int Generation, CancellationToken Token) BeginTransition()
    {
        var previous = _cts;
        var next = new CancellationTokenSource();
        _cts = next;
        var gen = ++_generation;

        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        return (gen, next.Token);
    }
}
