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
/// 概览页用的分类占比环形图。自绘圆弧，中心显示总计，
/// 悬停某一段时中心切换成该分类的名称与时长。
/// </summary>
public class DonutChart : Control
{
    public static readonly StyledProperty<IEnumerable<ChartsDataModel>?> SeriesProperty =
        AvaloniaProperty.Register<DonutChart, IEnumerable<ChartsDataModel>?>(nameof(Series));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(Progress), 1d);

    /// <summary>
    /// 环的粗细
    /// </summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(Thickness), 16d);

    public static readonly StyledProperty<string?> CenterTextProperty =
        AvaloniaProperty.Register<DonutChart, string?>(nameof(CenterText));

    public static readonly StyledProperty<string?> CenterCaptionProperty =
        AvaloniaProperty.Register<DonutChart, string?>(nameof(CenterCaption));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<DonutChart, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> MutedTextBrushProperty =
        AvaloniaProperty.Register<DonutChart, IBrush?>(nameof(MutedTextBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<DonutChart, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<DonutChart, string?>(nameof(EmptyText));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<DonutChart, bool>(nameof(IsLoading));

    private const double SliceGapDegrees = 1.6;
    private const double DurationMs = 260;

    private int _hoverIndex = -1;
    private DispatcherTimer? _animTimer;
    private DateTime _animStart;
    private bool _playOnNextRender = true;
    private List<(ChartsDataModel Model, double Value)> _slices = [];
    private int _sliceCount;

    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private IBrush? _fallbackBrush;


    public DonutChart()
    {
        Focusable = true;
        IsTabStop = true;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || _sliceCount <= 0) return;

        switch (e.Key)
        {
            case Key.Left:
                _hoverIndex = _hoverIndex <= 0 ? _sliceCount - 1 : _hoverIndex - 1;
                break;
            case Key.Right:
                _hoverIndex = _hoverIndex < 0 || _hoverIndex >= _sliceCount - 1 ? 0 : _hoverIndex + 1;
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

    static DonutChart()
    {
        AffectsRender<DonutChart>(SeriesProperty, ProgressProperty, ThicknessProperty,
            CenterTextProperty, CenterCaptionProperty, TextBrushProperty, MutedTextBrushProperty,
            TrackBrushProperty, EmptyTextProperty, IsLoadingProperty);
    }

    public IEnumerable<ChartsDataModel>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public string? CenterText
    {
        get => GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    public string? CenterCaption
    {
        get => GetValue(CenterCaptionProperty);
        set => SetValue(CenterCaptionProperty, value);
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

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SeriesProperty)
        {
            _brushCache.Clear();
            _hoverIndex = -1;
            _playOnNextRender = true;
            InvalidateVisual();
        }
    }

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

        var thickness = Math.Max(4, Thickness);
        var radius = Math.Min(width, height) / 2 - thickness / 2 - 2;
        if (radius <= 2) return;

        var center = new Point(width / 2, height / 2);
        var innerRadius = Math.Max(0, radius - thickness / 2);
        var trackPen = new Pen(TrackBrush ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), thickness);
        context.DrawEllipse(null, trackPen, center, radius, radius);

        _slices = BuildSlices(out var total);
        _sliceCount = _slices.Count;

        if (total <= 0 || _slices.Count == 0)
        {
            DrawEmpty(context, center, innerRadius);
            FocusVisual.Draw(context, Bounds.Size, IsKeyboardFocusWithin,
                Math.Min(Bounds.Width, Bounds.Height) / 2);
            return;
        }

        var progress = Math.Clamp(Progress, 0, 1);
        var singleSlice = _slices.Count == 1;
        var startAngle = -90d;

        for (var i = 0; i < _slices.Count; i++)
        {
            var (model, value) = _slices[i];
            var sweep = value / total * 360d * progress;

            // 只有一段时不留缝，多段之间留一点点呼吸感
            var gap = singleSlice ? 0 : SliceGapDegrees;
            var drawSweep = Math.Max(0, sweep - gap);
            if (drawSweep <= 0.2) continue;

            var isHover = i == _hoverIndex;
            var brush = GetBrush(model.Color);
            var pen = new Pen(brush, isHover ? thickness + 4 : thickness)
            {
                LineCap = PenLineCap.Flat
            };

            DrawArc(context, center, radius, startAngle + gap / 2, drawSweep, pen);
            startAngle += sweep;
        }

        DrawCenterForHover(context, center, innerRadius, total);

        FocusVisual.Draw(context, Bounds.Size, IsKeyboardFocusWithin,
            Math.Min(Bounds.Width, Bounds.Height) / 2);
    }

    private List<(ChartsDataModel Model, double Value)> BuildSlices(out double total)
    {
        var slices = new List<(ChartsDataModel, double)>();
        total = 0;

        foreach (var item in Series ?? [])
        {
            if (item == null) continue;

            var value = item.Value > 0 ? item.Value : (item.Values?.Sum() ?? 0);
            if (value <= 0) continue;

            slices.Add((item, value));
            total += value;
        }

        slices.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return slices;
    }

    private static void DrawArc(DrawingContext context, Point center, double radius,
        double startAngleDegrees, double sweepDegrees, IPen pen)
    {
        // 单分类时扫角接近 360°，ArcTo 起终点重合会畸变成一条空路径，直接画整圆
        if (sweepDegrees >= 359.5)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var start = PointOnCircle(center, radius, startAngleDegrees);
        var end = PointOnCircle(center, radius, startAngleDegrees + sweepDegrees);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.ArcTo(end, new Size(radius, radius), 0,
                sweepDegrees > 180, SweepDirection.Clockwise);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private void DrawCenterForHover(DrawingContext context, Point center, double innerRadius, double total)
    {
        if (_hoverIndex >= 0 && _hoverIndex < _slices.Count)
        {
            var (model, value) = _slices[_hoverIndex];
            DrawCenter(context, center, innerRadius, FormatDuration(value), model.Name);
            return;
        }

        var text = CenterText;
        if (string.IsNullOrEmpty(text))
            text = FormatDuration(total);

        DrawCenter(context, center, innerRadius, text, CenterCaption);
    }

    private void DrawEmpty(DrawingContext context, Point center, double innerRadius)
    {
        if (IsLoading || string.IsNullOrEmpty(EmptyText)) return;

        var safeRadius = ComputeSafeRadius(innerRadius);
        if (safeRadius <= 6) return;

        var brush = MutedTextBrush ?? TextBrush ?? Brushes.Gray;
        var (text, origin) = LayoutCenteredText(
            EmptyText, center, safeRadius, verticalShift: 0,
            maxFontSize: 12, minFontSize: 9.5, step: 0.5,
            brush, FontWeight.Normal, allowWrap: true);

        context.DrawText(text, origin);
    }

    private void DrawCenter(DrawingContext context, Point center, double innerRadius, string? main, string? caption)
    {
        var safeRadius = ComputeSafeRadius(innerRadius);
        if (safeRadius <= 6) return;

        if (!string.IsNullOrEmpty(main))
        {
            var brush = TextBrush ?? Brushes.Black;
            var shift = string.IsNullOrEmpty(caption) ? 0 : -7;
            var maxFontSize = Math.Min(20, Math.Max(12, safeRadius * 0.52));
            var (text, origin) = LayoutCenteredText(
                main, center, safeRadius, verticalShift: shift,
                maxFontSize, minFontSize: 11, step: 1,
                brush, FontWeight.SemiBold, allowWrap: false);
            context.DrawText(text, origin);
        }

        if (string.IsNullOrEmpty(caption)) return;

        var captionShift = string.IsNullOrEmpty(main) ? 0 : 12;
        var (captionText, captionOrigin) = LayoutCenteredText(
            caption, center, safeRadius, verticalShift: captionShift,
            maxFontSize: 11, minFontSize: 9.5, step: 0.5,
            MutedTextBrush ?? TextBrush, FontWeight.Normal, allowWrap: false);

        context.DrawText(captionText, captionOrigin);
    }

    internal static double ComputeSafeRadius(double innerRadius) =>
        innerRadius <= 4 ? 0 : Math.Max(0, innerRadius - Math.Clamp(innerRadius * 0.12, 3, 6));

    internal static double SafeChordWidth(double safeRadius, double maxAbsDy) =>
        safeRadius <= 0 || maxAbsDy >= safeRadius
            ? 0
            : 2 * Math.Sqrt(safeRadius * safeRadius - maxAbsDy * maxAbsDy);

    internal static (FormattedText Text, Point Origin) LayoutCenteredText(
        string content,
        Point center,
        double safeRadius,
        double verticalShift,
        double maxFontSize,
        double minFontSize,
        double step,
        IBrush? brush,
        FontWeight weight,
        bool allowWrap)
    {
        FormattedText? fallback = null;

        for (var size = maxFontSize; size >= minFontSize - 0.01; size -= step)
        {
            var ft = MakeText(content, size, brush, weight);
            fallback = ft;

            var topDy = -ft.Height / 2 + verticalShift;
            var bottomDy = ft.Height / 2 + verticalShift;
            var maxAbsDy = Math.Max(Math.Abs(topDy), Math.Abs(bottomDy));
            var chordWidth = SafeChordWidth(safeRadius, maxAbsDy);

            if (chordWidth > 0 && ft.Width <= chordWidth)
                return (ft, new Point(center.X - ft.Width / 2, center.Y + topDy));
        }

        // 即使最小字号仍超出安全弦宽，启用限宽居中（空态允许最多两行换行，数据/标签单行截断）
        var constrained = fallback ?? MakeText(content, minFontSize, brush, weight);
        var singleHeight = constrained.Height;
        if (allowWrap)
        {
            var maxLines = safeRadius >= singleHeight * 1.4 ? 2 : 1;
            var boxHalfHeight = singleHeight * maxLines / 2;
            var boxWidth = Math.Max(12, SafeChordWidth(safeRadius, Math.Abs(verticalShift) + boxHalfHeight));
            constrained.MaxTextWidth = boxWidth;
            constrained.MaxTextHeight = singleHeight * maxLines + 1;
            constrained.MaxLineCount = maxLines;
            constrained.TextAlignment = TextAlignment.Center;
            constrained.Trimming = TextTrimming.CharacterEllipsis;
            return (constrained, new Point(center.X - boxWidth / 2, center.Y + verticalShift - constrained.Height / 2));
        }

        var singleTopDy = -singleHeight / 2 + verticalShift;
        var singleBottomDy = singleHeight / 2 + verticalShift;
        var singleMaxDy = Math.Max(Math.Abs(singleTopDy), Math.Abs(singleBottomDy));
        var singleWidth = Math.Max(12, SafeChordWidth(safeRadius, singleMaxDy));
        constrained.MaxTextWidth = singleWidth;
        constrained.MaxTextHeight = singleHeight + 1;
        constrained.MaxLineCount = 1;
        constrained.TextAlignment = TextAlignment.Center;
        constrained.Trimming = TextTrimming.CharacterEllipsis;
        return (constrained, new Point(center.X - singleWidth / 2, center.Y + singleTopDy));
    }

    #region Hover

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        var width = Bounds.Width;
        var height = Bounds.Height;

        var center = new Point(width / 2, height / 2);
        var dx = position.X - center.X;
        var dy = position.Y - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        var thickness = Math.Max(4, Thickness);
        var radius = Math.Min(width, height) / 2 - thickness / 2 - 2;

        if (radius <= 2 || distance < radius - thickness / 2 - 4 || distance > radius + thickness / 2 + 4)
        {
            SetHover(-1);
            return;
        }

        // 角度转成从 12 点开始顺时针的累计角度
        var angle = Math.Atan2(dy, dx) * 180d / Math.PI;
        angle = (angle + 90 + 360) % 360;

        var total = _slices.Sum(s => s.Value);
        if (total <= 0)
        {
            SetHover(-1);
            return;
        }

        var cursor = angle / 360d * total;
        var accumulated = 0d;

        for (var i = 0; i < _slices.Count; i++)
        {
            accumulated += _slices[i].Value;
            if (cursor <= accumulated)
            {
                SetHover(i);
                return;
            }
        }

        SetHover(-1);
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
        var elapsed = (DateTime.UtcNow - _animStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / DurationMs, 0, 1);

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

    private static FormattedText MakeText(string text, double size, IBrush? brush,
        FontWeight weight = FontWeight.Normal)
    {
        return new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight),
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
