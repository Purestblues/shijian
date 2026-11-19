using System;
using System.Collections.Generic;
using Avalonia.Controls;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.PageState;

namespace ShiJian.Client.Models;

[GeneratePageState]
public partial class IndexPageModel : ModelBase
{
    private List<ChartsDataModel> _moreData = [];
    private List<ChartsDataModel> _appFrequentUseData;

    private int _frequentUseNum;

    private int _moreNum;

    private int _tabbarSelectedIndex;

    private List<ChartsDataModel> _webFrequentUseData;

    private List<ChartsDataModel> _weekData;

    private SelectItemModel _selectedPeriod;
    private List<SelectItemModel> _periodOptions;

    private string _compareCaption = string.Empty;
    private int _periodOffset;
    private string _periodLabel = string.Empty;
    private bool _canPageForward;

    private bool _isWebBrowserMerged;
    private SelectItemModel _webMergeOption;
    private bool _hasEntityError;
    private string _entityErrorText = string.Empty;

    /// <summary>
    /// 对比文字的周期描述（比昨日 / 比上周）
    /// </summary>
    [PageState]
    public string CompareCaption
    {
        get => _compareCaption;
        set
        {
            _compareCaption = value;
            OnPropertyChanged();
        }
    }

    /// <summary>N16：历史周期偏移量（0 = 当前周期，-1 = 上一周期，以此类推）</summary>
    [PageState]
    public int PeriodOffset
    {
        get => _periodOffset;
        set
        {
            if (_periodOffset == value) return;
            _periodOffset = value;
            OnPropertyChanged();
        }
    }

    /// <summary>N16：翻页条中间显示的当前周期文本</summary>
    [PageState]
    public string PeriodLabel
    {
        get => _periodLabel;
        set
        {
            if (_periodLabel == value) return;
            _periodLabel = value;
            OnPropertyChanged();
        }
    }

    /// <summary>N16：是否允许点击「下一周期」（当前已是最新周期时禁用）</summary>
    [PageState]
    public bool CanPageForward
    {
        get => _canPageForward;
        set
        {
            if (_canPageForward == value) return;
            _canPageForward = value;
            OnPropertyChanged();
        }
    }

    #region 概览卡片

    private double _totalTime;
    private double _lastTotalTime;
    private string _totalTimeText = "0";
    private double _appCount;
    private double _lastAppCount;
    private double _webTime;
    private double _lastWebTime;
    private string _webTimeText = "0";
    private double _webSiteCount;
    private double _lastWebSiteCount;
    private double _webPageCount;
    private double _lastWebPageCount;

    /// <summary>
    /// 总使用时长（应用+网站，秒）
    /// </summary>
    [PageState]
    public double TotalTime
    {
        get => _totalTime;
        set
        {
            _totalTime = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 上一个周期的总使用时长（用于对比）
    /// </summary>
    [PageState]
    public double LastTotalTime
    {
        get => _lastTotalTime;
        set
        {
            _lastTotalTime = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public string TotalTimeText
    {
        get => _totalTimeText;
        set
        {
            _totalTimeText = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public double AppCount
    {
        get => _appCount;
        set
        {
            _appCount = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public double LastAppCount
    {
        get => _lastAppCount;
        set
        {
            _lastAppCount = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public double WebTime
    {
        get => _webTime;
        set
        {
            _webTime = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public double LastWebTime
    {
        get => _lastWebTime;
        set
        {
            _lastWebTime = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public string WebTimeText
    {
        get => _webTimeText;
        set
        {
            _webTimeText = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public double WebSiteCount
    {
        get => _webSiteCount;
        set
        {
            _webSiteCount = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public double LastWebSiteCount
    {
        get => _lastWebSiteCount;
        set
        {
            _lastWebSiteCount = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public double WebPageCount
    {
        get => _webPageCount;
        set
        {
            _webPageCount = value;
            OnPropertyChanged();
        }
    }

    [PageState]
    public double LastWebPageCount
    {
        get => _lastWebPageCount;
        set
        {
            _lastWebPageCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 环形图中心标注（总计）
    /// </summary>
    [PageState]
    public string ChartDonutCaption
    {
        get => _chartDonutCaption;
        set
        {
            _chartDonutCaption = value;
            OnPropertyChanged();
        }
    }

    #endregion

    #region 图表

    private string _chartDonutCaption = string.Empty;

    private List<ChartsDataModel> _hourSeries = [];
    private string[] _hourLabels = [];
    private string _hourTotalText = "0";
    private List<ChartsDataModel> _categorySeries = [];
    private string _categoryTotalText = "0";

    /// <summary>
    /// 时段分布序列（每项为一条分类，Values 与 HourLabels 对齐）
    /// </summary>
    [PageState(DataCache = true)]
    public List<ChartsDataModel> HourSeries
    {
        get => _hourSeries;
        set
        {
            _hourSeries = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 时段分布底部刻度
    /// </summary>
    [PageState(DataCache = true)]
    public string[] HourLabels
    {
        get => _hourLabels;
        set
        {
            _hourLabels = value;
            OnPropertyChanged();
        }
    }

    [PageState(DataCache = true)]
    public string HourTotalText
    {
        get => _hourTotalText;
        set
        {
            _hourTotalText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 分类占比序列（Value 为分类总时长）
    /// </summary>
    [PageState(DataCache = true)]
    public List<ChartsDataModel> CategorySeries
    {
        get => _categorySeries;
        set
        {
            _categorySeries = value;
            OnPropertyChanged();
        }
    }

    [PageState(DataCache = true)]
    public string CategoryTotalText
    {
        get => _categoryTotalText;
        set
        {
            _categoryTotalText = value;
            OnPropertyChanged();
        }
    }

    #endregion

    #region 时段对比

    private double[] _compareTotals = [];
    private bool _isCompareEnabled = true;
    private bool _isCompareSupported = true;

    /// <summary>
    /// 当前维度是否支持时段对比（网站维度没有同粒度数据）
    /// </summary>
    [PageState]
    public bool IsCompareSupported
    {
        get => _isCompareSupported;
        set
        {
            if (_isCompareSupported == value) return;
            _isCompareSupported = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 是否叠加对比柱（如“比昨日”）
    /// </summary>
    [PageState]
    public bool IsCompareEnabled
    {
        get => _isCompareEnabled;
        set
        {
            if (_isCompareEnabled == value) return;
            _isCompareEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CompareValues));
        }
    }

    /// <summary>
    /// 对比时段的总量（与 HourLabels 对齐）；关闭开关或切换到网站维度时自动变空
    /// </summary>
    [PageState(DataCache = true)]
    public double[] CompareValues
    {
        get => _isCompareEnabled ? _compareTotals : [];
        set
        {
            _compareTotals = value ?? [];
            OnPropertyChanged();
        }
    }

    #endregion

    /// <summary>
    /// 周期选项（今日/本周）
    /// </summary>
    public List<SelectItemModel> PeriodOptions
    {
        get => _periodOptions;
        set
        {
            _periodOptions = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 选中的周期
    /// </summary>
    [PageState(LookupFrom = nameof(PeriodOptions))]
    public SelectItemModel SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            _selectedPeriod = value;
            OnPropertyChanged();
            if (value != null)
                TabbarSelectedIndex = value.Id;
        }
    }

    /// <summary>
    /// tabbar selected item index (内部使用)
    /// </summary>
    [PageState]
    public int TabbarSelectedIndex
    {
        get => _tabbarSelectedIndex;
        set
        {
            _tabbarSelectedIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomRange));
        }
    }

    private DateTime _customStart = DateTime.Now.Date.AddDays(-6);
    private DateTime _customEnd = DateTime.Now.Date;

    /// <summary>
    /// 自定义范围的起始日期
    /// </summary>
    [PageState]
    public DateTime CustomStart
    {
        get => _customStart;
        set
        {
            var date = value.Date;
            if (_customStart == date) return;
            _customStart = date;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 自定义范围的结束日期
    /// </summary>
    [PageState]
    public DateTime CustomEnd
    {
        get => _customEnd;
        set
        {
            var date = value.Date;
            if (_customEnd == date) return;
            _customEnd = date;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 当前周期是否为「自定义」
    /// </summary>
    public bool IsCustomRange => _tabbarSelectedIndex == 2;

    /// <summary>
    /// 网站是否并入浏览器（N2 视图开关）。
    /// 打开后站点行消失、浏览器行拿回完整活跃时长；关闭时站点独立成行、浏览器只剩「其他页面」。
    /// 只改变视图口径，不动任何原始数据。
    /// </summary>
    public bool IsWebBrowserMerged
    {
        get => _isWebBrowserMerged;
        set
        {
            if (_isWebBrowserMerged == value) return;
            _isWebBrowserMerged = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWebListVisible));
        }
    }

    /// <summary>关闭「并入浏览器」时右侧的网站列才有内容</summary>
    public bool IsWebListVisible => !_isWebBrowserMerged;

    /// <summary>实体接口不可用（多为服务端版本过旧 / 加载失败）</summary>
    public bool HasEntityError
    {
        get => _hasEntityError;
        set
        {
            if (_hasEntityError == value) return;
            _hasEntityError = value;
            OnPropertyChanged();
        }
    }

    public string EntityErrorText
    {
        get => _entityErrorText;
        set
        {
            if (_entityErrorText == value) return;
            _entityErrorText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>头部「独立 / 并入浏览器」开关的选中项</summary>
    public SelectItemModel WebMergeOption
    {
        get => _webMergeOption;
        set
        {
            if (ReferenceEquals(_webMergeOption, value)) return;
            _webMergeOption = value;
            OnPropertyChanged();
        }
    }

    public List<SelectItemModel> WebMergeOptions { get; set; } = [];

    /// <summary>
    /// week data
    /// </summary>
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

    /// <summary>
    /// 最为频繁条数
    /// </summary>
    public int FrequentUseNum
    {
        get => _frequentUseNum;
        set
        {
            _frequentUseNum = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 更多条数
    /// </summary>
    public int MoreNum
    {
        get => _moreNum;
        set
        {
            _moreNum = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 网页最为频繁数据
    /// </summary>
    [PageState(DataCache = true)]
    public List<ChartsDataModel> WebFrequentUseData
    {
        get => _webFrequentUseData;
        set
        {
            _webFrequentUseData = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 应用最为频繁数据
    /// </summary>
    [PageState(DataCache = true)]
    public List<ChartsDataModel> AppFrequentUseData
    {
        get => _appFrequentUseData;
        set
        {
            _appFrequentUseData = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 统一更多数据（应用 + 网站合并按时长排序）
    /// </summary>
    [PageState(DataCache = true)]
    public List<ChartsDataModel> MoreData
    {
        get => _moreData;
        set
        {
            _moreData = value;
            OnPropertyChanged();
        }
    }
}
