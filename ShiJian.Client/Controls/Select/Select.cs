using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ShiJian.Client.Controls.Select;

public class Select : TemplatedControl
{
    public static readonly StyledProperty<bool> IsShowIconProperty =
        AvaloniaProperty.Register<Select, bool>(nameof(IsShowIcon));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<Select, bool>(nameof(IsOpen));

    public static readonly DirectProperty<Select, List<SelectItemModel>> OptionsProperty =
        AvaloniaProperty.RegisterDirect<Select, List<SelectItemModel>>(
            nameof(Options),
            o => o.Options,
            (o, v) => o.Options = v ?? new List<SelectItemModel>());

    public static readonly StyledProperty<SelectItemModel> SelectedItemProperty =
        AvaloniaProperty.Register<Select, SelectItemModel>(nameof(SelectedItem));


    private List<SelectItemModel> _options = new();

    private StackPanel _optionsContainer;
    private Popup? _popup;
    private long _lastLightDismissTicks;
    private bool _closingExplicitly;

    public Select()
    {
        PointerPressed += OnPointerPressed;
    }

    public bool IsShowIcon
    {
        get => GetValue(IsShowIconProperty);
        set => SetValue(IsShowIconProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public List<SelectItemModel> Options
    {
        get => _options;
        set => SetAndRaise(OptionsProperty, ref _options, value);
    }

    public SelectItemModel SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Select);


    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var control = change.Sender as Select;
        if (change.Property == OptionsProperty && change.OldValue != change.NewValue) control.RenderOptions();
        if (change.Property == SelectedItemProperty && change.NewValue != change.OldValue)
            control.OnSelectedItemChange();
    }

    public event EventHandler OnSelectedItemChanged;

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        PointerPressed -= OnPointerPressed;
        if (_optionsContainer?.Children != null)
        {
            foreach (var item in _optionsContainer.Children) item.PointerPressed -= Option_MouseLeftButtonUp;
            _optionsContainer?.Children?.Clear();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // IsLightDismissEnabled="True" 会在同一个按压周期内先把 Popup 关掉，
        // 若不抑制刚关闭窗口期，直接 IsOpen = !IsOpen 会把刚关上的下拉框立刻又弹开。
        if (!IsOpen && _lastLightDismissTicks > 0 &&
            Environment.TickCount64 - _lastLightDismissTicks < 180)
        {
            _lastLightDismissTicks = 0;
            e.Handled = true;
            return;
        }

        IsOpen = !IsOpen;
        e.Handled = true;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_popup != null)
            _popup.Closed -= OnPopupClosed;
        _popup = e.NameScope.Find<Popup>("PART_Popup");
        if (_popup != null)
            _popup.Closed += OnPopupClosed;
        _optionsContainer = e.NameScope.Get<StackPanel>("OptionsContainer");
        RenderOptions();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (!_closingExplicitly)
            _lastLightDismissTicks = Environment.TickCount64;
    }

    public void RenderOptions()
    {
        if (_optionsContainer == null || Options == null) return;

        _optionsContainer?.Children.Clear();

        foreach (var item in Options)
        {
            var option = new Option();
            option.Value = item;
            option.IsShowIcon = IsShowIcon;
            option.IsChecked = SelectedItem?.Name == item.Name;
            option.PointerPressed += Option_MouseLeftButtonUp;
            _optionsContainer.Children.Add(option);
        }
    }

    private void Option_MouseLeftButtonUp(object? sender, PointerPressedEventArgs e)
    {
        var option = sender as Option;
        if (option != null)
        {
            _closingExplicitly = true;
            try
            {
                SelectedItem = option.Value;
                OnSelectedItemChange();
                OnSelectedItemChanged?.Invoke(this, EventArgs.Empty);
                IsOpen = false;
            }
            finally
            {
                _closingExplicitly = false;
            }
            e.Handled = true;
        }
    }

    private void OnSelectedItemChange()
    {
        if (_optionsContainer == null || SelectedItem == null) return;
        foreach (Option option in _optionsContainer.Children) option.IsChecked = SelectedItem.Name == option.Value.Name;
    }
}