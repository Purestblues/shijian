using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Controls.Input;
using ShiJian.Client.Librarys;
using ShiJian.Client.Servicers;

namespace ShiJian.Client.Controls.Charts;

public enum ContextMenuType
{
    App,
    WebSite,
    AppDetail,
    WebSiteDetail
}

public class ListChart : TemplatedControl
{
    public static readonly DirectProperty<ListChart, IEnumerable<ChartsDataModel>> DataProperty =
        AvaloniaProperty.RegisterDirect<ListChart, IEnumerable<ChartsDataModel>>(
            nameof(Data), o => o.Data, (o, v) => o.Data = v);

    public static readonly DirectProperty<ListChart, bool> IsSearchProperty =
        AvaloniaProperty.RegisterDirect<ListChart, bool>(
            nameof(IsSearch), o => o.IsSearch, (o, v) => o.IsSearch = v);

    public static readonly DirectProperty<ListChart, bool> EmptyCompactProperty =
        AvaloniaProperty.RegisterDirect<ListChart, bool>(
            nameof(EmptyCompact), o => o.EmptyCompact, (o, v) => o.EmptyCompact = v);

    public static readonly DirectProperty<ListChart, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<ListChart, bool>(
            nameof(IsLoading), o => o.IsLoading, (o, v) => o.IsLoading = v);

    public static readonly DirectProperty<ListChart, string> EmptyTextProperty =
        AvaloniaProperty.RegisterDirect<ListChart, string>(
            nameof(EmptyText), o => o.EmptyText, (o, v) => o.EmptyText = v);

    public static readonly DirectProperty<ListChart, bool> IsCanScrollProperty =
        AvaloniaProperty.RegisterDirect<ListChart, bool>(
            nameof(IsCanScroll), o => o.IsCanScroll, (o, v) => o.IsCanScroll = v);

    public static readonly DirectProperty<ListChart, ICommand> ClickCommandProperty =
        AvaloniaProperty.RegisterDirect<ListChart, ICommand>(
            nameof(ClickCommand), o => o.ClickCommand, (o, v) => o.ClickCommand = v);

    /// <summary>
    /// 点击分类徽章时的命令，参数为 <see cref="ChartBadgeModel"/>
    /// </summary>
    public static readonly DirectProperty<ListChart, ICommand?> BadgeClickCommandProperty =
        AvaloniaProperty.RegisterDirect<ListChart, ICommand?>(
            nameof(BadgeClickCommand), o => o.BadgeClickCommand, (o, v) => o.BadgeClickCommand = v);

    public static readonly DirectProperty<ListChart, ContextMenuType> MenuTypeProperty =
        AvaloniaProperty.RegisterDirect<ListChart, ContextMenuType>(
            nameof(MenuType), o => o.MenuType, (o, v) => o.MenuType = v);

    public static readonly DirectProperty<ListChart, bool> IsShowBadgeProperty =
        AvaloniaProperty.RegisterDirect<ListChart, bool>(
            nameof(IsShowBadge), o => o.IsShowBadge, (o, v) => o.IsShowBadge = v);

    public static readonly DirectProperty<ListChart, double> IconSizeProperty =
        AvaloniaProperty.RegisterDirect<ListChart, double>(
            nameof(IconSize), o => o.IconSize, (o, v) => o.IconSize = v);

    public static readonly DirectProperty<ListChart, double> DataMaxValueProperty =
        AvaloniaProperty.RegisterDirect<ListChart, double>(
            nameof(DataMaxValue), o => o.DataMaxValue, (o, v) => o.DataMaxValue = v);

    /// <summary>
    /// 进度条颜色，不设置时跟随主题默认值
    /// </summary>
    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<ListChart, IBrush?>(nameof(BarBrush));

    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(150);

    private IEnumerable<ChartsDataModel> _data = [];
    private bool _isSearch;
    private bool _isCanScroll = true;
    private ICommand? _clickCommand;
    private ICommand? _badgeClickCommand;
    private ContextMenuType _menuType;
    private bool _isShowBadge;
    private double _iconSize = 25;
    private double _dataMaxValue;
    private string _searchText = string.Empty;
    private bool _isLoading;
    private bool _emptyCompact;
    private string _emptyText = string.Empty;

    private ListBox _listView;
    private TextBox _searchBox;
    private Run _countText;
    private EmptyData _emptyDataView;
    private DispatcherTimer _searchTimer;
    private bool _templateApplied;
    private bool _handlersAttached;
    private int _menuRequestSeq;

    public event EventHandler? OnItemClick;

    public IEnumerable<ChartsDataModel> Data
    {
        get => _data;
        set => SetAndRaise(DataProperty, ref _data, value);
    }

    public bool IsSearch
    {
        get => _isSearch;
        set => SetAndRaise(IsSearchProperty, ref _isSearch, value);
    }

    /// <summary>加载中：空列表时显示骨架而不是「没有数据」</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetAndRaise(IsLoadingProperty, ref _isLoading, value)) RefreshDisplayData();
        }
    }

    /// <summary>空态用紧凑样式（不画插画），适合卡片等窄区域</summary>
    public bool EmptyCompact
    {
        get => _emptyCompact;
        set
        {
            if (!SetAndRaise(EmptyCompactProperty, ref _emptyCompact, value)) return;
            if (_emptyDataView != null) _emptyDataView.IsCompact = value;
        }
    }

    /// <summary>空态文案，留空用默认的「暂时没有数据」</summary>
    public string EmptyText
    {
        get => _emptyText;
        set
        {
            if (!SetAndRaise(EmptyTextProperty, ref _emptyText, value)) return;
            if (_emptyDataView != null) _emptyDataView.Text = value;
        }
    }

    public bool IsCanScroll
    {
        get => _isCanScroll;
        set => SetAndRaise(IsCanScrollProperty, ref _isCanScroll, value);
    }

    public ICommand ClickCommand
    {
        get => _clickCommand;
        set => SetAndRaise(ClickCommandProperty, ref _clickCommand, value);
    }

    public ICommand? BadgeClickCommand
    {
        get => _badgeClickCommand;
        set => SetAndRaise(BadgeClickCommandProperty, ref _badgeClickCommand, value);
    }

    public ContextMenuType MenuType
    {
        get => _menuType;
        set => SetAndRaise(MenuTypeProperty, ref _menuType, value);
    }

    public bool IsShowBadge
    {
        get => _isShowBadge;
        set => SetAndRaise(IsShowBadgeProperty, ref _isShowBadge, value);
    }

    public double IconSize
    {
        get => _iconSize;
        set => SetAndRaise(IconSizeProperty, ref _iconSize, value);
    }

    public double DataMaxValue
    {
        get => _dataMaxValue;
        set => SetAndRaise(DataMaxValueProperty, ref _dataMaxValue, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ListChart);

    /// <summary>列表项用方向键选中后，Enter / Space 等同于点击</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || e.Key is not (Key.Enter or Key.Space)) return;
        if (_searchBox != null && _searchBox.IsFocused) return;
        if (_listView?.SelectedItem is not ChartsDataModel model) return;

        var command = ClickCommand;
        if (command == null || !command.CanExecute(model)) return;

        command.Execute(model);
        e.Handled = true;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DetachHandlers();

        _listView = e.NameScope.Get<ListBox>("ListView");
        _listView.Focusable = true;
        _listView.IsTabStop = true;
        _searchBox = e.NameScope.Get<TextBox>("SearchBox");
        _countText = e.NameScope.Get<Run>("CountText");
        _emptyDataView = e.NameScope.Find<EmptyData>("EmptyDataView");
        if (_emptyDataView != null)
        {
            _emptyDataView.Text = _emptyText;
            _emptyDataView.IsCompact = _emptyCompact;
        }

        UpdateScrollability();
        RefreshDisplayData();

        _templateApplied = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_templateApplied) return;

        if (change.Property == DataProperty)
            RefreshDisplayData();
        else if (change.Property == IsCanScrollProperty)
            UpdateScrollability();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        AttachHandlers();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        StopSearchTimer();
        DetachHandlers();
    }

    private void AttachHandlers()
    {
        if (_handlersAttached) return;
        if (_listView == null && _searchBox == null) return;

        if (_listView != null)
        {
            _listView.AddHandler(ChartsItemTypeList.ContextMenuRequestedEvent, OnItemContextMenuRequested, RoutingStrategies.Bubble, true);
            _listView.AddHandler(ChartsItemTypeList.ItemClickRequestedEvent, OnItemClickRequested, RoutingStrategies.Bubble, true);
        }

        if (_searchBox != null)
            _searchBox.TextChanged += OnSearchTextChanged;

        _handlersAttached = true;
    }

    private void DetachHandlers()
    {
        if (!_handlersAttached) return;

        if (_listView != null)
        {
            _listView.RemoveHandler(ChartsItemTypeList.ContextMenuRequestedEvent, OnItemContextMenuRequested);
            _listView.RemoveHandler(ChartsItemTypeList.ItemClickRequestedEvent, OnItemClickRequested);
        }

        if (_searchBox != null)
            _searchBox.TextChanged -= OnSearchTextChanged;

        _handlersAttached = false;
    }


    private void RefreshDisplayData()
    {
        if (_listView == null) return;

        var source = Data as IReadOnlyList<ChartsDataModel> ?? Data?.ToList();
        if (source == null || source.Count == 0)
        {
            DataMaxValue = 0;
            if (_countText != null) _countText.Text = "0";
            _listView.ItemsSource = Array.Empty<ChartsDataModel>();
            if (_emptyDataView != null)
            {
                _emptyDataView.IsLoading = _isLoading;
                _emptyDataView.Text = string.IsNullOrEmpty(_emptyText) ? null : _emptyText;
                _emptyDataView.IsVisible = true;
            }
            return;
        }
        if (_emptyDataView != null) _emptyDataView.IsVisible = false;

        double maxValue = 0;
        for (var i = 0; i < source.Count; i++)
        {
            var v = source[i].Value;
            if (v > maxValue) maxValue = v;
        }

        if (maxValue < 10) maxValue = 10;
        DataMaxValue = Math.Round(maxValue / 2, MidpointRounding.AwayFromZero) * 2 + 2;

        if (_countText != null) _countText.Text = source.Count.ToString();

        if (string.IsNullOrEmpty(_searchText))
        {
            _listView.ItemsSource = Data;
            _ = ListRowMotion.PlayStaggerInAsync(_listView);
        }
        else
        {
            ApplySearch(_searchText);
        }
    }

    private void UpdateScrollability()
    {
        if (_listView == null) return;

        if (!IsCanScroll)
            _listView.Classes.Add("noScroll");
        else
            _listView.Classes.Remove("noScroll");
    }

    #region Search

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = (_searchBox as InputBox)?.Text ?? string.Empty;
        if (text == _searchText) return;
        _searchText = text;
        StartSearchTimer();
    }

    private void StartSearchTimer()
    {
        if (_searchTimer == null)
        {
            _searchTimer = new DispatcherTimer { Interval = SearchDebounceDelay };
            _searchTimer.Tick += OnSearchTimerTick;
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void StopSearchTimer()
    {
        _searchTimer?.Stop();
    }

    private void OnSearchTimerTick(object? sender, EventArgs e)
    {
        StopSearchTimer();
        ApplySearch(_searchText);
    }


    private void ApplySearch(string keyword)
    {
        if (_listView == null) return;

        var source = Data as IReadOnlyList<ChartsDataModel> ?? Data?.ToList();
        if (source == null || source.Count == 0)
        {
            _listView.ItemsSource = Array.Empty<ChartsDataModel>();
            if (_countText != null) _countText.Text = "0";
            return;
        }

        if (string.IsNullOrEmpty(keyword))
        {
            _listView.ItemsSource = Data;
            if (_countText != null) _countText.Text = source.Count.ToString();
            return;
        }

        var result = new List<ChartsDataModel>(Math.Min(source.Count, 16));
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            if (MatchesSearch(item, keyword))
                result.Add(item);
        }

        _listView.ItemsSource = result;
        if (_countText != null) _countText.Text = result.Count.ToString();
    }

    private static bool MatchesSearch(ChartsDataModel item, string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return true;

        if ((item.Name?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (item.PopupText?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
            return true;

        var badges = item.BadgeList;
        if (badges == null) return false;

        // 针对 "忽略"/"ignore" 特殊处理：预先转小写一次，避免多次分配
        var lowerKeyword = keyword.ToLowerInvariant();
        if (ChartBadgeModel.IgnreLanguages.Contains(lowerKeyword))
        {
            for (var i = 0; i < badges.Count; i++)
                if (badges[i].Type == ChartBadgeType.Ignore) return true;
        }

        for (var i = 0; i < badges.Count; i++)
            if (badges[i].Name?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                return true;

        return false;
    }

#endregion

    #region Events

    private void OnItemClickRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is ChartsItemTypeList { Data: { } data })
        {
            OnItemClick?.Invoke(data, EventArgs.Empty);
            ClickCommand?.Execute(data);
        }
    }

    private async void OnItemContextMenuRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is ChartsItemTypeList { Data: { } data })
        {
            var seq = ++_menuRequestSeq;
            var menu = await CreateMenuForDataAsync(data);
            if (menu == null || seq != _menuRequestSeq) return;

            CloseExistingMenu();
            _listView.ContextMenu = menu;
            menu.Closed += OnContextMenuClosed;
            menu.Open(_listView);
        }
    }

    private void CloseExistingMenu()
    {
        if (_listView.ContextMenu is { } existing)
        {
            existing.Closed -= OnContextMenuClosed;
            if (existing.IsOpen) existing.Close();
            _listView.ContextMenu = null;
        }
    }

    private void OnContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnContextMenuClosed;
            if (ReferenceEquals(_listView.ContextMenu, menu))
                _listView.ContextMenu = null;
        }
    }

    private async Task<ContextMenu?> CreateMenuForDataAsync(ChartsDataModel data)
    {
        var servicer = ServiceLocator.GetService<IContextMenuServicer>();
        return servicer == null ? null : await servicer.CreateContextMenuAsync(MenuType, data);
    }

    #endregion
}
