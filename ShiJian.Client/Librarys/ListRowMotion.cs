using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using ShiJian.Client.Logging;

namespace ShiJian.Client.Librarys;

/// <summary>
/// 列表行进出动效（对齐 docs/DESIGN-SP-ALIGN.md）：
/// - 首次/换数据时前 N 行 40ms 错峰淡入（不修改位移，防止虚拟化列表相邻行跳动，且滚动时不触发）
/// - 删除行时执行高度与透明度协同折叠收起，并在结束后复原容器本地属性以保证虚拟化复用安全
/// </summary>
public static class ListRowMotion
{
    private const int StaggerStepMs = 40;

    private sealed class StaggerState
    {
        public int Generation;
        public CancellationTokenSource? Cts;
    }

    private static readonly ConditionalWeakTable<ItemsControl, StaggerState> States = new();

    /// <summary>对前 <paramref name="maxRows"/> 个已实例化行播放 40ms 错峰淡入</summary>
    public static async Task PlayStaggerInAsync(
        ItemsControl? listControl,
        int maxRows = 10,
        CancellationToken ct = default)
    {
        if (listControl == null || maxRows <= 0) return;

        var state = States.GetOrCreateValue(listControl);
        CancellationTokenSource? prevCts;
        CancellationTokenSource currentCts;
        int myGen;

        lock (state)
        {
            prevCts = state.Cts;
            myGen = ++state.Generation;
            currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            state.Cts = currentCts;
        }

        prevCts?.Cancel();
        prevCts?.Dispose();

        if (!Motion.IsEnabled)
        {
            RestoreRealizedOpacity(listControl, maxRows);
            return;
        }

        var rows = new List<Control>(maxRows);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            currentCts.Token.ThrowIfCancellationRequested();

            lock (state)
            {
                if (state.Generation != myGen || currentCts.IsCancellationRequested)
                    return;
            }

            var itemCount = listControl.ItemCount;
            var limit = Math.Min(itemCount, maxRows);
            for (var i = 0; i < limit; i++)
            {
                if (listControl.ContainerFromIndex(i) is { } container)
                {
                    container.Opacity = 0;
                    rows.Add(container);
                }
            }

            if (rows.Count == 0) return;

            var tasks = new List<Task>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var delay = TimeSpan.FromMilliseconds(StaggerStepMs * i);
                tasks.Add(FadeInRowAsync(rows[i], delay, currentCts.Token));
            }

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // 新一轮刷新或卸载已接管，在 finally 恢复可见性
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ListRowMotion] 列表错峰进场异常，已恢复可见态: {ex.Message}");
        }
        finally
        {
            foreach (var row in rows)
                row.Opacity = 1;

            lock (state)
            {
                if (state.Generation == myGen && ReferenceEquals(state.Cts, currentCts))
                    state.Cts = null;
            }

            currentCts.Dispose();
        }
    }

    /// <summary>行删除时先按 Height + Opacity 折叠退场，并在结束后复原容器尺寸以安全复用</summary>
    public static async Task CollapseRowAsync(Control? row, CancellationToken ct = default)
    {
        if (row == null || !Motion.IsEnabled) return;

        var startHeight = row.Bounds.Height;
        if (startHeight <= 0 || double.IsNaN(startHeight)) return;

        var prevClip = row.ClipToBounds;
        try
        {
            row.ClipToBounds = true;
            var anim = new Animation
            {
                Duration = MotionTokens.S,
                Easing = MotionTokens.Leave(),
                FillMode = FillMode.None,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter(Layoutable.HeightProperty, startHeight),
                            new Setter(Visual.OpacityProperty, 1d)
                        }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter(Layoutable.HeightProperty, 0d),
                            new Setter(Visual.OpacityProperty, 0d)
                        }
                    }
                }
            };

            await anim.RunAsync(row, ct);
        }
        catch (OperationCanceledException)
        {
            // 取消时直接走 finally 复原
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ListRowMotion] 行折叠动画异常: {ex.Message}");
        }
        finally
        {
            row.Height = double.NaN;
            row.Opacity = 1;
            row.ClipToBounds = prevClip;
        }
    }

    private static async Task FadeInRowAsync(Control row, TimeSpan delay, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);

        ct.ThrowIfCancellationRequested();

        var anim = new Animation
        {
            Duration = MotionTokens.S,
            Easing = MotionTokens.Enter(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } }
            }
        };

        await anim.RunAsync(row, ct);
    }

    private static void RestoreRealizedOpacity(ItemsControl listControl, int maxRows)
    {
        var limit = Math.Min(listControl.ItemCount, maxRows);
        for (var i = 0; i < limit; i++)
        {
            if (listControl.ContainerFromIndex(i) is { } container)
                container.Opacity = 1;
        }
    }
}
