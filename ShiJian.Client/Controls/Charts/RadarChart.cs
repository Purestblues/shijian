using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ShiJian.Client.Librarys;
using Colors = ShiJian.Client.Base.Color.Colors;

namespace ShiJian.Client.Controls.Charts;

public class RadarChart : Control
{
    public static readonly DirectProperty<RadarChart, double> MaxValueProperty =
        AvaloniaProperty.RegisterDirect<RadarChart, double>(
            nameof(MaxValue),
            o => o.MaxValue,
            (o, v) => o.MaxValue = v);

    public static readonly DirectProperty<RadarChart, List<double>> ValuesProperty =
        AvaloniaProperty.RegisterDirect<RadarChart, List<double>>(
            nameof(Values),
            o => o.Values,
            (o, v) => o.Values = v);

    public static readonly DirectProperty<RadarChart, List<string>> LabelsProperty =
        AvaloniaProperty.RegisterDirect<RadarChart, List<string>>(
            nameof(Labels),
            o => o.Labels,
            (o, v) => o.Labels = v);

    private List<string> _labels = new();
    private double _maxValue = 100; // 保留默认值100

    private List<double> _values = new();

    public double MaxValue
    {
        get => _maxValue;
        set => SetAndRaise(MaxValueProperty, ref _maxValue, value);
    }

    public List<double> Values
    {
        get => _values;
        set => SetAndRaise(ValuesProperty, ref _values, value);
    }

    public List<string> Labels
    {
        get => _labels;
        set => SetAndRaise(LabelsProperty, ref _labels, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Values == null || Values.Count < 3) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 * 0.7;

        DrawGrid(context, center, radius);

        DrawDataPolygon(context, center, radius);

        DrawLabels(context, center, radius);

        DrawCenterAndVertexPoints(context, center, radius);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnThemeVariantChanged;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static IBrush ResolveBrush(string resourceKey, string fallbackHex)
    {
        if (Application.Current is { } app &&
            app.CheckAccess() &&
            app.TryGetResource(resourceKey, app.ActualThemeVariant, out var resource) &&
            resource is IBrush brush)
        {
            return brush;
        }

        return Colors.GetFromString(fallbackHex);
    }

    private static Color ResolveAccentColor()
    {
        if (Application.Current is { } app &&
            app.CheckAccess() &&
            app.TryGetResource("ThemeColor", app.ActualThemeVariant, out var resource) &&
            resource is Color color)
        {
            return color;
        }

        return Color.Parse(StateData.ThemeColor);
    }

    private void DrawLabels(DrawingContext context, Point center, double radius)
    {
        if (Labels == null || Labels.Count != Values.Count) return;

        var typeface = new Typeface(FontFamily.Default);
        var brush = ResolveBrush("SubTextBrush", "#5F6E82");

        for (var i = 0; i < Labels.Count; i++)
        {
            // 计算标签的位置
            var angle = 2 * Math.PI * i / Values.Count - Math.PI / 2;
            var labelPoint = new Point(
                center.X + (radius + 25) * Math.Cos(angle),
                center.Y + (radius + 20) * Math.Sin(angle));

            // 创建格式化文本
            var formattedText = new FormattedText(
                Labels[i],
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                brush);
            // 调整标签位置，使其居中
            labelPoint = new Point(
                labelPoint.X - formattedText.Width / 2,
                labelPoint.Y - formattedText.Height / 2);
            // 绘制标签
            context.DrawText(formattedText, labelPoint);
        }
    }

    private List<Point> CalculatePolygonPoints(Point center, double radius, int sides)
    {
        var points = new List<Point>();
        for (var i = 0; i < sides; i++)
        {
            // 计算每个顶点的角度
            var angle = 2 * Math.PI * i / sides - Math.PI / 2; // 从顶部开始
            // 计算顶点的坐标
            var point = new Point(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle));
            points.Add(point);
        }

        return points;
    }

    private void DrawGrid(DrawingContext context, Point center, double radius)
    {
        var subtleBrush = ResolveBrush("DividerSubtleBrush", "#D8DEE9");
        var strongBrush = ResolveBrush("DividerStrongBrush", "#C2CAD8");
        var pen = new Pen(subtleBrush, 1.0);

        // 绘制同心多边形
        for (var i = 1; i <= 3; i++)
        {
            var points = CalculatePolygonPoints(center, radius * i / 3, Values.Count);
            context.DrawGeometry(null, pen, new PolylineGeometry(points, true));
        }

        var axisPen = new Pen(strongBrush, 0.8);

        // 绘制轴线
        for (var i = 0; i < Values.Count; i++)
        {
            var angle = 2 * Math.PI * i / Values.Count - Math.PI / 2;
            var endPoint = new Point(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle));
            context.DrawLine(axisPen, center, endPoint);
        }
    }

    private void DrawDataPolygon(DrawingContext context, Point center, double radius)
    {
        var points = new List<Point>();
        for (var i = 0; i < Values.Count; i++)
        {
            var value = Values[i] / MaxValue;
            value = value == 1 ? 0.97 : value;
            var r = radius * value;
            var angle = 2 * Math.PI * i / Values.Count - Math.PI / 2;

            points.Add(new Point(
                center.X + r * Math.Cos(angle),
                center.Y + r * Math.Sin(angle)));
        }

        var accent = ResolveAccentColor();
        var ramp = ChartColorHelper.BuildRamp(accent);
        // 绘制填充区域
        context.DrawGeometry(
            new SolidColorBrush(ramp[400], 0.20),
            new Pen(new SolidColorBrush(ramp[500]), 1.8),
            new PolylineGeometry(points, true));
    }

    private void DrawCenterAndVertexPoints(DrawingContext context, Point center, double radius)
    {
        var accent = ResolveAccentColor();
        var ramp = ChartColorHelper.BuildRamp(accent);
        var centerBrush = new SolidColorBrush(ramp[500]);
        var centerFill = ResolveBrush("Surface1Brush", "#ECEFF4");
        var borderBrush = new Pen(ResolveBrush("DividerSubtleBrush", "#D8DEE9"), 1);
        var vertexOutline = new Pen(ResolveBrush("Surface2Brush", "#FFFFFF"), 1.5);

        context.DrawEllipse(
            centerFill,
            borderBrush,
            center,
            6,
            6
        );

        // 绘制顶点圆形
        for (var i = 0; i < Values.Count; i++)
        {
            var value = Values[i] / MaxValue;
            value = value == 1 ? 0.97 : value;
            var r = radius * value;
            var angle = 2 * Math.PI * i / Values.Count - Math.PI / 2;

            var point = new Point(
                center.X + r * Math.Cos(angle),
                center.Y + r * Math.Sin(angle));

            context.DrawEllipse(centerBrush, vertexOutline, point, 3.5, 3.5);
        }
    }


    private Size MeasureRotatedString(TextBlock textBlock)
    {
        var formattedText = new FormattedText(
            textBlock.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch),
            textBlock.FontSize,
            Brushes.Black);
        return new Size(formattedText.Height, formattedText.Width);
    }
}