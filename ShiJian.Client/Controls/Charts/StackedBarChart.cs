using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Librarys;

namespace ShiJian.Client.Controls.Charts;

/// <summary>
/// 概览页用的紧凑堆叠柱图（横向 24 小时 / 7 天，纵向按分类堆叠）。
/// 全部自绘：没有坐标轴、没有弹层，只有柱子和按需抽稀的刻度，
/// 兼顾"一眼看出时段分布"和"不占地方"。
/// </summary>
public class StackedBarChart : Control
{
    /// <summary>
    /// 数据源：每项是一条分类序列，Values 与 LabelCount 对齐，Color 决定堆叠色
    /// </summary>
    public static readonly StyledProperty<IEnumerable<ChartsDataModel>?> SeriesProperty =
        AvaloniaProperty.Register<StackedBarChart, IEnumerable<ChartsDataModel>?>(nameof(Series));

    /// <summary>
    /// 底部刻度文本
    /// </summary>
    public static readonly StyledProperty<string[]?> LabelsProperty =
        AvaloniaProperty.Register<StackedBarChart, string[]?>(nameof(Labels));

    /// <summary>
    /// 进场进度（0~1），柱高按它缩放，用来做生长动画
    /// </summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<StackedBarChart, double>(nameof(Progress), 1d);

    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<StackedBarChart, IBrush?>(nameof(AxisBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<StackedBarChart, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> MutedTextBrushProperty =
        AvaloniaProperty.Register<StackedBarChart, IBrush?>(nameof(MutedTextBrush));

    public static readonly StyledProperty<IBrush?> HoverBrushProperty =
        AvaloniaProperty.Register<StackedBarChart, IBrush?>(nameof(HoverBrush));

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<StackedBarChart, string?>(nameof(EmptyText));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<StackedBarChart, bool>(nameof(IsLoading));

    /// <summary>
    /// 对比序列（如「昨日同小时」总量），与 Series 的列一一对应；
    /// 不为空时在每根柱子后面画一根淡色柱，用来读出相对高低
    /// </summary>
    public static readonly StyledProperty<IEnumerable<double>?> CompareValuesProperty =
        AvaloniaProperty.Register<StackedBarChart, IEnumerable<double>?>(nameof(CompareValues));

    public static readonly StyledProperty<IBrush?> CompareBrushProperty =
        AvaloniaProperty.Register<StackedBarChart, IBrush?>(nameof(CompareBrush));

    private const double LabelRowHeight = 18;
    private const double TopPadding = 16;
    private const double MinLabelSpacing = 26;

    private int _hoverIndex = -1;
    private int _columnCount;
    private DispatcherTimer? _animTimer;
    private DateTime _animStart;
    private bool _playOnNextRender = true;

    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private IBrush? _fallbackBrush;


    public StackedBarChart()
    {
        Focusable = true;
        IsTabStop = true;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || _columnCount <= 0) return;

        switch (e.Key)
        {
            case Key.Left:
                _hoverIndex = _hoverIndex <= 0 ? _columnCount - 1 : _hoverIndex - 1;
                break;
            case Key.Right:
                _hoverIndex = _hoverIndex < 0 || _hoverIndex >= _columnCount - 1 ? 0 : _hoverIndex + 1;
                break;
            case Key.Escape:
                if (_hoverIndex < 0) return;
                _hoverIndex = -1;
                break;
            default:
                return;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    static StackedBarChart()
    {
        AffectsRender<StackedBarChart>(SeriesProperty, LabelsProperty, ProgressProperty,
            TextBrushProperty, MutedTextBrushProperty, AxisBrushProperty, HoverBrushProperty, EmptyTextProperty,
            IsLoadingProperty, CompareValuesProperty, CompareBrushProperty);
    }

    public IEnumerable<ChartsDataModel>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string[]? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? MutedTextBrush
    {
        get => GetValue(MutedTextBrushProperty);
        set => SetValue(MutedTextBrushProperty, value);
    }

    public IBrush? HoverBrush
    {
        get => GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public IEnumerable<double>? CompareValues
    {
        get => GetValue(CompareValuesProperty);
        set => SetValue(CompareValuesProperty, value);
    }

    public IBrush? CompareBrush
    {
        get => GetValue(CompareBrushProperty);
        set => SetValue(CompareBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SeriesProperty || change.Property == LabelsProperty)
        {
            _brushCache.Clear();
            _hoverIndex = -1;
            _playOnNextRender = true;
            InvalidateVisual();
        }
    }

    #region Render

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        if (_playOnNextRender)
        {
            _playOnNextRender = false;
            // 不能在渲染流程里改属性（会触发"Visual was invalidated during the render pass"），
            // 推迟到本轮渲染之后再起动画
            Dispatcher.UIThread.Post(StartEnterAnimation, DispatcherPriority.Background);
        }

        var series = BuildSeries(out var columnCount);
        _columnCount = columnCount;
        if (columnCount == 0 || series.Count == 0)
        {
            DrawEmpty(context, width, height);
            return;
        }

        var labels = Labels;
        var labelRow = labels is { Length: > 0 } ? LabelRowHeight : 0;
        var plotHeight = height - labelRow - TopPadding;
        if (plotHeight <= 4) return;

        var totals = new double[columnCount];
        foreach (var item in series)
        {
            var values = item.Values;
            if (values == null) continue;
            var count = Math.Min(values.Length, columnCount);
            for (var i = 0; i < count; i++) totals[i] += values[i];
        }

        var compare = CompareValues?.ToArray();
        var compareTotals = new double[columnCount];
        var hasCompare = false;
        if (compare is { Length: > 0 })
        {
            var count = Math.Min(compare.Length, columnCount);
            for (var i = 0; i < count; i++)
            {
                compareTotals[i] = compare[i];
                if (compare[i] > 0) hasCompare = true;
            }
        }

        var maxTotal = totals.Length == 0 ? 0 : totals.Max();
        if (hasCompare) maxTotal = Math.Max(maxTotal, compareTotals.Max());
        if (maxTotal <= 0)
        {
            DrawEmpty(context, width, height);
            return;
        }

        var progress = Math.Clamp(Progress, 0, 1);
        var slot = width / columnCount;
        var barWidth = Math.Clamp(slot * 0.62, 4, 26);
        var baseline = height - labelRow;

        // 悬停列的浅色背景，先画，避免盖住柱子
        if (_hoverIndex >= 0 && _hoverIndex < columnCount && HoverBrush is { } hover)
        {
            var hoverRect = new Rect(slot * _hoverIndex + 1, TopPadding - 6, slot - 2, plotHeight + 6);
            context.DrawRectangle(hover, null, new RoundedRect(hoverRect, 5));
        }

        // 对比柱：画在当前柱后面、并且略宽一圈，只看轮廓就能读出高低差
        if (hasCompare)
        {
            var ghostBrush = CompareBrush ?? new SolidColorBrush(Color.FromArgb(46, 128, 128, 128));
            var ghostWidth = Math.Min(barWidth + 8, slot - 2);
            for (var i = 0; i < columnCount; i++)
            {
                if (compareTotals[i] <= 0) continue;

                var ghostHeight = compareTotals[i] / maxTotal * plotHeight * progress;
                if (ghostHeight < 1) continue;

                var gx = slot * i + (slot - ghostWidth) / 2;
                var rect = new Rect(gx, baseline - ghostHeight, ghostWidth, ghostHeight);
                context.DrawRectangle(ghostBrush, null, new RoundedRect(rect, Math.Min(4, ghostHeight / 2)));
            }
        }

        for (var i = 0; i < columnCount; i++)
        {
            if (totals[i] <= 0) continue;

            var x = slot * i + (slot - barWidth) / 2;
            var usedHeight = 0d;

            foreach (var item in series)
            {
                var values = item.Values;
                if (values == null || i >= values.Length) continue;

                var value = values[i];
                if (value <= 0) continue;

                var segHeight = value / maxTotal * plotHeight * progress;
                if (segHeight < 0.6) continue;

                usedHeight += segHeight;

                // 段与段之间留 1.5 的缝，堆叠才不会糊成一块
                var drawHeight = Math.Max(1, segHeight - 1.5);
                var rect = new Rect(x, baseline - usedHeight, barWidth, drawHeight);
                var radius = Math.Min(3, drawHeight / 2);
                context.DrawRectangle(GetBrush(item.Color), null, new RoundedRect(rect, radius));
            }
        }

        DrawBaseline(context, width, baseline);
        DrawLabels(context, labels, slot, columnCount, height);
        DrawHoverValue(context, totals, maxTotal, slot, plotHeight, baseline, width);

        FocusVisual.Draw(context, new Size(width, height), IsKeyboardFocusWithin);
    }

    private List<ChartsDataModel> BuildSeries(out int columnCount)
    {
        columnCount = 0;
        var series = Series?.Where(m => m?.Values is { Length: > 0 }).ToList() ?? [];

        // 从新到旧排序：时长大的分类压在下面，视觉上更稳
        series.Sort((a, b) => Sum(b.Values).CompareTo(Sum(a.Values)));

        foreach (var item in series)
            columnCount = Math.Max(columnCount, item.Values.Length);

        if (Labels is { Length: > 0 } labels)
            columnCount = Math.Max(columnCount, labels.Length);

        return series;

        static double Sum(double[] values)
        {
            var total = 0d;
            foreach (var v in values) total += v;
            return total;
        }
    }

    private void DrawBaseline(DrawingContext context, double width, double y)
    {
        if (AxisBrush is not { } brush) return;

        var pen = new Pen(brush, 1);
        context.DrawLine(pen, new Point(0, y + 0.5), new Point(width, y + 0.5));
    }

    private void DrawLabels(DrawingContext context, string[]? labels, double slot, int columnCount, double height)
    {
        if (labels is not { Length: > 0 } || TextBrush is not { } brush) return;

        // 间距不够就抽稀，保证刻度不重叠
        var step = Math.Max(1, (int)Math.Ceiling(MinLabelSpacing / Math.Max(slot, 1)));
        var count = Math.Min(labels.Length, columnCount);
        var baseline = height - LabelRowHeight;

        for (var i = 0; i < count; i += step)
        {
            var text = MakeText(labels[i], 10, brush);
            var x = slot * i + (slot - text.Width) / 2;
            context.DrawText(text, new Point(x, baseline + 3));
        }
    }

    private void DrawHoverValue(DrawingContext context, double[] totals, double maxTotal,
        double slot, double plotHeight, double baseline, double width)
    {
        if (_hoverIndex < 0 || _hoverIndex >= totals.Length) return;
        if (totals[_hoverIndex] <= 0 || TextBrush is not { } brush) return;

        var top = baseline - totals[_hoverIndex] / maxTotal * plotHeight * Math.Clamp(Progress, 0, 1);
        var text = MakeText(FormatDuration(totals[_hoverIndex]), 11, brush);

        var x = slot * _hoverIndex + slot / 2 - text.Width / 2;
        x = Math.Clamp(x, 0, Math.Max(0, width - text.Width));
        var y = Math.Max(0, top - text.Height - 4);

        context.DrawText(text, new Point(x, y));
    }

    private void DrawEmpty(DrawingContext context, double width, double height)
    {
        if (IsLoading) return;
        var text = MakeText(EmptyText ?? "—", 12, MutedTextBrush ?? TextBrush);
        context.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }

    #endregion

    #region Hover

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var series = BuildSeries(out var columnCount);
        if (columnCount <= 0 || series.Count == 0)
        {
            SetHover(-1);
            return;
        }

        var slot = Bounds.Width / columnCount;
        var index = slot <= 0 ? -1 : (int)(e.GetPosition(this).X / slot);
        SetHover(index >= 0 && index < columnCount ? index : -1);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    private void SetHover(int index)
    {
        if (_hoverIndex == index) return;
        _hoverIndex = index;
        InvalidateVisual();
    }

    #endregion

    #region Animation

    private void StartEnterAnimation()
    {
        _animTimer?.Stop();

        // 动效关闭时不跑生长动画，直接给出完成态
        if (!Motion.IsEnabled)
        {
            _animTimer = null;
            Progress = 1;
            return;
        }

        Progress = 0;
        _animStart = DateTime.UtcNow;

        _animTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnAnimTick);
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        const double durationMs = 260;

        var elapsed = (DateTime.UtcNow - _animStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        // ease-out cubic：起步快、收尾稳
        Progress = 1 - Math.Pow(1 - t, 3);

        if (t >= 1)
        {
            _animTimer?.Stop();
            _animTimer = null;
            Progress = 1;
        }
    }

    #endregion

    private IBrush GetBrush(string? color)
    {
        _fallbackBrush ??= new SolidColorBrush(Color.Parse("#5E81AC"));

        if (string.IsNullOrWhiteSpace(color)) return _fallbackBrush;
        if (_brushCache.TryGetValue(color, out var cached)) return cached;

        IBrush brush;
        try
        {
            brush = new SolidColorBrush(Color.Parse(color));
        }
        catch
        {
            brush = _fallbackBrush;
        }

        _brushCache[color] = brush;
        return brush;
    }

    private static FormattedText MakeText(string text, double size, IBrush? brush)
    {
        return new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            size,
            brush ?? Brushes.Gray);
    }

    private static string FormatDuration(double seconds)
    {
        var total = (int)Math.Round(seconds);
        if (total < 60) return $"{total}s";

        var minutes = total / 60;
        if (minutes < 60) return $"{minutes}m";

        var hours = minutes / 60;
        var restMinutes = minutes % 60;
        return restMinutes == 0 ? $"{hours}h" : $"{hours}h{restMinutes}m";
    }
}
