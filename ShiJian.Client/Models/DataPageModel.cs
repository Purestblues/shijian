using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Controls.Timeline;
using ShiJian.Client.PageState;
using ShiJian.Client.Shared.Models;

namespace ShiJian.Client.Models;

[GeneratePageState]
public partial class DataPageModel : ModelBase
{
    private List<ChartsDataModel> _data;

    private DateTime _dayDate;

    private List<ChartsDataModel> _monthData;

    private DateTime _monthDate;
    private ObservableCollection<string> _tabbarData;

    private int _tabbarSelectedIndex;
    private SelectItemModel? _selectedPeriod;

    private bool _isHeaderExpanded = true;

    private List<ChartsDataModel> _yearData;
    private DateTime _yearDate;
    private List<ChartsDataModel> _weekData;
    private DateTime _weekDate;
    private bool _hasEntityError;
    private string _entityErrorText = string.Empty;

    public List<SelectItemModel> PeriodOptions { get; set; } = [];

    [PageState(LookupFrom = nameof(ShowTypeOptions))]
    public new SelectItemModel ShowType
    {
        get => base.ShowType;
        set
        {
            base.ShowType = value;
            OnPropertyChanged(nameof(IsAppDimension));
        }
    }

    /// <summary>应用维度（有表盘）/ 网站维度（没有表盘，榜单占满整行）</summary>
    public bool IsAppDimension => ShowType?.Id == 0;

    #region 环形时钟

    private ClockRingData _dayClock = ClockRingData.Empty;
    [PageState(DataCache = true)]
    public ClockRingData DayClock
    {
        get => _dayClock;
        set { _dayClock = value; OnPropertyChanged(); }
    }

    private ClockRingData _weekClock = ClockRingData.Empty;
    [PageState(DataCache = true)]
    public ClockRingData WeekClock
    {
        get => _weekClock;
        set { _weekClock = value; OnPropertyChanged(); }
    }

    private ClockRingData _monthClock = ClockRingData.Empty;
    [PageState(DataCache = true)]
    public ClockRingData MonthClock
    {
        get => _monthClock;
        set { _monthClock = value; OnPropertyChanged(); }
    }

    private ClockRingData _yearClock = ClockRingData.Empty;
    [PageState(DataCache = true)]
    public ClockRingData YearClock
    {
        get => _yearClock;
        set { _yearClock = value; OnPropertyChanged(); }
    }

    private bool _useCategoryColor;
    [PageState]
    public bool UseCategoryColor
    {
        get => _useCategoryColor;
        set { _useCategoryColor = value; OnPropertyChanged(); }
    }

    private SelectItemModel _selectedColorMode;
    [PageState(LookupFrom = nameof(ColorModeOptions))]
    public SelectItemModel SelectedColorMode
    {
        get => _selectedColorMode;
        set
        {
            _selectedColorMode = value;
            UseCategoryColor = value?.Id == 1;
            OnPropertyChanged();
        }
    }

    public List<SelectItemModel> ColorModeOptions { get; } =
    [
        new() { Id = 0, Name = ResourceStrings.TimelineAppColor },
        new() { Id = 1, Name = ResourceStrings.TimelineCategoryColor }
    ];

    #endregion

    #region 看板

    private DataPageInsight _dayInsight = DataPageInsight.Empty;
    [PageState(DataCache = true)]
    public DataPageInsight DayInsight
    {
        get => _dayInsight;
        set { _dayInsight = value; OnPropertyChanged(); }
    }

    private DataPageInsight _weekInsight = DataPageInsight.Empty;
    [PageState(DataCache = true)]
    public DataPageInsight WeekInsight
    {
        get => _weekInsight;
        set { _weekInsight = value; OnPropertyChanged(); }
    }

    private DataPageInsight _monthInsight = DataPageInsight.Empty;
    [PageState(DataCache = true)]
    public DataPageInsight MonthInsight
    {
        get => _monthInsight;
        set { _monthInsight = value; OnPropertyChanged(); }
    }

    private DataPageInsight _yearInsight = DataPageInsight.Empty;
    [PageState(DataCache = true)]
    public DataPageInsight YearInsight
    {
        get => _yearInsight;
        set { _yearInsight = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 当前周期的看板：四个周期的数据各自缓存，这里只指向当前那份，
    /// 免得 XAML 里把同一套面板写四遍
    /// </summary>
    private DataPageInsight _currentInsight = DataPageInsight.Empty;
    public DataPageInsight CurrentInsight
    {
        get => _currentInsight;
        set { _currentInsight = value; OnPropertyChanged(); }
    }

    /// <summary>窗口够不够大（太小就把右侧洞察栏和底部柱图藏掉，保证表盘还有地方）</summary>
    private bool _isInsightVisible = true;
    public bool IsInsightVisible
    {
        get => _isInsightVisible;
        set { _isInsightVisible = value; OnPropertyChanged(); }
    }

    private bool _isDistributionVisible = true;
    public bool IsDistributionVisible
    {
        get => _isDistributionVisible;
        set { _isDistributionVisible = value; OnPropertyChanged(); }
    }

    #endregion

    /// <summary>
    /// tabbar data
    /// </summary>
    public ObservableCollection<string> TabbarData
    {
        get => _tabbarData;
        set
        {
            _tabbarData = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// tabbar selected item index
    /// </summary>
    [PageState]
    public int TabbarSelectedIndex
    {
        get => _tabbarSelectedIndex;
        set
        {
            _tabbarSelectedIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 头部筛选栏是否展开
    /// </summary>
    [PageState]
    public bool IsHeaderExpanded
    {
        get => _isHeaderExpanded;
        set
        {
            _isHeaderExpanded = value;
            OnPropertyChanged();
        }
    }

    [PageState(LookupFrom = nameof(PeriodOptions))]
    public SelectItemModel? SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            _selectedPeriod = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// data
    /// </summary>
    [PageState(DataCache = true)]
    public List<ChartsDataModel> Data
    {
        get => _data;
        set
        {
            _data = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// MonthData
    /// </summary>
    [PageState(DataCache = true)]
    public List<ChartsDataModel> MonthData
    {
        get => _monthData;
        set
        {
            _monthData = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// YearData
    /// </summary>
    [PageState(DataCache = true)]
    public List<ChartsDataModel> YearData
    {
        get => _yearData;
        set
        {
            _yearData = value;
            OnPropertyChanged();
        }
    }

    [PageState(DataCache = true)]
    public List<ChartsDataModel> WeekData
    {
        get => _weekData;
        set
        {
            _weekData = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public DateTime WeekDate
    {
        get => _weekDate;
        set
        {
            _weekDate = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// date
    /// </summary>
    [PageState]
    public DateTime DayDate
    {
        get => _dayDate;
        set
        {
            _dayDate = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// date
    /// </summary>
    [PageState]
    public DateTime MonthDate
    {
        get => _monthDate;
        set
        {
            _monthDate = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// date
    /// </summary>
    [PageState]
    public DateTime YearDate
    {
        get => _yearDate;
        set
        {
            _yearDate = value;
            OnPropertyChanged();
        }
    }

    public bool HasEntityError
    {
        get => _hasEntityError;
        set
        {
            _hasEntityError = value;
            OnPropertyChanged();
        }
    }

    public string EntityErrorText
    {
        get => _entityErrorText;
        set
        {
            _entityErrorText = value;
            OnPropertyChanged();
        }
    }
}
