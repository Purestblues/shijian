using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace ShiJian.Client.Behaviors;

/// <summary>
/// 自适应多列 WrapPanel 卡片宽度：按可用宽度计算等宽列数，消除固定 Width 在大屏或窄屏右侧留出的空白。
/// </summary>
public class AdaptiveWrapBehavior : Behavior<WrapPanel>
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveWrapBehavior, double>(nameof(MinItemWidth), 188d);

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject == null) return;
        AssociatedObject.SizeChanged += OnPanelSizeChanged;
        Apply(AssociatedObject.Bounds.Width);
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject != null)
            AssociatedObject.SizeChanged -= OnPanelSizeChanged;
        base.OnDetaching();
    }

    private void OnPanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Apply(e.NewSize.Width);
    }

    private void Apply(double availableWidth)
    {
        if (AssociatedObject == null) return;
        var target = ComputeItemWidth(availableWidth, MinItemWidth);
        if (double.IsNaN(target)) return;
        if (double.IsNaN(AssociatedObject.ItemWidth) || Math.Abs(AssociatedObject.ItemWidth - target) > 0.5d)
            AssociatedObject.ItemWidth = target;
    }

    public static double ComputeItemWidth(double availableWidth, double minItemWidth)
    {
        if (availableWidth <= 0 || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth))
            return double.NaN;
        var min = Math.Max(80d, minItemWidth);
        var columns = Math.Max(1, (int)Math.Floor(availableWidth / min));
        // 预留 0.5px 避免 DPI 缩放舍入把最后一列挤折到下一行
        return Math.Max(1d, Math.Floor((availableWidth - 0.5d) / columns));
    }
}
