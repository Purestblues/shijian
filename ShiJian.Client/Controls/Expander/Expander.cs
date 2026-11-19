using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Librarys;

namespace ShiJian.Client.Controls.Expander;

public class Expander : Avalonia.Controls.Expander
{
    //  内容部分容器边框,用于设置下移动画
    private Border ContentBorder_;

    //  内容容器高度隐藏容器,用于辅助显示隐藏动画
    private Canvas ContentHeightCanvas_;
    private StackPanel ContentStackPanel_;
    private Border HeaderBorder_;
    private Icon Chevron_;


    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ContentHeightCanvas_.SetValue(HeightProperty, MeasureContentHeight());
        ContentStackPanel_.SizeChanged += ContentStackPanel__SizeChanged;
    }

    private void ContentStackPanel__SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // 展开状态下内容变高变矮，占位 Canvas 跟着同步，否则会裁掉内容
        if (IsExpanded && ContentHeightCanvas_ != null) ContentHeightCanvas_.Height = MeasureContentHeight();
    }

    /// <summary>
    /// 整条头部都是点击热区（G13：原来只有右侧箭头绑了命令，点标题没反应）
    /// </summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(HeaderBorder_).Properties.IsLeftButtonPressed) return;
        IsExpanded = !IsExpanded;
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty)
        {
            ApplyExpanded(IsExpanded);
        }
    }

    /// <summary>
    /// 内容高度由占位 Canvas 驱动（模板里 RowDefinition.Height 绑它的 Height），
    /// 所以展开 / 收起只要把这个高度做成动画即可；动效关闭时直接落值。
    /// </summary>
    private void ApplyExpanded(bool expanded)
    {
        if (ContentHeightCanvas_ == null || ContentStackPanel_ == null) return;

        var target = expanded ? MeasureContentHeight() : 0;
        var from = ContentHeightCanvas_.Height;
        if (double.IsNaN(from)) from = 0;

        if (!Motion.IsEnabled || Math.Abs(target - from) < 0.5)
        {
            ContentHeightCanvas_.Height = target;
            return;
        }

        _ = AnimateAsync(from, target, expanded);
    }

    private async Task AnimateAsync(double from, double to, bool expanded)
    {
        try
        {
            await ExpandMotion.AnimateHeightAsync(
                ContentHeightCanvas_,
                from,
                to,
                MotionTokens.M,
                expanded ? MotionTokens.Enter() : MotionTokens.Leave());
        }
        catch
        {
            if (ContentHeightCanvas_ != null) ContentHeightCanvas_.Height = to;
        }
    }

    private double MeasureContentHeight()
    {
        var width = ContentStackPanel_.Bounds.Width;
        if (width <= 0) width = Bounds.Width;
        if (width <= 0) return 0;

        ContentStackPanel_.Measure(new Size(width, double.PositiveInfinity));
        return ContentStackPanel_.DesiredSize.Height;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        HeaderBorder_ = e.NameScope.Find<Border>("HeaderBorder");
        Chevron_ = e.NameScope.Find<Icon>("Icon");
        ContentHeightCanvas_ = e.NameScope.Find<Canvas>("ContentHeight");
        ContentBorder_ = e.NameScope.Find<Border>("Content");
        ContentStackPanel_ = e.NameScope.Find<StackPanel>("ContentStackPanel");

        if (HeaderBorder_ != null)
        {
            HeaderBorder_.PointerPressed += OnHeaderPointerPressed;
            PressedState.Attach(HeaderBorder_);
        }
    }
}
