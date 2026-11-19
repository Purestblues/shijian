using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using ShiJian.Client.Librarys;

namespace ShiJian.Client.Controls.Select;

public class TabSwitch : TemplatedControl
{
    public static readonly StyledProperty<List<SelectItemModel>> OptionsProperty =
        AvaloniaProperty.Register<TabSwitch, List<SelectItemModel>>(nameof(Options));

    public static readonly StyledProperty<SelectItemModel> SelectedItemProperty =
        AvaloniaProperty.Register<TabSwitch, SelectItemModel>(nameof(SelectedItem));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<TabSwitch, int>(nameof(SelectedIndex));

    public static readonly StyledProperty<bool> IsShowIconProperty =
        AvaloniaProperty.Register<TabSwitch, bool>(nameof(IsShowIcon), true);

    private StackPanel _itemsContainer;
    private Border _slider;
    private Transitions _sliderTransitions;
    private bool _sliderUpdatePending;
    private bool _sliderInitialized;

    public TabSwitch()
    {
        Options = new List<SelectItemModel>();

        // 周期/维度切换是常用控件，必须能用键盘改
        Focusable = true;
        IsTabStop = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || Options == null || Options.Count == 0) return;

        var index = SelectedIndex;
        switch (e.Key)
        {
            case Key.Left:
                index = index <= 0 ? Options.Count - 1 : index - 1;
                break;
            case Key.Right:
                index = index < 0 || index >= Options.Count - 1 ? 0 : index + 1;
                break;
            default:
                return;
        }

        SelectedIndex = index;
        e.Handled = true;
    }

    public List<SelectItemModel> Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public SelectItemModel SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public bool IsShowIcon
    {
        get => GetValue(IsShowIconProperty);
        set => SetValue(IsShowIconProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == OptionsProperty || change.Property == IsShowIconProperty)
        {
            RenderItems();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            UpdateSelectedItem();
        }
        else if (change.Property == SelectedItemProperty)
        {
            UpdateSelectedIndex();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _itemsContainer = e.NameScope.Get<StackPanel>("ItemsContainer");
        _slider = e.NameScope.Get<Border>("Slider");
        _sliderTransitions = _slider.Transitions;
        RenderItems();
    }

    private void RenderItems()
    {
        if (_itemsContainer == null || Options == null) return;

        _itemsContainer.Children.Clear();
        _sliderInitialized = false;

        for (int i = 0; i < Options.Count; i++)
            _itemsContainer.Children.Add(CreateOption(Options[i], i));

        UpdateOptionsStyle();
    }

    private TabOption CreateOption(SelectItemModel item, int index)
    {
        var option = new TabOption
        {
            Value = item,
            IsShowIcon = IsShowIcon,
            IsChecked = index == SelectedIndex
        };

        option.PointerPressed += (s, e) =>
        {
            SelectedIndex = index;
            e.Handled = true;
        };

        return option;
    }

    private void UpdateSelectedItem()
    {
        if (Options != null && SelectedIndex >= 0 && SelectedIndex < Options.Count)
        {
            SelectedItem = Options[SelectedIndex];
            UpdateOptionsStyle();
        }
    }

    private void UpdateSelectedIndex()
    {
        if (Options != null && SelectedItem != null)
        {
            var index = Options.IndexOf(SelectedItem);
            if (index >= 0)
            {
                SelectedIndex = index;
                UpdateOptionsStyle();
            }
        }
    }

    private void UpdateOptionsStyle()
    {
        if (_itemsContainer == null) return;

        for (int i = 0; i < _itemsContainer.Children.Count; i++)
        {
            if (_itemsContainer.Children[i] is TabOption option)
                option.IsChecked = i == SelectedIndex;
        }

        if (!_sliderUpdatePending)
        {
            _sliderUpdatePending = true;
            Dispatcher.UIThread.Post(ScheduleSliderUpdate, DispatcherPriority.Loaded);
        }
    }

    private void ScheduleSliderUpdate()
    {
        _sliderUpdatePending = false;
        PositionSlider();
    }

    private void PositionSlider()
    {
        if (_slider == null || _itemsContainer == null) return;

        if (SelectedIndex < 0 || SelectedIndex >= _itemsContainer.Children.Count)
        {
            _slider.IsVisible = false;
            return;
        }

        var selected = _itemsContainer.Children[SelectedIndex];
        var containerOffset = _itemsContainer.Bounds.Position;
        var targetX = containerOffset.X + selected.Bounds.X;
        var targetWidth = selected.Bounds.Width;

        _slider.Margin = new Thickness(0);

        var animate = _sliderInitialized && Motion.IsEnabled;
        if (!animate)
        {
            _slider.Transitions = null;
            _slider.RenderTransform = TransformOperations.Parse(
                $"translateX({targetX.ToString(CultureInfo.InvariantCulture)}px)");
            _slider.Width = targetWidth;
            _slider.IsVisible = true;
            if (Motion.IsEnabled && _sliderTransitions != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_slider != null && Motion.IsEnabled)
                        _slider.Transitions = _sliderTransitions;
                }, DispatcherPriority.Render);
            }
            _sliderInitialized = true;
        }
        else
        {
            _slider.Transitions ??= _sliderTransitions;
            _slider.RenderTransform = TransformOperations.Parse(
                $"translateX({targetX.ToString(CultureInfo.InvariantCulture)}px)");
            _slider.Width = targetWidth;
            _slider.IsVisible = true;
        }
    }

    protected override Type StyleKeyOverride => typeof(TabSwitch);
}
