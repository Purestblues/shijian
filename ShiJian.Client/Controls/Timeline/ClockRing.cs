using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using ShiJian.Client.Controls.Base;

namespace ShiJian.Client.Controls.Timeline;

/// <summary>
/// 环形时钟：把一段周期画成表盘，扇区就是真实的使用时段。
/// 日 = 24 小时表盘，周 = 7 天表盘，月 = 当月天数表盘，年 = 12 个月表盘。
/// 悬停显示该段的名称与时间，点击交给 <see cref="ClickCommand"/>。
/// </summary>
public class ClockRing : Control, ICustomHitTest
{
    public static readonly StyledProperty<IEnumerable<ClockSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<ClockRing, IEnumerable<ClockSegment>?>(nameof(Segments));

    public static readonly StyledProperty<ClockTickMode> TickModeProperty =
        AvaloniaProperty.Register<ClockRing, ClockTickMode>(nameof(TickMode));

    public static readonly StyledProperty<DateTime> RangeStartProperty =
        AvaloniaProperty.Register<ClockRing, DateTime>(nameof(RangeStart), DateTime.Today);

    public static readonly StyledProperty<DateTime> RangeEndProperty =
        AvaloniaProperty.Register<ClockRing, DateTime>(nameof(RangeEnd), DateTime.Today.AddDays(1));

    /// <summary>是否按分类着色（false = 按应用着色）</summary>
    public static readonly StyledProperty<bool> UseCategoryColorProperty =
        AvaloniaProperty.Register<ClockRing, bool>(nameof(UseCategoryColor));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<ClockRing, double>(nameof(Thickness), 22d);

    public static readonly StyledProperty<string?> CenterTextProperty =
        AvaloniaProperty.Register<ClockRing, string?>(nameof(CenterText));

    public static readonly StyledProperty<string?> CenterCaptionProperty =
        AvaloniaProperty.Register<ClockRing, string?>(nameof(CenterCaption));

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<ClockRing, string?>(nameof(EmptyText));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<ClockRing, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> MutedTextBrushProperty =
        AvaloniaProperty.Register<ClockRing, IBrush?>(nameof(MutedTextBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ClockRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> HoverBackgroundBrushProperty =
        AvaloniaProperty.Register<ClockRing, IBrush?>(nameof(HoverBackgroundBrush));

    public static readonly StyledProperty<ICommand?> ClickCommandProperty =
        AvaloniaProperty.Register<ClockRing, ICommand?>(nameof(ClickCommand));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<ClockRing, bool>(nameof(IsLoading));

    public static readonly DirectProperty<ClockRing, ClockSegment?> HoveredSegmentProperty =
        AvaloniaProperty.RegisterDirect<ClockRing, ClockSegment?>(
            nameof(HoveredSegment),
            o => o.HoveredSegment,
            (o, v) => o.HoveredSegment = v);

    /// <summary>小于这个角度也要画出来，否则几秒的碎片会完全看不见</summary>
    private const double MinSweepDegrees = 1.1;

    private const double LabelSpace = 26;

    private readonly List<(ClockSegment Segment, double Start, double Sweep)> _hitMap = [];
    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private ClockSegment? _hoveredSegment;
    private double? _hoverFraction;
    private IBrush? _fallbackBrush;

    static ClockRing()
    {
        AffectsRender<ClockRing>(SegmentsProperty, TickModeProperty, RangeStartProperty, RangeEndProperty,
            UseCategoryColorProperty, ThicknessProperty, CenterTextProperty, CenterCaptionProperty,
            EmptyTextProperty, TextBrushProperty, MutedTextBrushProperty, TrackBrushProperty,
            HoverBackgroundBrushProperty, IsLoadingProperty);
    }

    public IEnumerable<ClockSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public ClockTickMode TickMode
    {
        get => GetValue(TickModeProperty);
        set => SetValue(TickModeProperty, value);
    }

    public DateTime RangeStart
    {
        get => GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public DateTime RangeEnd
    {
        get => GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    public bool UseCategoryColor
    {
        get => GetValue(UseCategoryColorProperty);
        set => SetValue(UseCategoryColorProperty, value);
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

    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
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

    public IBrush? HoverBackgroundBrush
    {
        get => GetValue(HoverBackgroundBrushProperty);
        set => SetValue(HoverBackgroundBrushProperty, value);
    }

    public ICommand? ClickCommand
    {
        get => GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public ClockSegment? HoveredSegment
    {
        get => _hoveredSegment;
        set
        {
            if (ReferenceEquals(_hoveredSegment, value)) return;
            SetAndRaise(HoveredSegmentProperty, ref _hoveredSegment, value);
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SegmentsProperty || change.Property == UseCategoryColorProperty)
        {
            _brushCache.Clear();
            HoveredSegment = null;
        }
    }

    /// <summary>
    /// 自绘控件默认只在自己画过的像素上参与命中测试，表盘中间是空心的，
    /// 悬停/点击会时灵时不灵。这里整个矩形都接受指针，具体命中哪个扇区由内部按角度算。
    /// </summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>
    /// 表盘永远是正方形：取可用宽高里较小的一边。
    /// 不自报尺寸的话，控件在居中布局里会被压成 0×0（Stretch 才靠父容器撑开）。
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(side)) side = 420;
        if (side <= 0) side = 420;

        return new Size(side, side);
    }

    /// <summary>
    /// 表盘中间是空心的，而 Avalonia 的命中测试只认「画过的地方」：
    /// 不铺一层底色，悬停就会时灵时不灵（点在环上算，点进圈里不算）。
    /// alpha = 1 肉眼看不出来，但足以让整个矩形进入命中区域。
    /// </summary>
    private static readonly IBrush HitTestFill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(HitTestFill, new Rect(Bounds.Size));

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var thickness = Math.Max(6, Thickness);
        var radius = Math.Min(width, height) / 2 - thickness / 2 - LabelSpace;
        if (radius <= 4) return;

        var center = new Point(width / 2, height / 2);

        DrawTicks(context, center, radius, thickness);

        context.DrawEllipse(null,
            new Pen(TrackBrush ?? new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)), thickness),
            center, radius, radius);

        var segments = (Segments ?? []).Where(s => s != null).OrderBy(s => s.StartFraction).ToList();
        _hitMap.Clear();

        foreach (var segment in segments)
        {
            var rawSweep = Math.Max(0, segment.EndFraction - segment.StartFraction) * 360d;
            if (rawSweep <= 0) continue;

            var sweep = Math.Max(MinSweepDegrees, rawSweep);
            var start = -90d + segment.StartFraction * 360d;
            var isHover = ReferenceEquals(segment, HoveredSegment);

            var pen = new Pen(GetBrush(segment), isHover ? thickness + 5 : thickness)
            {
                LineCap = PenLineCap.Flat
            };

            DrawArc(context, center, radius, start, sweep, pen);

            // 注意两点：
            // 1) 绘制用标准数学角（0° = 3 点方向），命中用「从 12 点开始顺时针」，差 90°，这里归一化；
            // 2) 命中存的是**真实**扫角，不是加了 MinSweep 的绘制扫角——否则几秒钟的碎片
            //    会各自「吃」掉 1° 左右，密集时段里点哪儿都会命中几分钟前的那一条。
            _hitMap.Add((segment, (start + 90 + 360) % 360, rawSweep));
        }

        // 后台刷新会整批换掉扇区（对象都变了），按鼠标所在角度重新认一次，
        // 否则每次数据刷新都会把悬停状态清空，看起来就是「悬停没反应」。
        if (_hoverFraction is { } hoverFraction)
            _hoveredSegment = FindSegmentByFraction(hoverFraction);

        DrawHoverMarker(context, center, radius, thickness);
        DrawCenter(context, center, Math.Max(16, radius - thickness / 2), segments.Count > 0);

        // 键盘焦点环：套在整块控件外沿
        FocusVisual.Draw(context, Bounds.Size, IsKeyboardFocusWithin, 12);
    }

    /// <summary>鼠标在环带上时，画一根短线标出当前位置——空档里也有反馈，不然像没反应</summary>
    private void DrawHoverMarker(DrawingContext context, Point center, double radius, double thickness)
    {
        if (_hoverFraction is not { } fraction) return;

        var pen = new Pen(MutedTextBrush ?? new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), 2)
        {
            LineCap = PenLineCap.Round
        };

        DrawTickLine(context, pen, center,
            radius - thickness / 2 - 3,
            radius + thickness / 2 + 3,
            fraction);
    }

    #region 刻度

    private void DrawTicks(DrawingContext context, Point center, double radius, double thickness)
    {
        var outer = radius + thickness / 2;
        var tickBrush = MutedTextBrush ?? new SolidColorBrush(Color.FromArgb(120, 128, 128, 128));
        var pen = new Pen(tickBrush, 1);

        switch (TickMode)
        {
            case ClockTickMode.Hour:
                for (var hour = 0; hour < 24; hour++)
                {
                    var major = hour % 3 == 0;
                    DrawTickLine(context, pen, center, outer + 2, outer + (major ? 7 : 4), hour / 24d);
                    if (!major) continue;

                    DrawLabel(context, center, outer + 13, hour / 24d, $"{hour:00}");
                }

                break;

            case ClockTickMode.Day:
            {
                // 两端都算一天：一周是 7 格，不是 6 格
                var days = Math.Max(1, (RangeEnd.Date - RangeStart.Date).Days + 1);
                // 一个月 30 天，每天都写标签会糊成一团：超过 7 天就每 5 天标一次
                var labelStep = days <= 7 ? 1 : 5;

                for (var i = 0; i < days; i++)
                {
                    var fraction = (double)i / days;
                    DrawTickLine(context, pen, center, outer + 2, outer + 5, fraction);

                    if (i % labelStep != 0) continue;
                    DrawLabel(context, center, outer + 13, fraction, RangeStart.AddDays(i).ToString("M/d"));
                }

                break;
            }

            default:
                for (var month = 0; month < 12; month++)
                {
                    var fraction = month / 12d;
                    DrawTickLine(context, pen, center, outer + 2, outer + 6, fraction);
                    DrawLabel(context, center, outer + 13, fraction,
                        RangeStart.AddMonths(month).ToString("MMM", CultureInfo.CurrentCulture));
                }

                break;
        }
    }

    private static void DrawTickLine(DrawingContext context, IPen pen, Point center,
        double from, double to, double fraction)
    {
        var angle = (-90d + fraction * 360d) * Math.PI / 180d;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        context.DrawLine(pen,
            new Point(center.X + from * cos, center.Y + from * sin),
            new Point(center.X + to * cos, center.Y + to * sin));
    }

    private void DrawLabel(DrawingContext context, Point center, double distance, double fraction, string text)
    {
        var formatted = MakeText(text, 10, MutedTextBrush ?? TextBrush);
        var angle = (-90d + fraction * 360d) * Math.PI / 180d;
        var x = center.X + distance * Math.Cos(angle) - formatted.Width / 2;
        var y = center.Y + distance * Math.Sin(angle) - formatted.Height / 2;
        context.DrawText(formatted, new Point(x, y));
    }

    #endregion

    private void DrawCenter(DrawingContext context, Point center, double innerRadius, bool hasData)
    {
        var hovered = HoveredSegment;

        string? main;
        string? caption;

        if (hovered != null)
        {
            main = hovered.Name;
            caption = string.IsNullOrEmpty(hovered.RangeText)
                ? hovered.CategoryName
                : hovered.RangeText;
        }
        else if (_hoverFraction is { } fraction && hasData)
        {
            main = FormatHoverLabel(fraction);
            caption = EmptyText;
        }
        else if (!hasData)
        {
            main = IsLoading ? null : EmptyText;
            caption = null;
        }
        else
        {
            main = CenterText;
            caption = CenterCaption;
        }

        // 内环文字随可用内径等比缩放并限宽，避免在紧凑窗口（860x553）贴边甚至压住环带
        var maxTextWidth = innerRadius * 1.65;
        FormattedText? mainText = null;
        if (!string.IsNullOrEmpty(main))
        {
            var baseSize = hovered != null
                ? Math.Clamp(innerRadius * 0.25, 12, 15)
                : Math.Clamp(innerRadius * 0.36, 12, 20);
            mainText = MakeText(main, baseSize, TextBrush, FontWeight.SemiBold);
            if (mainText.Width > maxTextWidth && mainText.Width > 0)
            {
                var fittedSize = Math.Max(11, baseSize * (maxTextWidth / mainText.Width));
                mainText = MakeText(main, fittedSize, TextBrush, FontWeight.SemiBold);
            }
        }

        FormattedText? captionText = null;
        if (!string.IsNullOrEmpty(caption))
        {
            var capSize = Math.Clamp(innerRadius * 0.19, 10, 11);
            captionText = MakeText(caption, capSize, MutedTextBrush ?? TextBrush);
            if (captionText.Width > maxTextWidth && captionText.Width > 0)
            {
                var fittedCap = Math.Max(9.5, capSize * (maxTextWidth / captionText.Width));
                captionText = MakeText(caption, fittedCap, MutedTextBrush ?? TextBrush);
            }
        }

        const double gap = 2;
        if (mainText != null && captionText != null)
        {
            var totalHeight = mainText.Height + gap + captionText.Height;
            var topY = center.Y - totalHeight / 2;
            context.DrawText(mainText, new Point(center.X - mainText.Width / 2, topY));
            context.DrawText(captionText, new Point(center.X - captionText.Width / 2, topY + mainText.Height + gap));
        }
        else if (mainText != null)
        {
            context.DrawText(mainText, new Point(center.X - mainText.Width / 2, center.Y - mainText.Height / 2));
        }
        else if (captionText != null)
        {
            context.DrawText(captionText, new Point(center.X - captionText.Width / 2, center.Y - captionText.Height / 2));
        }
    }

    public ClockRing()
    {
        Focusable = true;
        IsTabStop = true;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }

    /// <summary>键盘在扇区之间移动，Enter 等同于点击（交回 ClickCommand）</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled) return;

        var segments = (Segments ?? []).Where(s => s != null).OrderBy(s => s.StartFraction).ToList();
        if (segments.Count == 0) return;

        var index = HoveredSegment == null ? -1 : segments.IndexOf(HoveredSegment);

        switch (e.Key)
        {
            case Key.Left:
                index = index <= 0 ? segments.Count - 1 : index - 1;
                break;
            case Key.Right:
                index = index < 0 || index >= segments.Count - 1 ? 0 : index + 1;
                break;
            case Key.Home:
                index = 0;
                break;
            case Key.End:
                index = segments.Count - 1;
                break;
            case Key.Escape:
                if (index < 0) return;
                HoveredSegment = null;
                _hoverFraction = null;
                InvalidateVisual();
                e.Handled = true;
                return;
            case Key.Enter:
            case Key.Space:
                if (HoveredSegment == null) return;
                if (ClickCommand is { } command && command.CanExecute(HoveredSegment))
                    command.Execute(HoveredSegment);
                e.Handled = true;
                return;
            default:
                return;
        }

        HoveredSegment = segments[index];
        _hoverFraction = null;
        InvalidateVisual();
        e.Handled = true;
    }

    private ClockSegment? FindSegmentByFraction(double fraction) =>
        FindSegmentByAngle(fraction * 360d);

    #region 交互

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        HoveredSegment = null;
        _hoverFraction = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    private void UpdateHover(Point position)
    {
        var fraction = GetFractionOnRing(position);
        var segment = fraction == null ? null : HitTestSegment(position);

        _hoverFraction = fraction;
        HoveredSegment = segment;

        Cursor = segment?.Data != null || segment?.CategoryData != null
            ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;

        InvalidateVisual();
    }

    /// <summary>指针落在环带上时返回 0~1 的圆周比例，否则返回 null</summary>
    private double? GetFractionOnRing(Point position)
    {
        var thickness = Math.Max(6, Thickness);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - thickness / 2 - LabelSpace;
        if (radius <= 4) return null;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var dx = position.X - center.X;
        var dy = position.Y - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < radius - thickness / 2 - 8 || distance > radius + thickness / 2 + 8)
            return null;

        var angle = Math.Atan2(dy, dx) * 180d / Math.PI;
        return ((angle + 90 + 360) % 360) / 360d;
    }

    /// <summary>把圆周比例翻译成刻度文案：日看时刻、周/月看日期、年看月份</summary>
    private string FormatHoverLabel(double fraction)
    {
        var start = RangeStart;

        switch (TickMode)
        {
            case ClockTickMode.Hour:
                return start.AddSeconds(fraction * 86400d).ToString("HH:mm");

            case ClockTickMode.Day:
            {
                var days = Math.Max(1, (RangeEnd.Date - start.Date).Days + 1);
                var index = Math.Min(days - 1, (int)(fraction * days));
                return start.AddDays(index).ToString("M/d");
            }

            default:
            {
                var index = Math.Min(11, (int)(fraction * 12));
                return start.AddMonths(index).ToString("yyyy-MM");
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var segment = HitTestSegment(e.GetPosition(this));
        if (segment == null) return;

        var command = ClickCommand;
        if (command == null) return;

        if (command.CanExecute(segment)) command.Execute(segment);
        e.Handled = true;
    }

    private ClockSegment? HitTestSegment(Point position)
    {
        if (_hitMap.Count == 0) return null;

        var thickness = Math.Max(6, Thickness);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - thickness / 2 - LabelSpace;
        if (radius <= 4) return null;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var dx = position.X - center.X;
        var dy = position.Y - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        // 容差放宽到 8：环带只有 20~30px，贴着边缘划过去也要能触发
        if (distance < radius - thickness / 2 - 8 || distance > radius + thickness / 2 + 8)
            return null;

        var angle = Math.Atan2(dy, dx) * 180d / Math.PI;
        angle = (angle + 90 + 360) % 360;

        return FindSegmentByAngle(angle);
    }

    private ClockSegment? FindSegmentByAngle(double angleFromTop)
    {
        ClockSegment? nearest = null;
        var nearestDistance = double.MaxValue;

        foreach (var (segment, start, sweep) in _hitMap)
        {
            var offset = (angleFromTop - start + 360) % 360;
            if (offset <= sweep) return segment;

            // 没落在任何一段里：允许吸附到 0.6° 内最近的一段，几秒的碎片也点得到
            var distance = Math.Min(offset - sweep, 360 - offset);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = segment;
            }
        }

        return nearestDistance <= 0.6 ? nearest : null;
    }

    #endregion

    #region 绘制辅助

    private IBrush GetBrush(ClockSegment segment)
    {
        var color = UseCategoryColor && !string.IsNullOrWhiteSpace(segment.CategoryColor)
            ? segment.CategoryColor
            : segment.Color;

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

    private static void DrawArc(DrawingContext context, Point center, double radius,
        double startAngleDegrees, double sweepDegrees, IPen pen)
    {
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
            ctx.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static FormattedText MakeText(string? text, double size, IBrush? brush,
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

    #endregion
}
