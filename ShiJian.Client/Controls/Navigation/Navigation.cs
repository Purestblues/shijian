using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using ShiJian.Client.Controls.Navigation.Models;
using ShiJian.Client.Librarys;

namespace ShiJian.Client.Controls.Navigation;

public class Navigation : TemplatedControl
{
    public static readonly DirectProperty<Navigation, ContextMenu?> ItemContextMenuProperty =
        AvaloniaProperty.RegisterDirect<Navigation, ContextMenu?>(
            nameof(ItemContextMenu),
            o => o.ItemContextMenu,
            (o, v) => o.ItemContextMenu = v);

    public static readonly DirectProperty<Navigation, NavigationItemModel?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<Navigation, NavigationItemModel?>(
            nameof(SelectedItem),
            o => o.SelectedItem,
            (o, v) => o.SelectedItem = v);

    public static readonly DirectProperty<Navigation, object?> TopExtContentProperty =
        AvaloniaProperty.RegisterDirect<Navigation, object?>(
            nameof(TopExtContent),
            o => o.TopExtContent,
            (o, v) => o.TopExtContent = v);

    public static readonly DirectProperty<Navigation, object?> BottomExtContentProperty =
        AvaloniaProperty.RegisterDirect<Navigation, object?>(
            nameof(BottomExtContent),
            o => o.BottomExtContent,
            (o, v) => o.BottomExtContent = v);

    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<Navigation, bool>(nameof(IsCompact));

    public static readonly DirectProperty<Navigation, ObservableCollection<NavigationItemModel>> DataProperty =
        AvaloniaProperty.RegisterDirect<Navigation, ObservableCollection<NavigationItemModel>>(
            nameof(Data),
            o => o.Data,
            (o, v) => o.Data = v);

    private readonly Dictionary<int, NavigationItem> ItemsDictionary;

    private object? _bottomExtContent;

    private ObservableCollection<NavigationItemModel> _data = new();
    private ContextMenu? _itemContextMenu;

    private NavigationItemModel? _selectedItem;

    private object? _topExtContent;

    private StackPanel ItemsPanel;
    private Border? _activeIndicator;
    private Transitions? _indicatorTransitions;
    private bool _indicatorInitialized;
    private bool _indicatorUpdatePending;

    public Navigation()
    {
        ItemsDictionary = new Dictionary<int, NavigationItem>();
        Loaded += Navigation_Loaded;
        Unloaded += Navigation_Unloaded;
    }

    public ContextMenu? ItemContextMenu
    {
        get => _itemContextMenu;
        set => SetAndRaise(ItemContextMenuProperty, ref _itemContextMenu, value);
    }

    public NavigationItemModel? SelectedItem
    {
        get => _selectedItem;
        set => SetAndRaise(SelectedItemProperty, ref _selectedItem, value);
    }

    public object? TopExtContent
    {
        get => _topExtContent;
        set => SetAndRaise(TopExtContentProperty, ref _topExtContent, value);
    }

    public object? BottomExtContent
    {
        get => _bottomExtContent;
        set => SetAndRaise(BottomExtContentProperty, ref _bottomExtContent, value);
    }

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public ObservableCollection<NavigationItemModel> Data
    {
        get => _data;
        set => SetAndRaise(DataProperty, ref _data, value);
    }

    protected override Type StyleKeyOverride => typeof(Navigation);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedItemProperty) OnSelectedItemChanged(change);

        if (change.Property == DataProperty) OnDataChanged(change);

        if (change.Property == IsCompactProperty)
        {
            foreach (var item in ItemsDictionary.Values)
            {
                item.IsCompact = IsCompact;
            }
            ScheduleActiveIndicatorUpdate(animate: false);
        }
    }

    public event EventHandler OnSelected;

    public event EventHandler OnMouseRightButtonUP;

    private void OnSelectedItemChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var control = e.Sender as Navigation;
        var oldItem = e.OldValue as NavigationItemModel;
        var newItem = e.NewValue as NavigationItemModel;
        if (newItem != oldItem)
        {
            if (newItem != null && control.ItemsDictionary.ContainsKey(newItem.ID))
                control.ItemsDictionary[newItem.ID].IsSelected = true;

            if (oldItem != null && control.ItemsDictionary.ContainsKey(oldItem.ID))
                control.ItemsDictionary[oldItem.ID].IsSelected = false;

            control.ScrollToActive();
        }
    }

    private void OnDataChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var control = e.Sender as Navigation;
        if (e.OldValue != e.NewValue)
            if (control.Data != null)
            {
                control.Data.CollectionChanged -= control.Data_CollectionChanged;
                control.Data.CollectionChanged += control.Data_CollectionChanged;

                foreach (var item in control.Data) control.AddItem(item);
            }
    }

    private void Data_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            foreach (var item in e.NewItems)
                AddItem(item as NavigationItemModel);

        if (e.Action == NotifyCollectionChangedAction.Remove)
            foreach (var item in e.OldItems)
                RemoveItem(item as NavigationItemModel);

        if (e.Action == NotifyCollectionChangedAction.Replace)
            foreach (var ritem in e.NewItems)
            {
                var item = ritem as NavigationItemModel;
                var id = item.ID;
                ItemsDictionary[id].Icon = item.UnSelectedIcon;
                ItemsDictionary[id].Title = item.Title;
                ItemsDictionary[id].SelectedIcon = item.SelectedIcon;
            }
    }

    private void Navigation_Loaded(object? sender, RoutedEventArgs e)
    {
        Motion.Changed += OnMotionChanged;
        ScrollToActive();
    }

    private void Navigation_Unloaded(object? sender, RoutedEventArgs e)
    {
        Motion.Changed -= OnMotionChanged;
        if (ItemsPanel != null)
        {
            ItemsPanel.SizeChanged -= OnItemsPanelSizeChanged;
        }
    }

    private void OnMotionChanged(object? sender, EventArgs e)
    {
        if (!Motion.IsEnabled || _activeIndicator == null || _indicatorTransitions == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_activeIndicator != null && Motion.IsEnabled)
                _activeIndicator.Transitions = _indicatorTransitions;
        }, DispatcherPriority.Loaded);
    }

    private void OnItemsPanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ScheduleActiveIndicatorUpdate(animate: false);
    }

    private void RemoveItem(NavigationItemModel item)
    {
        var navItem = ItemsDictionary[item.ID];
        ItemsPanel.Children.Remove(navItem);
        ItemsDictionary.Remove(item.ID);
        ScheduleActiveIndicatorUpdate(animate: false);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (ItemsPanel != null)
        {
            ItemsPanel.SizeChanged -= OnItemsPanelSizeChanged;
        }
        ItemsPanel = e.NameScope.Get<StackPanel>("ItemsPanel")!;
        ItemsPanel.SizeChanged += OnItemsPanelSizeChanged;
        _activeIndicator = e.NameScope.Find<Border>("ActiveIndicator");
        _indicatorTransitions = _activeIndicator?.Transitions;
        _indicatorInitialized = false;
        Render();
    }

    private void AddItem(NavigationItemModel item)
    {
        if (ItemsPanel != null)
        {
            var navItem = new NavigationItem();
            navItem.Classes.Add("in-nav-list");
            var id = item.ID < -1 ? CreateID() : item.ID;
            if (ItemsDictionary.ContainsKey(id)) return;

            item.ID = id;
            navItem.ID = id;
            navItem.Title = item.Title;
            navItem.Icon = item.UnSelectedIcon;
            navItem.SelectedIcon = item.SelectedIcon;
            navItem.IconColor = item.IconColor;
            navItem.BadgeText = item.BadgeText;
            navItem.Uri = item.Uri;
            navItem.IsCompact = IsCompact;

            System.ComponentModel.PropertyChangedEventHandler itemChangedHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(NavigationItemModel.Title))
                    navItem.Title = item.Title;
            };
            item.PropertyChanged += itemChangedHandler;

            navItem.MouseUp += NavItem_MouseUp;
            navItem.Activated += NavItem_Activated;
            navItem.Unloaded += (e, c) =>
            {
                item.PropertyChanged -= itemChangedHandler;
                navItem.MouseUp -= NavItem_MouseUp;
                navItem.Activated -= NavItem_Activated;
            };

            if (SelectedItem?.ID == id) navItem.IsSelected = true;

            ItemsPanel.Children.Add(navItem);
            ItemsDictionary.Add(id, navItem);
        }
    }


    private void NavItem_MouseUp(object? sender, PointerPressedEventArgs e)
    {
        var navitem = sender as NavigationItem;
        if (navitem != null)
        {
            var properties = e.GetCurrentPoint(null).Properties;

            if (properties.IsLeftButtonPressed)
            {
                // 左键选中
                SelectedItem = Data.First(m => m.ID == navitem.ID);
                OnSelected?.Invoke(this, EventArgs.Empty);

                if (SelectedItem != null && SelectedItem.ID != navitem.ID) ScrollToActive();
            }
            else if (properties.IsRightButtonPressed)
            {
                // 右键
                var args = new RoutedEventArgs();
                args.RoutedEvent = e.RoutedEvent;
                args.Source = Data.FirstOrDefault(m => m.ID == navitem.ID);
                OnMouseRightButtonUP?.Invoke(this, args);
            }
        }
    }


    private void NavItem_Activated(object? sender, EventArgs e)
    {
        if (sender is NavigationItem navitem) SelectItem(navitem);
    }

    /// <summary>把某个侧边项设为选中并通知外部（鼠标点击和键盘激活走同一条路）</summary>
    private void SelectItem(NavigationItem navitem)
    {
        if (Data == null) return;

        var model = Data.FirstOrDefault(m => m.ID == navitem.ID);
        if (model == null) return;

        SelectedItem = model;
        OnSelected?.Invoke(this, EventArgs.Empty);

        if (SelectedItem != null && SelectedItem.ID != navitem.ID) ScrollToActive();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || e.Key is not (Key.Up or Key.Down)) return;
        if (ItemsPanel == null) return;

        var items = ItemsPanel.Children.OfType<NavigationItem>().ToList();
        if (items.Count == 0) return;

        var current = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].IsFocused) continue;
            current = i;
            break;
        }

        // 还没聚焦到具体项时，从当前选中项开始
        if (current < 0 && SelectedItem != null)
            current = items.FindIndex(i => i.ID == SelectedItem.ID);

        var next = e.Key == Key.Down
            ? (current + 1 + items.Count) % items.Count
            : (current - 1 + items.Count) % items.Count;

        items[next].Focus();
        SelectItem(items[next]);
        e.Handled = true;
    }

    private int CreateID()
    {
        if (ItemsDictionary.Count == 0) return 1;

        return ItemsDictionary.Max(m => m.Key) + 1;
    }

    private void ScrollToActive(double animationDuration = 0.35)
    {
        if (SelectedItem == null || ItemsDictionary.Count == 0 || !ItemsDictionary.ContainsKey(SelectedItem.ID))
        {
            UpdateActiveIndicator(animate: animationDuration > 0);
            return;
        }

        var item = ItemsDictionary[SelectedItem.ID];
        item.IsSelected = true;
        ScheduleActiveIndicatorUpdate(animate: _indicatorInitialized && animationDuration > 0);
    }

    private void UpdateActiveLocation()
    {
        if (SelectedItem == null || !IsLoaded)
        {
            UpdateActiveIndicator(animate: false);
            return;
        }

        var item = ItemsDictionary[SelectedItem.ID];
        item.Loaded += (_, _) => { ScrollToActive(0); };
    }

    private void ScheduleActiveIndicatorUpdate(bool animate)
    {
        if (_activeIndicator == null) return;

        if (animate && _indicatorInitialized)
        {
            UpdateActiveIndicator(animate: true);
            return;
        }

        if (_indicatorUpdatePending) return;
        _indicatorUpdatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _indicatorUpdatePending = false;
            UpdateActiveIndicator(animate: false);
        }, DispatcherPriority.Loaded);
    }

    private void UpdateActiveIndicator(bool animate)
    {
        if (_activeIndicator == null) return;

        if (SelectedItem == null || !ItemsDictionary.TryGetValue(SelectedItem.ID, out var item))
        {
            if (!Motion.IsEnabled)
                _activeIndicator.Transitions = null;
            else
                _activeIndicator.Transitions = _indicatorTransitions;

            _activeIndicator.Opacity = 0;
            return;
        }

        var targetY = item.Bounds.Y;
        var targetHeight = item.Bounds.Height;
        if (targetHeight <= 0)
        {
            ScheduleActiveIndicatorUpdate(animate);
            return;
        }

        var shouldAnimate = animate && Motion.IsEnabled && _indicatorInitialized;
        if (!shouldAnimate)
        {
            _activeIndicator.Transitions = null;
        }
        else
        {
            _activeIndicator.Transitions = _indicatorTransitions;
        }

        _activeIndicator.Height = targetHeight;
        _activeIndicator.IsVisible = true;
        _activeIndicator.Opacity = 1;
        _activeIndicator.RenderTransform =
            TransformOperations.Parse($"translateY({targetY.ToString(CultureInfo.InvariantCulture)}px)");

        if (!shouldAnimate && Motion.IsEnabled && _indicatorTransitions != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_activeIndicator != null && Motion.IsEnabled)
                    _activeIndicator.Transitions = _indicatorTransitions;
            }, DispatcherPriority.Render);
        }

        _indicatorInitialized = true;
    }

    private void Render()
    {
        ItemsPanel.Children.Clear();
        ItemsDictionary.Clear();

        if (Data != null)
            foreach (var item in Data)
                AddItem(item);

        UpdateActiveLocation();
    }
}
