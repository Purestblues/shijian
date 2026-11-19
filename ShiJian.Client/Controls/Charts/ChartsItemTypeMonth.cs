using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using ShiJian.Client.Controls.Charts.Model;

namespace ShiJian.Client.Controls.Charts;

public class ChartsItemTypeMonth : TemplatedControl
{
    public static readonly DirectProperty<ChartsItemTypeMonth, string> ToolTipProperty =
        AvaloniaProperty.RegisterDirect<ChartsItemTypeMonth, string>(
            nameof(ToolTip),
            o => o.ToolTip,
            (o, v) => o.ToolTip = v);

    public static readonly DirectProperty<ChartsItemTypeMonth, ChartsDataModel> DataProperty =
        AvaloniaProperty.RegisterDirect<ChartsItemTypeMonth, ChartsDataModel>(
            nameof(Data),
            o => o.Data,
            (o, v) => o.Data = v);

    public static readonly DirectProperty<ChartsItemTypeMonth, double> MaxValueProperty =
        AvaloniaProperty.RegisterDirect<ChartsItemTypeMonth, double>(
            nameof(MaxValue),
            o => o.MaxValue,
            (o, v) => o.MaxValue = v);

    public static readonly DirectProperty<ChartsItemTypeMonth, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<ChartsItemTypeMonth, bool>(
            nameof(IsLoading),
            o => o.IsLoading,
            (o, v) => o.IsLoading = v);

    public static readonly DirectProperty<ChartsItemTypeMonth, bool> IsSelectedProperty =
        AvaloniaProperty.RegisterDirect<ChartsItemTypeMonth, bool>(
            nameof(IsSelected),
            o => o.IsSelected,
            (o, v) => o.IsSelected = v);

    private ChartsDataModel _data;

    private bool _isLoading;

    private bool _isSelected;

    private double _maxValue;
    private string _toolTip;
    private bool IsAddEvent;

    //private StackPanel ValueContainer;
    //private Image IconObj;
    private bool isRendering;

    private Border? ValueBlockObj;
    private TextBlock? DayText;

    public ChartsItemTypeMonth()
    {
        Unloaded += ChartsItemTypeMonth_Unloaded;
    }

    public string ToolTip
    {
        get => _toolTip;
        set => SetAndRaise(ToolTipProperty, ref _toolTip, value);
    }

    /// <summary>
    /// 数据
    /// </summary>
    public ChartsDataModel Data
    {
        get => _data;
        set => SetAndRaise(DataProperty, ref _data, value);
    }

    public double MaxValue
    {
        get => _maxValue;
        set => SetAndRaise(MaxValueProperty, ref _maxValue, value);
    }

    /// <summary>
    /// 是否正在加载中
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    /// <summary>
    /// 是否选中
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetAndRaise(IsSelectedProperty, ref _isSelected, value);
    }

    protected override Type StyleKeyOverride => typeof(ChartsItemTypeMonth);

    private void ChartsItemTypeMonth_Unloaded(object? sender, RoutedEventArgs e)
    {
        Unloaded -= ChartsItemTypeMonth_Unloaded;
        Loaded -= ChartsItemTypeMonth_Loaded;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        ValueBlockObj = e.NameScope.Find<Border>("ValueBlockObj");
        DayText = e.NameScope.Find<TextBlock>("DayText");
        if (!IsAddEvent)
        {
            IsAddEvent = true;
            Loaded += ChartsItemTypeMonth_Loaded;
        }
    }

    private void ChartsItemTypeMonth_Loaded(object? sender, RoutedEventArgs e)
    {
        Render();
    }

    private void Render()
    {
        if (isRendering || Data == null || ValueBlockObj == null || DayText == null) return;

        isRendering = true;

        DayText.Text = Data.DateTime.Day > 0 ? Data.DateTime.Day.ToString() : string.Empty;

        var ratio = MaxValue > 0 && Data.Value > 0
            ? Math.Clamp(Data.Value / MaxValue, 0, 1)
            : 0;

        if (ratio > 0)
        {
            ValueBlockObj.Opacity = 0.15 + Math.Sqrt(ratio) * 0.70;
            if (ratio > 0.55)
            {
                var onBrand = ResolveBrush("OnBrandBrush");
                if (onBrand != null) DayText.Foreground = onBrand;
            }
            else
            {
                var subText = ResolveBrush("SubTextBrush");
                if (subText != null) DayText.Foreground = subText;
            }
        }
        else
        {
            ValueBlockObj.Opacity = 0;
            var subText = ResolveBrush("SubTextBrush");
            if (subText != null) DayText.Foreground = subText;
        }

        ToolTip = Data.DateTime.ToString("yyyy年MM月dd日") + " " + (string.IsNullOrEmpty(Data.Tag) ? "无数据" : Data.Tag);

        if (Data.DateTime.Date == DateTime.Now.Date)
        {
            IsSelected = true;
            ToolTip = "[今日] " + ToolTip;
        }
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
}
