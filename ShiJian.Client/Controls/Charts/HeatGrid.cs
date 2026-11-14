using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ShiJian.Client.Models;

namespace ShiJian.Client.Controls.Charts;

/// <summary>
/// 详细页看板用的热力图：行 / 列都是分类维度（星期、月份、分类），
/// 格子颜色深浅表示时长，悬停看具体数值。
///
/// 自绘而不是堆 ItemsControl：一年视图是 12×31 = 372 个格子，
/// 用控件堆出来滚动和换数据都会卡。
/// </summary>
public class HeatGrid : Control
{
    public static readonly StyledProperty<IEnumerable<HeatRow>?> RowsProperty =
        AvaloniaProperty.Register<HeatGrid, IEnumerable<HeatRow>?>(nameof(Rows));

    public static readonly StyledProperty<string[]?> ColumnLabelsProperty =
        AvaloniaProperty.Register<HeatGrid, string[]?>(nameof(ColumnLabels));

    public static readonly StyledProperty<double> MaxValueProperty =
        AvaloniaProperty.Register<HeatGrid, double>(nameof(MaxValue));

    /// <summary>格子深浅的基色（页面传主题色）</summary>
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<HeatGrid, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<HeatGrid, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> MutedTextBrushProperty =
        AvaloniaProperty.Register<HeatGrid, IBrush?>(nameof(MutedTextBrush));

    public static readonly StyledProperty<IBrush?> HoverBrushProperty =
        AvaloniaProperty.Register<HeatGrid, IBrush?>(nameof(HoverBrush));

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<HeatGrid, string?>(nameof(EmptyText));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<HeatGrid, bool>(nameof(IsLoading));

    /// <summary>行标题列宽，0 表示不画行标题</summary>
    public static readonly StyledProperty<double> RowLabelWidthProperty =
        AvaloniaProperty.Register<HeatGrid, double>(nameof(RowLabelWidth), 40d);

    /// <summary>
    /// 单格最大高度。行数少时（比如当天只用了两三个应用）不让格子拉成一根根长条，
    /// 热力图该有的样子是「一行行小方块」
    /// </summary>
    public static readonly StyledProperty<double> MaxCellHeightProperty =
        AvaloniaProperty.Register<HeatGrid, double>(nameof(MaxCellHeight), 26d);

    /// <summary>
    /// 方块化：宽高取同一个值（日历用）。列少的时候不至于把格子拉成一根根柱子
    /// </summary>
    public static readonly StyledProperty<bool> SquareCellsProperty =
        AvaloniaProperty.Register<HeatGrid, bool>(nameof(SquareCells));

    private const double Gap = 2;
    private const double ColumnLabelHeight = 15;
    private const double MinLabelSpacing = 26;
    private const double MinRowCellHeight = 14;
    private const double IdealRowCellHeight = 16;

    private static readonly IBrush FallbackAccent = new SolidColorBrush(Color.Parse("#5E81AC"));

    private (int Row, int Column) _hover = (-1, -1);

    static HeatGrid()
    {
        AffectsRender<HeatGrid>(RowsProperty, ColumnLabelsProperty, MaxValueProperty, AccentBrushProperty,
            TextBrushProperty, MutedTextBrushProperty, HoverBrushProperty, EmptyTextProperty, IsLoadingProperty,
            RowLabelWidthProperty, MaxCellHeightProperty, SquareCellsProperty);
        AffectsMeasure<HeatGrid>(RowsProperty, ColumnLabelsProperty, RowLabelWidthProperty,
            MaxCellHeightProperty, SquareCellsProperty);
    }

    public IEnumerable<HeatRow>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public string[]? ColumnLabels
    {
        get => GetValue(ColumnLabelsProperty);
        set => SetValue(ColumnLabelsProperty, value);
    }

    public double MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
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

    public double RowLabelWidth
    {
        get => GetValue(RowLabelWidthProperty);
        set => SetValue(RowLabelWidthProperty, value);
    }

    public double MaxCellHeight
    {
        get => GetValue(MaxCellHeightProperty);
        set => SetValue(MaxCellHeightProperty, value);
    }

    public bool SquareCells
    {
        get => GetValue(SquareCellsProperty);
        set => SetValue(SquareCellsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Rows?.Where(r => r?.Cells is { Count: > 0 }).ToList() ?? [];
        var labels = ColumnLabels ?? [];
        var columnCount = rows.Count > 0 ? Math.Max(rows.Max(r => r.Cells.Count), labels.Length) : labels.Length;
        return MeasurePreferredSize(availableSize, rows.Count, columnCount, MaxCellHeight, SquareCells, RowLabelWidth);
    }

    internal static Size MeasurePreferredSize(
        Size availableSize,
        int rowCount,
        int columnCount,
        double maxCellHeight,
        bool squareCells,
        double rowLabelWidth)
    {
        var width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        if (rowCount <= 0)
        {
            var emptyHeight = double.IsInfinity(availableSize.Height) ? 160 : availableSize.Height;
            return new Size(width, emptyHeight);
        }

        double targetCellHeight;
        if (squareCells && columnCount > 0)
        {
            var effectiveLabelWidth = Math.Clamp(rowLabelWidth, 0, width / 3);
            var plotWidth = Math.Max(0, width - effectiveLabelWidth);
            var cellWidth = Math.Max(MinRowCellHeight, (plotWidth - Gap * (columnCount - 1)) / columnCount);
            targetCellHeight = Math.Clamp(cellWidth, MinRowCellHeight, Math.Max(MinRowCellHeight, maxCellHeight));
        }
        else
        {
            targetCellHeight = Math.Clamp(IdealRowCellHeight, MinRowCellHeight, Math.Max(MinRowCellHeight, maxCellHeight));
        }

        var minNeededHeight = rowCount * MinRowCellHeight + Gap * Math.Max(0, rowCount - 1) + ColumnLabelHeight;
        var preferredHeight = rowCount * targetCellHeight + Gap * Math.Max(0, rowCount - 1) + ColumnLabelHeight;

        var height = double.IsInfinity(availableSize.Height)
            ? Math.Max(140, preferredHeight)
            : Math.Max(minNeededHeight, availableSize.Height);

        return new Size(width, height);
    }

    internal static int ComputeRowLabelStep(double rowPitch, double labelHeight)
    {
        if (rowPitch <= 0 || labelHeight <= 0) return 1;
        return Math.Max(1, (int)Math.Ceiling((labelHeight + 1) / rowPitch));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var rows = Rows?.Where(r => r?.Cells is { Count: > 0 }).ToList() ?? [];
        var labels = ColumnLabels ?? [];

        if (rows.Count == 0 || MaxValue <= 0)
        {
            DrawEmpty(context, width, height);
            return;
        }

        var columnCount = Math.Max(rows.Max(r => r.Cells.Count), labels.Length);
        if (columnCount <= 0)
        {
            DrawEmpty(context, width, height);
            return;
        }

        var rowLabelWidth = Math.Clamp(RowLabelWidth, 0, width / 3);
        var plotWidth = Math.Max(0, width - rowLabelWidth);
        var plotHeight = Math.Max(0, height - ColumnLabelHeight);
        if (plotWidth <= 0 || plotHeight <= 0) return;

        var cellWidth = (plotWidth - Gap * (columnCount - 1)) / columnCount;
        var cellHeight = (plotHeight - Gap * (rows.Count - 1)) / rows.Count;
        if (cellWidth <= 0 || cellHeight <= 0) return;

        (cellWidth, cellHeight) = ResolveCellSize(cellWidth, cellHeight);

        var radius = Math.Min(3, Math.Min(cellWidth, cellHeight) / 3);

        // 行数少的时候（比如当天只用了两三个应用）格子不拉长，整体在卡片里居中
        var gridHeight = rows.Count * cellHeight + Gap * (rows.Count - 1);
        var gridWidth = columnCount * cellWidth + Gap * (columnCount - 1);
        var offsetY = Math.Max(0, (plotHeight - gridHeight) / 2);
        var offsetX = rowLabelWidth + Math.Max(0, (plotWidth - gridWidth) / 2);

        DrawColumnLabels(context, labels, rowLabelWidth, cellWidth, height);

        var rowLabelStep = 1;
        if (rowLabelWidth > 0 && rows.Count > 0)
        {
            var sampleLabel = MakeText(rows[0].Label, 10, MutedTextBrush ?? TextBrush);
            rowLabelStep = ComputeRowLabelStep(cellHeight + Gap, sampleLabel.Height);
        }

        for (var row = 0; row < rows.Count; row++)
        {
            var y = offsetY + row * (cellHeight + Gap);

            if (rowLabelWidth > 0 && row % rowLabelStep == 0)
            {
                var text = MakeText(rows[row].Label, 10, MutedTextBrush ?? TextBrush);
                context.DrawText(text, new Point(rowLabelWidth - text.Width - 6,
                    y + (cellHeight - text.Height) / 2));
            }

            for (var column = 0; column < rows[row].Cells.Count; column++)
            {
                var cell = rows[row].Cells[column];
                if (cell.IsPlaceholder) continue;

                var x = offsetX + column * (cellWidth + Gap);
                var rect = new Rect(x, y, cellWidth, cellHeight);
                context.DrawRectangle(CreateBrush(cell.Value), null, new RoundedRect(rect, radius));
            }
        }

        DrawHover(context, rows, offsetX, cellWidth, cellHeight, offsetY, width);
    }

    /// <summary>格子尺寸：方块模式取长宽较小的一边，并对高度封顶</summary>
    private (double Width, double Height) ResolveCellSize(double cellWidth, double cellHeight)
    {
        var height = Math.Min(cellHeight, Math.Max(6, MaxCellHeight));
        var width = cellWidth;

        if (SquareCells)
        {
            var side = Math.Min(cellWidth, cellHeight);
            width = side;
            height = side;
        }

        return (width, height);
    }

    private void DrawColumnLabels(DrawingContext context, string[] labels, double rowLabelWidth,
        double cellWidth, double height)
    {
        if (labels.Length == 0 || TextBrush is null && MutedTextBrush is null) return;

        var brush = MutedTextBrush ?? TextBrush;
        var step = Math.Max(1, (int)Math.Ceiling(MinLabelSpacing / Math.Max(cellWidth + Gap, 1)));

        for (var i = 0; i < labels.Length; i += step)
        {
            var text = MakeText(labels[i], 10, brush);
            var x = rowLabelWidth + i * (cellWidth + Gap) + (cellWidth - text.Width) / 2;
            context.DrawText(text, new Point(x, height - text.Height));
        }
    }

    private void DrawHover(DrawingContext context, List<HeatRow> rows, double offsetX,
        double cellWidth, double cellHeight, double offsetY, double width)
    {
        var (row, column) = _hover;
        if (row < 0 || row >= rows.Count) return;
        if (column < 0 || column >= rows[row].Cells.Count) return;

        var cell = rows[row].Cells[column];
        var x = offsetX + column * (cellWidth + Gap);
        var y = offsetY + row * (cellHeight + Gap);

        // 悬停框：亮一圈比整格变色更好认，也不会盖掉深浅信息
        var pen = new Pen(TextBrush ?? Brushes.Gray, 1.5);
        context.DrawRectangle(null, pen, new RoundedRect(new Rect(x, y, cellWidth, cellHeight), 3));

        if (string.IsNullOrEmpty(cell.Tip)) return;

        var text = MakeText(cell.Tip, 11, ResolveBrush("DefaultTextBrush") ?? TextBrush ?? Brushes.Gray);
        var tipWidth = text.Width + 12;
        var tipHeight = text.Height + 6;
        var tipX = Math.Clamp(x + cellWidth / 2 - tipWidth / 2, 0, Math.Max(0, width - tipWidth));
        var tipY = y - tipHeight - 4;
        if (tipY < 0) tipY = y + cellHeight + 6;

        var background = ResolveBrush("PopupBackgroundBrush")
                         ?? HoverBrush
                         ?? new SolidColorBrush(Color.FromArgb(240, 46, 52, 64));
        var border = ResolveBrush("PopupBorderBrush");
        var tipRect = new RoundedRect(new Rect(tipX, tipY, tipWidth, tipHeight), 6);
        context.DrawRectangle(background, border != null ? new Pen(border, 1) : null, tipRect);
        context.DrawText(text, new Point(tipX + 6, tipY + 3));
    }

    private void DrawEmpty(DrawingContext context, double width, double height)
    {
        if (IsLoading || string.IsNullOrEmpty(EmptyText) || MutedTextBrush is null) return;
        var text = MakeText(EmptyText, 12, MutedTextBrush);
        context.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }

    /// <summary>越深越亮：开方一下，低值之间才分得开（线性映射前几小时全是黑的）</summary>
    private IBrush CreateBrush(double value)
    {
        var accent = AccentBrush is ISolidColorBrush solid ? solid.Color : ((ISolidColorBrush)FallbackAccent).Color;

        if (value <= 0)
            return ResolveBrush("Surface3Brush")
                   ?? new SolidColorBrush(Color.FromArgb(20, accent.R, accent.G, accent.B));

        var level = Math.Clamp(value / MaxValue, 0, 1);
        var alpha = (byte)Math.Clamp(48 + Math.Sqrt(level) * 200, 0, 255);
        return new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
    }

    private static IBrush? ResolveBrush(string resourceKey)
    {
        if (Application.Current is { } app &&
            app.CheckAccess() &&
            app.TryGetResource(resourceKey, app.ActualThemeVariant, out var resource) &&
            resource is IBrush brush)
        {
            return brush;
        }

        return null;
    }

    #region Hover

    private (int Row, int Column) HitTestCell(Point position)
    {
        var rows = Rows?.Where(r => r?.Cells is { Count: > 0 }).ToList() ?? [];
        if (rows.Count == 0 || MaxValue <= 0) return (-1, -1);

        var columnCount = Math.Max(rows.Max(r => r.Cells.Count), ColumnLabels?.Length ?? 0);
        if (columnCount <= 0) return (-1, -1);

        var rowLabelWidth = Math.Clamp(RowLabelWidth, 0, Bounds.Width / 3);
        var plotWidth = Math.Max(0, Bounds.Width - rowLabelWidth);
        var plotHeight = Math.Max(0, Bounds.Height - ColumnLabelHeight);

        var cellWidth = (plotWidth - Gap * (columnCount - 1)) / columnCount;
        var cellHeight = (plotHeight - Gap * (rows.Count - 1)) / rows.Count;
        if (cellWidth <= 0 || cellHeight <= 0) return (-1, -1);

        (cellWidth, cellHeight) = ResolveCellSize(cellWidth, cellHeight);

        if (position.X < rowLabelWidth || position.Y > plotHeight) return (-1, -1);

        var gridHeight = rows.Count * cellHeight + Gap * (rows.Count - 1);
        var gridWidth = columnCount * cellWidth + Gap * (columnCount - 1);
        var offsetY = Math.Max(0, (plotHeight - gridHeight) / 2);
        var offsetX = rowLabelWidth + Math.Max(0, (plotWidth - gridWidth) / 2);

        var column = (int)Math.Floor((position.X - offsetX) / (cellWidth + Gap));
        var row = (int)Math.Floor((position.Y - offsetY) / (cellHeight + Gap));
        if (row < 0 || row >= rows.Count || column < 0 || column >= rows[row].Cells.Count) return (-1, -1);
        if (rows[row].Cells[column].IsPlaceholder) return (-1, -1);

        return (row, column);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var hit = HitTestCell(e.GetPosition(this));
        if (hit == _hover) return;

        _hover = hit;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover == (-1, -1)) return;

        _hover = (-1, -1);
        InvalidateVisual();
    }

    #endregion

    private static FormattedText MakeText(string? text, double size, IBrush? brush)
    {
        return new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal),
            size,
            brush ?? Brushes.Gray);
    }
}
