using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Styling;
using ShiJian.Client.Base.Color;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Controls.Timeline;
using ShiJian.Client.Events;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Logging;
using ShiJian.Client.Models;
using ShiJian.Client.Models.Navigation;
using ShiJian.Client.Servicers;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Shared.Models;
using ShiJian.Client.Shared.Models.Data;
using ShiJian.Client.Shared.Models.Entity;
using ShiJian.Client.Shared.Models.Web;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.Views;
using AppModel = ShiJian.Client.Shared.Models.AppModel;

namespace ShiJian.Client.ViewModels;

public partial class DataPageViewModel : DataPageModel
{
    private readonly IEntityData _entityData;
    private readonly IAppData _appDataService;
    private readonly IWebData _webDataService;
    private readonly IData _dataService;
    private readonly ICategorys _categoryService;
    private readonly INavigationService _navigationService;
    private readonly IStateService _stateService;
    private readonly IAppEventService _appEventService;

    // 会话 / 浏览记录只缓存最近一次请求：一次加载里环形时钟和看板用的是同一段区间，
    // 命中缓存就不会把几 MB 的会话数据拉两遍
    private DateTime _sessionCacheStart;
    private DateTime _sessionCacheEnd;
    private List<AppSessionModel>? _sessionCache;
    private DateTime _browseCacheStart;
    private DateTime _browseCacheEnd;
    private List<WebBrowseLogModel>? _browseCache;

    public DataPageViewModel(
        IEntityData entityData,
        IAppData appData,
        IData data,
        IWebData webData,
        ICategorys categories,
        INavigationService navigationService,
        IStateService stateService,
        IAppEventService appEventService)
    {
        _entityData = entityData;
        _appDataService = appData;
        _dataService = data;
        _webDataService = webData;
        _categoryService = categories;
        _navigationService = navigationService;
        _stateService = stateService;
        _appEventService = appEventService;

        ToDetailCommand = AsyncRelayCommand.CreateFromTask<object>(OnToDetailAsync).DisposeWith(Disposables);
        ClockLegendClickCommand = AsyncRelayCommand.CreateFromTask<object>(OnClockLegendClickAsync).DisposeWith(Disposables);
        RefreshCommand = AsyncRelayCommand.CreateFromTask<object>(OnRefreshAsync).DisposeWith(Disposables);
        ToggleHeaderCommand = AsyncRelayCommand.Create(() => { IsHeaderExpanded = !IsHeaderExpanded; })
            .DisposeWith(Disposables);
        Initialize();

        RefreshOnChange(
            _appEventService.AppChanged,
            _appEventService.WebSiteChanged,
            RefreshAsync);

        ChartColorHelper.ThemeChanged += OnThemeChanged;
    }

    /// <summary>主题变了：环形时钟的配色要按新背景重算（分类色靠 ChartColorHelper）</summary>
    private void OnThemeChanged()
    {
        if (!DayClock.HasData && !WeekClock.HasData && !MonthClock.HasData && !YearClock.HasData) return;
        _ = RefreshAsync();
    }

    public ICommand ToDetailCommand { get; }
    public ICommand ClockLegendClickCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleHeaderCommand { get; }

    public override void Dispose()
    {
        ChartColorHelper.ThemeChanged -= OnThemeChanged;
        base.Dispose();
    }

    private void Initialize()
    {
        TabbarData = [ResourceStrings.Daily, ResourceStrings.Weekly, ResourceStrings.Monthly, ResourceStrings.Yearly];

        PeriodOptions =
        [
            new() { Id = 0, Name = ResourceStrings.Daily },
            new() { Id = 1, Name = ResourceStrings.Weekly },
            new() { Id = 2, Name = ResourceStrings.Monthly },
            new() { Id = 3, Name = ResourceStrings.Yearly }
        ];

        DayDate = DateTime.Now.Date;
        WeekDate = DateTime.Now.Date;
        MonthDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        YearDate = new DateTime(DateTime.Now.Year, 1, 1);

        WhenPropertyChanged(this, x => x.DayDate, date => OnDateChangedAsync(date, 0));
        WhenPropertyChanged(this, x => x.WeekDate, date => OnDateChangedAsync(date, 1));
        WhenPropertyChanged(this, x => x.MonthDate, date => OnDateChangedAsync(date, 2));
        WhenPropertyChanged(this, x => x.YearDate, date => OnDateChangedAsync(date, 3));

        WhenPropertyChanged(this, x => x.SelectedPeriod, p =>
        {
            if (p != null) TabbarSelectedIndex = p.Id;
            return Task.CompletedTask;
        });

        WhenPropertyChanged(this, x => x.TabbarSelectedIndex, async index =>
        {
            DateTime date;
            int dataType;
            if (index == 0)
            {
                if (DayDate == DateTime.MinValue) DayDate = DateTime.Now.Date;
                (date, dataType) = (DayDate, 0);
            }
            else if (index == 1)
            {
                if (WeekDate == DateTime.MinValue) WeekDate = DateTime.Now.Date;
                (date, dataType) = (WeekDate, 1);
            }
            else if (index == 2)
            {
                if (MonthDate == DateTime.MinValue) MonthDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                (date, dataType) = (MonthDate, 2);
            }
            else if (index == 3)
            {
                if (YearDate == DateTime.MinValue) YearDate = new DateTime(DateTime.Now.Year, 1, 1);
                (date, dataType) = (YearDate, 3);
            }
            else
            {
                return;
            }

            // 已缓存时立即切换避免等待；首次加载新周期时保留当前看板并靠 IsLoading 抑制「暂时没有数据」闪烁
            if (InsightAt(index) is { HasData: true } cached)
                CurrentInsight = cached;
            await LoadDataAsync(date, dataType);
        }, skipInitial: false);

        // 配色模式只影响扇区取色与图例口径，数据不用重拉：扇区里两种颜色都已备好
        WhenPropertyChanged(this, x => x.UseCategoryColor, _ =>
        {
            DayClock = RebuildLegend(DayClock);
            WeekClock = RebuildLegend(WeekClock);
            MonthClock = RebuildLegend(MonthClock);
            YearClock = RebuildLegend(YearClock);
            return Task.CompletedTask;
        });

        SelectedPeriod = PeriodOptions[0];
        SelectedColorMode = ColorModeOptions[0];
        TabbarSelectedIndex = 0;
    }


    public override Task OnNavigatedToAsync()
    {
        var restored = TryRestoreState(_navigationService, _stateService);
        // 状态恢复只还原了数据，当前看板指针要手动指过去
        CurrentInsight = InsightAt(TabbarSelectedIndex);
        return RestoreCachedDataOrLoadAsync(
            restored,
            _stateService,
            _appEventService,
            () => LoadDataAsync(DayDate, 0));
    }

    public override void OnNavigatedFrom()
    {
        SaveState(_stateService);
        SaveCachedDataVersion(_stateService, _appEventService);
        base.OnNavigatedFrom();
    }

    private Task OnDateChangedAsync(DateTime date, int dataType)
    {
        return LoadDataAsync(date, dataType);
    }

    private Task OnRefreshAsync(object _)
    {
        var (date, dataType) = TabbarSelectedIndex switch
        {
            0 => (DayDate, 0),
            1 => (WeekDate, 1),
            2 => (MonthDate, 2),
            3 => (YearDate, 3),
            _ => (DayDate, 0)
        };
        return LoadDataAsync(date, dataType);
    }

    public override Task RefreshAsync() => OnRefreshAsync(null!);

    private async Task OnToDetailAsync(object obj)
    {
        var date = TabbarSelectedIndex switch { 0 => DayDate, 1 => WeekDate, 2 => MonthDate, 3 => YearDate, _ => DateTime.Now };

        switch (obj)
        {
            case ClockSegment { Data: EntityModel segmentEntity }:
                await NavigateToEntityAsync(segmentEntity, date);
                break;
            case ChartsDataModel { Data: EntityModel rowEntity }:
                await NavigateToEntityAsync(rowEntity, date);
                break;
            case ClockSegment { Data: AppModel app }:
                _navigationService.NavigateTo(nameof(DetailPage), DetailNavigationContext.Create(app, TabbarSelectedIndex, date));
                break;
            case ChartsDataModel { Data: DailyLogModel { AppModel: not null } model }:
                _navigationService.NavigateTo(nameof(DetailPage), DetailNavigationContext.Create(model.AppModel, TabbarSelectedIndex, date));
                break;
            case ChartsDataModel { Data: WebSiteModel webSite }:
                _navigationService.NavigateTo(nameof(WebSiteDetailPage), WebSiteDetailNavigationContext.Create(webSite, TabbarSelectedIndex, date));
                break;
        }
    }

    /// <summary>点图例：应用/实体 → 详情；分类 → 该分类的汇总页</summary>
    private async Task OnClockLegendClickAsync(object obj)
    {
        var date = TabbarSelectedIndex switch { 0 => DayDate, 1 => WeekDate, 2 => MonthDate, 3 => YearDate, _ => DateTime.Now };

        switch (obj)
        {
            case ClockLegendItem { Data: EntityModel entity }:
                await NavigateToEntityAsync(entity, date);
                break;
            case ClockLegendItem { Data: AppModel app }:
                _navigationService.NavigateTo(nameof(DetailPage), DetailNavigationContext.Create(app, TabbarSelectedIndex, date));
                break;
            case ClockLegendItem { CategoryData: Shared.Models.CategoryModel category }:
                _navigationService.NavigateTo(nameof(CategorySummaryPage),
                    CategorySummaryNavigationContext.Create(CategorySummaryKind.Unified, category.ID, category.Name));
                break;
        }
    }

    private async Task NavigateToEntityAsync(EntityModel entity, DateTime date)
    {
        try
        {
            if (entity.Kind == "site" || entity.Key.StartsWith("site:", StringComparison.Ordinal))
            {
                var site = await _webDataService.GetWebSiteAsync(entity.EntityId);
                if (site != null)
                    _navigationService.NavigateTo(nameof(WebSiteDetailPage),
                        WebSiteDetailNavigationContext.Create(site, TabbarSelectedIndex, date));
                return;
            }

            var app = await _appDataService.GetAppAsync(entity.EntityId);
            if (app != null)
                _navigationService.NavigateTo(nameof(DetailPage),
                    DetailNavigationContext.Create(app, TabbarSelectedIndex, date));
        }
        catch (Exception ex)
        {
            Logger.Error("打开实体详情失败", ex);
        }
    }

    private Task LoadDataAsync(DateTime date, int dataType)
    {
        return ExecuteAsync(async ct =>
        {
            HasEntityError = false;
            EntityErrorText = string.Empty;

            var (start, end) = GetDateRange(date, dataType);
            EntityListModel? entityList;
            try
            {
                entityList = await _entityData.GetEntityListAsync(start, end, browserMerged: false, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                HasEntityError = true;
                EntityErrorText = ChartDataMapper.FormatEntityLoadError(ex);
                ApplyInsight(dataType, DataPageInsight.Empty);
                Logger.Warn($"详细页实体数据加载失败：{ex.Message}");
                return;
            }
            ct.ThrowIfCancellationRequested();

            var rows = (entityList?.Rows ?? []).OrderByDescending(r => r.Seconds).ToList();
            var chartData = ChartDataMapper.MapFromEntities(rows, includeBadges: true);

            switch (dataType)
            {
                case 0:
                    Data = chartData;
                    DayClock = await BuildDayClockAsync(date, ct);
                    break;
                case 1:
                    WeekData = chartData;
                    WeekClock = await BuildHourlyClockAsync(start, end, ClockTickMode.Day,
                        FormatClockCaption(1, start, end), ct);
                    break;
                case 2:
                    MonthData = chartData;
                    MonthClock = await BuildHourlyClockAsync(start, end, ClockTickMode.Day,
                        FormatClockCaption(2, start, end), ct);
                    break;
                case 3:
                    YearData = chartData;
                    YearClock = await BuildMonthlyClockAsync(start, end, ct);
                    break;
            }

            ct.ThrowIfCancellationRequested();

            // 看板只算当前页签，避免同时预热四个周期时重复请求会话流水
            if (dataType != TabbarSelectedIndex) return;

            var insight = await BuildInsightAsync(date, dataType, ct);
            ApplyInsight(dataType, insight ?? DataPageInsight.Empty);
        });
    }

    #region 环形时钟

    /// <summary>图例只列前几名，剩下的合并成「其他」</summary>
    private const int ClockLegendTopCount = 5;

    private static bool IsDarkTheme() => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>服务端的会话 / 小时桶时间戳是 UTC，画到表盘上必须先换成本地时间</summary>
    private static DateTime ToLocal(DateTime utc) =>
        utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    private static readonly string[] KnownBrowserStems =
    [
        "chrome", "chromium", "msedge", "brave", "vivaldi", "arc", "thorium",
        "360se", "360chrome", "qqbrowser", "firefox", "librewolf", "waterfox",
        "zen", "safari"
    ];

    internal static string FormatClockCaption(int dataType, DateTime rangeStart, DateTime rangeEnd,
        DateTime? referenceToday = null)
    {
        var today = (referenceToday ?? DateTime.Today).Date;
        var rangeLast = rangeEnd.TimeOfDay == TimeSpan.Zero && rangeEnd > rangeStart
            ? rangeEnd.AddSeconds(-1).Date
            : rangeEnd.Date;

        return dataType switch
        {
            0 => rangeStart.Date == today
                ? ResourceStrings.Today
                : rangeStart.Year == today.Year
                    ? rangeStart.ToString("M'/'d", CultureInfo.InvariantCulture)
                    : rangeStart.ToString("yyyy'/'M'/'d", CultureInfo.InvariantCulture),
            1 => today >= rangeStart.Date && today <= rangeLast
                ? ResourceStrings.ThisWeek
                : $"{rangeStart.ToString("M'/'d", CultureInfo.InvariantCulture)}~{rangeLast.ToString("M'/'d", CultureInfo.InvariantCulture)}",
            2 => rangeStart.Year == today.Year && rangeStart.Month == today.Month
                ? ResourceStrings.ThisMonth
                : rangeStart.ToString("yyyy'/'M", CultureInfo.InvariantCulture),
            3 => rangeStart.Year == today.Year
                ? ResourceStrings.ThisYear
                : rangeStart.ToString("yyyy", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    private static bool IsBrowserApp(AppModel app, HashSet<int> entityBrowserIds)
    {
        if (entityBrowserIds.Contains(app.ID)) return true;
        var name = app.Name?.ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return false;
        return Array.Exists(KnownBrowserStems, stem => name.Contains(stem, StringComparison.Ordinal));
    }

    /// <summary>
    /// 按单小时把真实前景会话切分：若该小时存在来自 `/api/entity/hours` 的已归属站点秒数，
    /// 则将浏览器会话片段按网站各自秒数顺序拆分，其余保留给浏览器自身（与 `分类占比` 同源）。
    /// </summary>
    internal static List<ClockSegment> BuildDayClockSegments(
        DateTime date,
        IReadOnlyList<AppSessionModel> sessions,
        EntityHoursModel? entityHours,
        IReadOnlyDictionary<int, Shared.Models.CategoryModel> categoryMap,
        bool isDark)
    {
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);
        var periodSeconds = (dayEnd - dayStart).TotalSeconds;
        var segments = new List<ClockSegment>();

        var rows = entityHours?.Rows ?? [];
        var entityBrowserIds = rows
            .Where(r => r.IsBrowser && r.EntityId > 0)
            .Select(r => r.EntityId)
            .ToHashSet();
        var siteRows = rows
            .Where(r => string.Equals(r.Kind, "site", StringComparison.Ordinal) && r.Values.Length > 0)
            .ToList();

        var clipped = new List<(DateTime ClipStart, DateTime ClipEnd, AppModel App, bool IsBrowser)>();
        foreach (var session in sessions.Where(s => s.AppModel != null).OrderBy(s => s.StartTime))
        {
            var start = ToLocal(session.StartTime);
            var end = ToLocal(session.EndTime);
            if (end <= dayStart || start >= dayEnd) continue;

            var clipStart = start < dayStart ? dayStart : start;
            var clipEnd = end > dayEnd ? dayEnd : end;
            if (clipEnd <= clipStart) continue;

            clipped.Add((clipStart, clipEnd, session.AppModel!, IsBrowserApp(session.AppModel!, entityBrowserIds)));
        }

        for (var hour = 0; hour < 24; hour++)
        {
            var hourStart = dayStart.AddHours(hour);
            var hourEnd = hourStart.AddHours(1);
            var browserSlices = new List<(DateTime Start, DateTime End, AppModel App)>();

            foreach (var item in clipped)
            {
                if (item.ClipEnd <= hourStart || item.ClipStart >= hourEnd) continue;
                var segStart = item.ClipStart < hourStart ? hourStart : item.ClipStart;
                var segEnd = item.ClipEnd > hourEnd ? hourEnd : item.ClipEnd;
                if (segEnd <= segStart) continue;

                if (item.IsBrowser)
                {
                    browserSlices.Add((segStart, segEnd, item.App));
                }
                else
                {
                    AddAppSegment(segments, dayStart, periodSeconds, segStart, segEnd, item.App, isDark);
                }
            }

            if (browserSlices.Count == 0) continue;

            var hourSites = siteRows
                .Where(r => hour < r.Values.Length && r.Values[hour] > 0)
                .Select(r => (Row: r, Seconds: r.Values[hour]))
                .OrderByDescending(x => x.Seconds)
                .ToList();

            var browserTotal = browserSlices.Sum(s => (s.End - s.Start).TotalSeconds);
            var siteTotal = hourSites.Sum(x => x.Seconds);

            if (hourSites.Count == 0 || siteTotal <= 0 || browserTotal <= 0)
            {
                foreach (var slice in browserSlices)
                    AddAppSegment(segments, dayStart, periodSeconds, slice.Start, slice.End, slice.App, isDark);
                continue;
            }

            // 站点总时长不超过本小时浏览器前景会话时按精确秒数切分，否则等比例缩入会话长度
            var scale = siteTotal > browserTotal ? browserTotal / siteTotal : 1d;
            var allocCursor = 0d;
            var sliceOffsets = new List<(DateTime Start, DateTime End, AppModel App, double OffsetStart, double OffsetEnd)>(browserSlices.Count);
            var acc = 0d;
            foreach (var slice in browserSlices)
            {
                var len = (slice.End - slice.Start).TotalSeconds;
                sliceOffsets.Add((slice.Start, slice.End, slice.App, acc, acc + len));
                acc += len;
            }

            foreach (var (row, secs) in hourSites)
            {
                var allocLen = secs * scale;
                if (allocLen <= 0.5) continue;

                var allocStart = allocCursor;
                var allocEnd = Math.Min(browserTotal, allocCursor + allocLen);
                allocCursor = allocEnd;
                if (allocEnd <= allocStart) continue;

                categoryMap.TryGetValue(row.CategoryId, out var catModel);
                var catName = catModel?.Name ?? row.CategoryName;
                var catColor = ResolveCategoryColor(catModel?.Color ?? row.CategoryColor, catModel?.IsSystem ?? true,
                    catModel != null || !string.IsNullOrEmpty(row.CategoryName), isDark);
                var appColor = TimelineColorService.GetColor(row.Name, isDark);
                var entityModel = row.ToEntityModel((int)Math.Round(allocLen));

                foreach (var slice in sliceOffsets)
                {
                    var ovStart = Math.Max(allocStart, slice.OffsetStart);
                    var ovEnd = Math.Min(allocEnd, slice.OffsetEnd);
                    if (ovEnd - ovStart <= 0.5) continue;

                    var partStart = slice.Start.AddSeconds(ovStart - slice.OffsetStart);
                    var partEnd = slice.Start.AddSeconds(ovEnd - slice.OffsetStart);
                    segments.Add(new ClockSegment
                    {
                        StartFraction = (partStart - dayStart).TotalSeconds / periodSeconds,
                        EndFraction = (partEnd - dayStart).TotalSeconds / periodSeconds,
                        Name = row.Name,
                        CategoryName = catName,
                        Color = appColor,
                        CategoryColor = catColor,
                        RangeText = $"{partStart:HH:mm} ~ {partEnd:HH:mm}",
                        Seconds = (partEnd - partStart).TotalSeconds,
                        Data = entityModel,
                        CategoryData = catModel
                    });
                }
            }

            // 浏览器余额部分保留原生应用与分类
            if (browserTotal - allocCursor > 0.5)
            {
                foreach (var slice in sliceOffsets)
                {
                    var ovStart = Math.Max(allocCursor, slice.OffsetStart);
                    var ovEnd = slice.OffsetEnd;
                    if (ovEnd - ovStart <= 0.5) continue;

                    var partStart = slice.Start.AddSeconds(ovStart - slice.OffsetStart);
                    AddAppSegment(segments, dayStart, periodSeconds, partStart, slice.End, slice.App, isDark);
                }
            }
        }

        segments.Sort((a, b) => a.StartFraction.CompareTo(b.StartFraction));
        return segments;
    }

    private static void AddAppSegment(List<ClockSegment> segments, DateTime dayStart, double periodSeconds,
        DateTime segStart, DateTime segEnd, AppModel app, bool isDark)
    {
        var seconds = (segEnd - segStart).TotalSeconds;
        if (seconds <= 0) return;

        var name = app.GetDisplayName() ?? "Unknown";
        segments.Add(new ClockSegment
        {
            StartFraction = (segStart - dayStart).TotalSeconds / periodSeconds,
            EndFraction = (segEnd - dayStart).TotalSeconds / periodSeconds,
            Name = name,
            CategoryName = app.Category?.Name,
            Color = TimelineColorService.GetColor(name, isDark),
            CategoryColor = ResolveCategoryColor(app.Category, isDark),
            RangeText = $"{segStart:HH:mm} ~ {segEnd:HH:mm}",
            Seconds = seconds,
            Data = app,
            CategoryData = app.Category
        });
    }

    internal static List<ClockSegment> BuildHourlyClockSegments(
        EntityHoursModel? entityHours,
        IReadOnlyDictionary<int, Shared.Models.CategoryModel> categoryMap,
        DateTime rangeStart,
        DateTime rangeEnd,
        bool isDark)
    {
        var clockEnd = rangeEnd.TimeOfDay == TimeSpan.Zero ? rangeEnd : rangeEnd.Date.AddDays(1);
        var periodSeconds = Math.Max(1, (clockEnd - rangeStart).TotalSeconds);
        var segments = new List<ClockSegment>();
        var rows = entityHours?.Rows ?? [];
        if (rows.Count == 0) return segments;

        var bucketCount = Math.Max(entityHours?.BucketCount ?? 0, rows.Max(r => r.Values.Length));
        var labels = entityHours?.Labels ?? [];

        for (var i = 0; i < bucketCount; i++)
        {
            DateTime slotStart;
            if (i < labels.Count && DateTimeOffset.TryParse(labels[i], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dto))
            {
                slotStart = dto.DateTime;
            }
            else
            {
                slotStart = rangeStart.Date.AddHours(i);
            }

            if (slotStart < rangeStart || slotStart >= clockEnd) continue;

            var hourItems = rows
                .Where(r => i < r.Values.Length && r.Values[i] > 0)
                .Select(r => (Row: r, Seconds: r.Values[i]))
                .OrderByDescending(x => x.Seconds)
                .ToList();

            var slotTotal = hourItems.Sum(x => x.Seconds);
            if (slotTotal <= 0) continue;

            var slotFraction = (slotStart - rangeStart).TotalSeconds / periodSeconds;
            var slotWidth = Math.Min(3600d, (clockEnd - slotStart).TotalSeconds) / periodSeconds;
            var cursor = 0d;

            foreach (var (row, seconds) in hourItems)
            {
                var share = seconds / slotTotal;
                var startFraction = slotFraction + slotWidth * cursor;
                cursor += share;

                categoryMap.TryGetValue(row.CategoryId, out var catModel);
                var categoryColor = ResolveCategoryColor(catModel?.Color ?? row.CategoryColor,
                    catModel?.IsSystem ?? true,
                    catModel != null || !string.IsNullOrEmpty(row.CategoryName), isDark);

                segments.Add(new ClockSegment
                {
                    StartFraction = startFraction,
                    EndFraction = slotFraction + slotWidth * cursor,
                    Name = row.Name,
                    CategoryName = catModel?.Name ?? row.CategoryName,
                    Color = TimelineColorService.GetColor(row.Name, isDark),
                    CategoryColor = categoryColor,
                    RangeText = $"{slotStart:M/d HH:00} · {FormatDuration(seconds)}",
                    Seconds = seconds,
                    Data = row.ToEntityModel((int)Math.Round(seconds)),
                    CategoryData = catModel
                });
            }
        }

        return segments;
    }

    /// <summary>当天：用真实会话铺时段，并将浏览器时段按同小时归属的网站秒数子拆分</summary>
    private async Task<ClockRingData> BuildDayClockAsync(DateTime date, CancellationToken ct)
    {
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);

        var sessionsTask = GetSessionsCachedAsync(dayStart, dayEnd.AddSeconds(-1), ct);
        var hoursTask = _entityData.GetEntityHoursAsync(dayStart, dayStart, "hour", false, ct);
        var categoriesTask = _categoryService.GetCategoriesAsync(ct);
        await Task.WhenAll(sessionsTask, hoursTask, categoriesTask);
        ct.ThrowIfCancellationRequested();

        var sessions = await sessionsTask;
        var entityHours = await hoursTask;
        var categoryMap = (await categoriesTask).ToDictionary(c => c.ID);

        var segments = BuildDayClockSegments(dayStart, sessions, entityHours, categoryMap, IsDarkTheme());
        return BuildRingData(segments, ClockTickMode.Hour, dayStart, dayEnd,
            FormatClockCaption(0, dayStart, dayEnd));
    }

    /// <summary>
    /// 周 / 月：走 `/api/entity/hours?buckets=hour&amp;browserMerged=false`，
    /// 同小时内既能看见应用也能看见拆分后的网站实体与真实分类。
    /// </summary>
    private async Task<ClockRingData> BuildHourlyClockAsync(DateTime rangeStart, DateTime rangeEnd,
        ClockTickMode tickMode, string caption, CancellationToken ct)
    {
        var hoursTask = _entityData.GetEntityHoursAsync(rangeStart.Date, rangeEnd.Date, "hour", false, ct);
        var categoriesTask = _categoryService.GetCategoriesAsync(ct);
        await Task.WhenAll(hoursTask, categoriesTask);
        ct.ThrowIfCancellationRequested();

        var entityHours = await hoursTask;
        var categoryMap = (await categoriesTask).ToDictionary(c => c.ID);

        var segments = BuildHourlyClockSegments(entityHours, categoryMap, rangeStart, rangeEnd, IsDarkTheme());
        return BuildRingData(segments, tickMode, rangeStart, rangeEnd, caption);
    }

    /// <summary>
    /// 年：按月拆分，一个月里再按实体占比铺开。
    /// 直接走 `/api/entity/hours?buckets=month`（1 个请求拿齐 12 个月，且已带合并组与互斥扣减）。
    /// </summary>
    private async Task<ClockRingData> BuildMonthlyClockAsync(DateTime rangeStart, DateTime rangeEnd,
        CancellationToken ct)
    {
        var periodSeconds = Math.Max(1, (rangeEnd - rangeStart).TotalSeconds);
        var isDark = IsDarkTheme();
        var segments = new List<ClockSegment>();

        var hoursTask = _entityData.GetEntityHoursAsync(rangeStart, rangeEnd, "month", false, ct);
        var categoriesTask = _categoryService.GetCategoriesAsync(ct);
        await Task.WhenAll(hoursTask, categoriesTask);
        ct.ThrowIfCancellationRequested();

        var entityHours = await hoursTask;
        var categoryMap = (await categoriesTask).ToDictionary(c => c.ID);
        var rows = entityHours?.Rows ?? [];

        for (var i = 0; i < 12; i++)
        {
            var slotStart = new DateTime(rangeStart.Year, i + 1, 1);
            var monthItems = rows
                .Where(r => i < r.Values.Length && r.Values[i] > 0)
                .Select(r => (Row: r, Seconds: r.Values[i]))
                .OrderByDescending(x => x.Seconds)
                .ToList();

            var slotTotal = monthItems.Sum(x => x.Seconds);
            if (slotTotal <= 0) continue;

            var slotFraction = (slotStart - rangeStart).TotalSeconds / periodSeconds;
            var slotWidth = (slotStart.AddMonths(1) - slotStart).TotalSeconds / periodSeconds;
            var cursor = 0d;

            foreach (var (row, seconds) in monthItems)
            {
                var share = seconds / slotTotal;
                var startFraction = slotFraction + slotWidth * cursor;
                cursor += share;

                categoryMap.TryGetValue(row.CategoryId, out var catModel);
                var categoryColor = ResolveCategoryColor(catModel, isDark);
                object? categoryData = catModel;

                segments.Add(new ClockSegment
                {
                    StartFraction = startFraction,
                    EndFraction = slotFraction + slotWidth * cursor,
                    Name = row.Name,
                    CategoryName = row.CategoryName,
                    Color = TimelineColorService.GetColor(row.Name, isDark),
                    CategoryColor = categoryColor,
                    RangeText = $"{slotStart:yyyy-MM} · {FormatDuration(seconds)}",
                    Seconds = seconds,
                    Data = row.ToEntityModel((int)seconds),
                    CategoryData = categoryData
                });
            }
        }

        return BuildRingData(segments, ClockTickMode.Month, rangeStart, rangeEnd,
            FormatClockCaption(3, rangeStart, rangeEnd));
    }

    private static string ResolveCategoryColor(Shared.Models.CategoryModel? category, bool isDark) =>
        ResolveCategoryColor(category?.Color, category?.IsSystem ?? false, category != null, isDark);

    private static string ResolveCategoryColor(string? color, bool isSystem, bool hasCategory, bool isDark)
    {
        return hasCategory
            ? ChartColorHelper.Resolve(color, isSystem)
            : TimelineColorService.GetColor(ResourceStrings.SystemCategory, isDark);
    }

    private ClockRingData BuildRingData(List<ClockSegment> segments, ClockTickMode tickMode,
        DateTime rangeStart, DateTime rangeEnd, string caption)
    {
        var totalSeconds = segments.Sum(s => s.Seconds);

        return new ClockRingData
        {
            Segments = segments,
            Legend = BuildLegend(segments),
            CenterText = totalSeconds > 0 ? FormatDuration(totalSeconds) : string.Empty,
            CenterCaption = totalSeconds > 0 ? caption : string.Empty,
            TickMode = tickMode,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd
        };
    }

    /// <summary>只重算图例（应用 / 分类口径），扇区列表原样复用</summary>
    private ClockRingData RebuildLegend(ClockRingData source)
    {
        if (!source.HasData) return source;

        return new ClockRingData
        {
            Segments = source.Segments,
            Legend = BuildLegend(source.Segments),
            CenterText = source.CenterText,
            CenterCaption = source.CenterCaption,
            TickMode = source.TickMode,
            RangeStart = source.RangeStart,
            RangeEnd = source.RangeEnd
        };
    }

    /// <summary>图例跟配色模式走：应用色时列应用，分类色时列分类</summary>
    private List<ClockLegendItem> BuildLegend(List<ClockSegment> segments)
    {
        var total = segments.Sum(s => s.Seconds);
        if (total <= 0) return [];

        var useCategory = UseCategoryColor;
        var groups = segments
            .GroupBy(s => useCategory ? s.CategoryName ?? ResourceStrings.SystemCategory : s.Name)
            .Select(g => new
            {
                Name = g.Key,
                Seconds = g.Sum(s => s.Seconds),
                Color = useCategory ? g.First().CategoryColor : g.First().Color,
                Data = useCategory ? null : g.First().Data,
                CategoryData = useCategory ? g.First().CategoryData : null
            })
            .OrderByDescending(g => g.Seconds)
            .ToList();

        var legend = new List<ClockLegendItem>();
        var top = groups.Take(ClockLegendTopCount).ToList();

        foreach (var group in top)
        {
            legend.Add(new ClockLegendItem
            {
                Name = group.Name,
                Color = group.Color,
                DurationText = FormatDuration(group.Seconds),
                ShareText = $"{(int)Math.Round(group.Seconds / total * 100)}%",
                Data = group.Data,
                CategoryData = group.CategoryData
            });
        }

        var restSeconds = groups.Skip(ClockLegendTopCount).Sum(g => g.Seconds);
        if (restSeconds <= 0) return legend;

        legend.Add(new ClockLegendItem
        {
            Name = ResourceStrings.ClockLegendOther,
            Color = "#8b949e",
            DurationText = FormatDuration(restSeconds),
            ShareText = $"{(int)Math.Round(restSeconds / total * 100)}%",
            IsOther = true
        });

        return legend;
    }

    #endregion

    #region 看板

    /// <summary>热力图最多画几行分类（日视图）</summary>
    private const int HeatmapTopRows = 6;

    /// <summary>分类占比环形图最多画几段，剩下合并成「其他」</summary>
    private const int CategoryShareTopCount = 6;

    /// <summary>时段偏好横条的最大宽度（右侧洞察栏的可用宽度）</summary>
    private const double BandMaxWidth = 96;

    private DataPageInsight InsightAt(int index) => index switch
    {
        0 => DayInsight,
        1 => WeekInsight,
        2 => MonthInsight,
        3 => YearInsight,
        _ => DataPageInsight.Empty
    };

    private void ApplyInsight(int dataType, DataPageInsight insight)
    {
        switch (dataType)
        {
            case 0: DayInsight = insight; break;
            case 1: WeekInsight = insight; break;
            case 2: MonthInsight = insight; break;
            case 3: YearInsight = insight; break;
            default: return;
        }

        if (dataType == TabbarSelectedIndex) CurrentInsight = insight;
    }

    private async Task<DataPageInsight?> BuildInsightAsync(DateTime date, int dataType, CancellationToken ct)
    {
        var (start, end) = GetDateRange(date, dataType);
        var previousDate = PreviousDate(date, dataType);
        var (prevStart, prevEnd) = GetDateRange(previousDate, dataType);
        var bucketMode = dataType switch { 0 => "hour", 3 => "month", _ => "day" };

        var currentHoursTask = _entityData.GetEntityHoursAsync(start, end, bucketMode, false, ct);
        var prevHoursTask = _entityData.GetEntityHoursAsync(prevStart, prevEnd, bucketMode, false, ct);
        var summaryTask = _entityData.GetEntitySummaryAsync(start, end, false, ct);
        var prevSummaryTask = _entityData.GetEntitySummaryAsync(prevStart, prevEnd, false, ct);
        var categoriesTask = _categoryService.GetCategoriesAsync(ct);
        var spansTask = GetSpansAsync(start, end, isWeb: false, ct);

        try
        {
            await Task.WhenAll(currentHoursTask, prevHoursTask, summaryTask, prevSummaryTask,
                categoriesTask, spansTask);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            HasEntityError = true;
            EntityErrorText = ChartDataMapper.FormatEntityLoadError(ex);
            Logger.Warn($"详细页看板实体数据加载失败：{ex.Message}");
            return null;
        }
        ct.ThrowIfCancellationRequested();

        var currentHours = await currentHoursTask;
        var prevHours = await prevHoursTask;
        var summary = await summaryTask;
        var prevSummary = await prevSummaryTask;
        var categories = await categoriesTask;
        var spans = await spansTask;
        var periodSpans = spans.Select(s => (s.Start, s.End)).ToList();

        var buckets = ChartDataMapper.SumAllBuckets(currentHours);
        var prevBuckets = ChartDataMapper.SumAllBuckets(prevHours, buckets.Length);
        var labels = BucketLabels(dataType, buckets.Length, start);

        var totalSeconds = buckets.Sum();
        var prevTotalSeconds = prevBuckets.Sum();
        var hourOfDay = InsightBuilder.HourOfDayHistogram(periodSpans, start, end);

        var entityCount = (summary?.AppCount ?? 0) + (summary?.SiteCount ?? 0);
        var previousCount = (prevSummary?.AppCount ?? 0) + (prevSummary?.SiteCount ?? 0);

        var vsCaption = VersusCaption(dataType);
        var totalDiff = InsightBuilder.BuildDiff(totalSeconds, prevTotalSeconds);
        var countDiff = InsightBuilder.BuildDiff(entityCount, previousCount);

        var sessionCount = summary?.SessionCount ?? 0;
        var avgSessionSeconds = summary?.AverageSessionSeconds ?? 0;
        var longestSessionSeconds = summary?.LongestSessionSeconds ?? 0;
        var longestSessionStart = summary?.LongestSessionStart is { } ls ? ToLocal(ls) : (DateTime?)null;

        var cards = new List<InsightStatCard>(4)
        {
            BuildCard(ResourceStrings.CategoryTotalUsage, InsightBuilder.FormatDuration(totalSeconds), totalDiff, vsCaption),
            BuildCard(ResourceStrings.InsightEntities, entityCount.ToString(), countDiff, vsCaption),
            BuildCard(ResourceStrings.InsightSessions, sessionCount.ToString(), null, null,
                $"{ResourceStrings.InsightAverageSession} {InsightBuilder.FormatDuration(avgSessionSeconds)}"),
            BuildCard(ResourceStrings.InsightLongest, InsightBuilder.FormatDuration(longestSessionSeconds),
                null, null,
                longestSessionStart.HasValue ? InsightBuilder.FormatMoment(longestSessionStart.Value, start, end) : null)
        };

        var peakHour = InsightBuilder.PeakHour(hourOfDay);
        var activeCount = buckets.Count(v => v > 0);
        var activeCaption = dataType switch
        {
            0 => ResourceStrings.InsightActiveHours,
            3 => ResourceStrings.InsightActiveMonths,
            _ => ResourceStrings.CategoryActiveDays
        };

        var firstMoment = summary?.FirstSessionStart;
        var lastMoment = summary?.LastSessionEnd;

        var habits = new List<InsightHabitRow>
        {
            new()
            {
                Label = ResourceStrings.InsightFirstUse,
                Value = firstMoment.HasValue ? InsightBuilder.FormatMoment(ToLocal(firstMoment.Value), start, end) : "—"
            },
            new()
            {
                Label = ResourceStrings.InsightLastUse,
                Value = lastMoment.HasValue ? InsightBuilder.FormatMoment(ToLocal(lastMoment.Value), start, end) : "—"
            },
            new()
            {
                Label = ResourceStrings.InsightPeakHour,
                Value = peakHour >= 0 ? $"{peakHour:00}:00" : "—"
            },
            new()
            {
                Label = activeCaption,
                Value = activeCount.ToString()
            }
        };

        var bandNames = new[]
        {
            ResourceStrings.InsightBandDawn,
            ResourceStrings.InsightBandMorning,
            ResourceStrings.InsightBandAfternoon,
            ResourceStrings.InsightBandEvening
        };
        var bands = InsightBuilder.BuildBands(hourOfDay, bandNames, BandMaxWidth);

        var unifiedCategories = ChartDataMapper.BuildUnifiedCategorySeries(
            currentHours,
            ChartDataMapper.ToVisualMap(categories, ResourceStrings.Uncategorized),
            ResourceStrings.Uncategorized);
        var (categorySeries, categoryTotalText) = BuildUnifiedCategoryShare(unifiedCategories);

        var compare = new double[buckets.Length];
        for (var i = 0; i < compare.Length && i < prevBuckets.Length; i++) compare[i] = prevBuckets[i];

        var accent = ChartColorHelper.Resolve(null, true);
        var hourSeries = buckets.Length == 0
            ? []
            : new List<ChartsDataModel>
            {
                new()
                {
                    Name = ResourceStrings.CategoryTotalUsage,
                    Color = accent,
                    Values = buckets
                }
            };

        return new DataPageInsight
        {
            Cards = cards,
            Habits = habits,
            Bands = bands,
            Heatmap = BuildHeatmap(dataType, spans, periodSpans, buckets, start, end),
            HourSeries = hourSeries,
            HourLabels = labels,
            CompareValues = compare,
            CompareCaption = CompareCaption(dataType),
            BucketTotalText = InsightBuilder.FormatDuration(totalSeconds),
            CategorySeries = categorySeries,
            CategoryTotalText = categoryTotalText,
            CategoryCaption = ResourceStrings.Total
        };
    }

    private static InsightStatCard BuildCard(string caption, string value,
        InsightBuilder.InsightDiff? diff, string? diffCaption, string? sub = null)
    {
        return new InsightStatCard
        {
            Caption = caption,
            Value = value,
            Sub = sub,
            DiffText = diff?.Text,
            DiffCaption = diff is null ? null : diffCaption,
            IsUp = diff?.IsUp ?? false,
            IsDown = diff?.IsDown ?? false,
            IsFlat = diff?.IsFlat ?? false
        };
    }

    private static DateTime PreviousDate(DateTime date, int dataType) => dataType switch
    {
        0 => date.AddDays(-1),
        1 => date.AddDays(-7),
        2 => date.AddMonths(-1),
        3 => date.AddYears(-1),
        _ => date
    };

    private static string VersusCaption(int dataType) => dataType switch
    {
        0 => ResourceStrings.InsightVsYesterday,
        1 => ResourceStrings.InsightVsLastWeek,
        2 => ResourceStrings.InsightVsLastMonth,
        3 => ResourceStrings.InsightVsLastYear,
        _ => ResourceStrings.CategoryVsPrevious
    };

    private static string CompareCaption(int dataType) => dataType switch
    {
        0 => ResourceStrings.InsightCompareYesterday,
        1 => ResourceStrings.InsightCompareLastWeek,
        2 => ResourceStrings.InsightCompareLastMonth,
        3 => ResourceStrings.InsightCompareLastYear,
        _ => ResourceStrings.CategoryVsPrevious
    };

    private static string[] BucketLabels(int dataType, int count, DateTime rangeStart)
    {
        var labels = new string[Math.Max(0, count)];
        for (var i = 0; i < labels.Length; i++)
        {
            labels[i] = dataType switch
            {
                0 => i.ToString(),
                1 => WeekdayName(i),
                2 => rangeStart.AddDays(i).Day.ToString(),
                3 => MonthName(i),
                _ => i.ToString()
            };
        }

        return labels;
    }

    private static string WeekdayName(int index) => index switch
    {
        0 => ResourceStrings.Monday,
        1 => ResourceStrings.Tuesday,
        2 => ResourceStrings.Wednesday,
        3 => ResourceStrings.Thursday,
        4 => ResourceStrings.Friday,
        5 => ResourceStrings.Saturday,
        6 => ResourceStrings.Sunday,
        _ => string.Empty
    };

    private static string MonthName(int index) =>
        Application.Current?.Resources[$"{index + 1}Month"] as string ?? $"{index + 1}";

    private static (IReadOnlyList<ChartsDataModel> Series, string TotalText) BuildUnifiedCategoryShare(
        IReadOnlyList<ChartsDataModel> unifiedSeries)
    {
        var positive = unifiedSeries.Where(m => m.Value > 0).OrderByDescending(m => m.Value).ToList();
        var total = positive.Sum(m => m.Value);
        if (total <= 0) return ([], string.Empty);

        var series = positive.Take(CategoryShareTopCount)
            .Select(m => new ChartsDataModel
            {
                Name = m.Name,
                Color = m.Color,
                Value = m.Value,
                Tag = InsightBuilder.FormatDuration(m.Value)
            })
            .ToList();

        var rest = positive.Skip(CategoryShareTopCount).Sum(m => m.Value);
        if (rest > 0)
        {
            series.Add(new ChartsDataModel
            {
                Name = ResourceStrings.ClockLegendOther,
                Color = "#8b949e",
                Value = rest,
                Tag = InsightBuilder.FormatDuration(rest)
            });
        }

        return (series, InsightBuilder.FormatDuration(total));
    }

    #region 热力图

    private HeatmapData BuildHeatmap(int dataType,
        IReadOnlyList<(DateTime Start, DateTime End, string Name)> namedSpans,
        IReadOnlyList<(DateTime Start, DateTime End)> spans,
        IReadOnlyList<double> dayBuckets,
        DateTime rangeStart, DateTime rangeEnd)
    {
        return dataType switch
        {
            0 => BuildNameHourHeatmap(namedSpans, rangeStart, rangeEnd),
            2 => BuildMonthCalendarHeatmap(dayBuckets, rangeStart),
            3 => BuildMonthDayHeatmap(spans, rangeStart, rangeEnd),
            _ => BuildWeekHourHeatmap(spans, rangeStart, rangeEnd)
        };
    }

    /// <summary>
    /// 月视图：日历热力（行＝周、列＝星期、格子＝当天总时长）。
    /// 月里已经有「每天时长」柱图，但柱图看的是量级，日历看的是「哪几天连着头忙」。
    /// </summary>
    private static HeatmapData BuildMonthCalendarHeatmap(IReadOnlyList<double> dayTotals, DateTime rangeStart)
    {
        if (dayTotals.Count == 0) return HeatmapData.Empty;

        var max = dayTotals.Max();
        if (max <= 0) return HeatmapData.Empty;

        // 每周从周一算起
        var firstColumn = ((int)rangeStart.DayOfWeek + 6) % 7;
        var weeks = (int)Math.Ceiling((firstColumn + dayTotals.Count) / 7d);

        var rows = new List<HeatRow>(weeks);
        var peakLabel = string.Empty;
        var peakValue = 0d;

        for (var week = 0; week < weeks; week++)
        {
            var cells = new List<HeatCell>(7);
            for (var column = 0; column < 7; column++)
            {
                var index = week * 7 + column - firstColumn;
                if (index < 0 || index >= dayTotals.Count)
                {
                    cells.Add(new HeatCell { IsPlaceholder = true });
                    continue;
                }

                var day = rangeStart.AddDays(index);
                var value = dayTotals[index];
                cells.Add(new HeatCell
                {
                    Value = value,
                    Tip = $"{day:M/d} {WeekdayName(column)} · {InsightBuilder.FormatDuration(value)}"
                });

                if (value <= peakValue) continue;
                peakValue = value;
                peakLabel = $"{day:M/d} {WeekdayName(column)}";
            }

            rows.Add(new HeatRow
            {
                Label = string.Format(ResourceStrings.InsightWeekOfMonth, week + 1),
                Cells = cells
            });
        }

        return new HeatmapData
        {
            Rows = rows,
            ColumnLabels = Enumerable.Range(0, 7).Select(WeekdayName).ToArray(),
            Caption = ResourceStrings.InsightHeatmapByCalendar,
            PeakText = $"{peakLabel} · {InsightBuilder.FormatDuration(peakValue)}",
            MaxValue = max,
            IsCalendar = true
        };
    }

    /// <summary>日视图：当天用得最多的几个应用 / 站点 × 小时</summary>
    private static HeatmapData BuildNameHourHeatmap(
        IReadOnlyList<(DateTime Start, DateTime End, string Name)> spans,
        DateTime rangeStart, DateTime rangeEnd)
    {
        var grid = InsightBuilder.NameHourGrid(spans, rangeStart, rangeEnd);
        if (grid.Count == 0) return HeatmapData.Empty;

        var rows = grid
            .Where(x => x.Value.Sum() > 0)
            .OrderByDescending(x => x.Value.Sum())
            .Take(HeatmapTopRows)
            .ToList();

        if (rows.Count == 0) return HeatmapData.Empty;

        var max = rows.Max(x => x.Value.Max());
        if (max <= 0) return HeatmapData.Empty;

        var result = new List<HeatRow>(rows.Count);
        var peakName = string.Empty;
        var peakHour = 0;
        var peakValue = 0d;

        foreach (var (name, values) in rows)
        {
            var label = ShortenName(name);
            var cells = new List<HeatCell>(24);
            for (var hour = 0; hour < 24; hour++)
            {
                var value = values[hour];
                cells.Add(new HeatCell
                {
                    Value = value,
                    Tip = $"{name} {hour:00}:00 · {InsightBuilder.FormatDuration(value)}"
                });

                if (value <= peakValue) continue;
                peakValue = value;
                peakHour = hour;
                peakName = name;
            }

            result.Add(new HeatRow { Label = label, Cells = cells });
        }

        return new HeatmapData
        {
            Rows = result,
            ColumnLabels = Enumerable.Range(0, 24).Select(i => i.ToString()).ToArray(),
            Caption = ResourceStrings.InsightHeatmapByApp,
            PeakText = $"{peakName} {peakHour:00}:00 · {InsightBuilder.FormatDuration(peakValue)}",
            MaxValue = max
        };
    }

    /// <summary>热力图左侧行名只有窄窄一列，长的应用名截断，免得压到格子上</summary>
    private static string ShortenName(string name, int max = 7)
    {
        if (string.IsNullOrEmpty(name) || name.Length <= max) return name;
        return string.Concat(name.AsSpan(0, max), "…");
    }

    /// <summary>星期 × 小时：作息地图</summary>
    private static HeatmapData BuildWeekHourHeatmap(IReadOnlyList<(DateTime Start, DateTime End)> spans,
        DateTime rangeStart, DateTime rangeEnd)
    {
        var grid = InsightBuilder.WeekHourGrid(spans, rangeStart, rangeEnd);
        var max = InsightBuilder.Max(grid);
        if (max <= 0) return HeatmapData.Empty;

        var rows = new List<HeatRow>(7);
        var peakLabel = string.Empty;
        var peakValue = 0d;

        for (var row = 0; row < 7; row++)
        {
            var weekday = WeekdayName(row);
            var cells = new List<HeatCell>(24);
            for (var hour = 0; hour < 24; hour++)
            {
                var value = grid[row, hour];
                cells.Add(new HeatCell
                {
                    Value = value,
                    Tip = $"{weekday} {hour:00}:00 · {InsightBuilder.FormatDuration(value)}"
                });

                if (value <= peakValue) continue;
                peakValue = value;
                peakLabel = $"{weekday} {hour:00}:00";
            }

            rows.Add(new HeatRow { Label = weekday, Cells = cells });
        }

        return new HeatmapData
        {
            Rows = rows,
            ColumnLabels = Enumerable.Range(0, 24).Select(i => i.ToString()).ToArray(),
            Caption = ResourceStrings.InsightHeatmapByWeekday,
            PeakText = $"{peakLabel} · {InsightBuilder.FormatDuration(peakValue)}",
            MaxValue = max
        };
    }

    /// <summary>月份 × 日期：一年的日历热力</summary>
    private static HeatmapData BuildMonthDayHeatmap(IReadOnlyList<(DateTime Start, DateTime End)> spans,
        DateTime rangeStart, DateTime rangeEnd)
    {
        var grid = InsightBuilder.MonthDayGrid(spans, rangeStart, rangeEnd);
        var max = InsightBuilder.Max(grid);
        if (max <= 0) return HeatmapData.Empty;

        var rows = new List<HeatRow>(12);
        var peakLabel = string.Empty;
        var peakValue = 0d;

        for (var month = 0; month < 12; month++)
        {
            var name = MonthName(month);
            var cells = new List<HeatCell>(31);
            for (var day = 0; day < 31; day++)
            {
                var value = grid[month, day];
                cells.Add(new HeatCell
                {
                    Value = value,
                    Tip = $"{name}{day + 1}日 · {InsightBuilder.FormatDuration(value)}"
                });

                if (value <= peakValue) continue;
                peakValue = value;
                peakLabel = $"{name}{day + 1}日";
            }

            rows.Add(new HeatRow { Label = name, Cells = cells });
        }

        return new HeatmapData
        {
            Rows = rows,
            ColumnLabels = Enumerable.Range(1, 31).Select(i => i.ToString()).ToArray(),
            Caption = ResourceStrings.InsightHeatmapByMonth,
            PeakText = $"{peakLabel} · {InsightBuilder.FormatDuration(peakValue)}",
            MaxValue = max
        };
    }

    #endregion

    /// <summary>会话 / 浏览记录统一成「本地时间片段 + 名称」，热力图和使用习惯共用</summary>
    private async Task<IReadOnlyList<(DateTime Start, DateTime End, string Name)>> GetSpansAsync(
        DateTime start, DateTime end, bool isWeb, CancellationToken ct)
    {
        if (isWeb)
        {
            var logs = await GetBrowseLogsCachedAsync(start, end, ct);
            return logs
                .Select(log =>
                {
                    var logStart = ToLocal(log.LogTime);
                    var name = log.Site?.Title ?? log.Site?.Domain ?? string.Empty;
                    return (Start: logStart, End: logStart.AddSeconds(Math.Max(1, log.Duration)), Name: name);
                })
                .Where(x => x.End > x.Start)
                .ToList();
        }

        var sessions = await GetSessionsCachedAsync(start, end, ct);
        return sessions
            .Select(session => (Start: ToLocal(session.StartTime), End: ToLocal(session.EndTime),
                Name: session.AppModel?.GetDisplayName() ?? string.Empty))
            .Where(x => x.End > x.Start)
            .ToList();
    }

    private async Task<List<AppSessionModel>> GetSessionsCachedAsync(DateTime start, DateTime end,
        CancellationToken ct)
    {
        if (_sessionCache != null && _sessionCacheStart == start && _sessionCacheEnd == end)
            return _sessionCache;

        var list = (await _dataService.GetAppSessionsAsync(start, end, ct)).ToList();
        _sessionCacheStart = start;
        _sessionCacheEnd = end;
        _sessionCache = list;
        return list;
    }

    private async Task<List<WebBrowseLogModel>> GetBrowseLogsCachedAsync(DateTime start, DateTime end,
        CancellationToken ct)
    {
        if (_browseCache != null && _browseCacheStart == start && _browseCacheEnd == end)
            return _browseCache;

        var list = (await _webDataService.GetBrowseLogListAsync(start, end, cancellationToken: ct)).ToList();
        _browseCacheStart = start;
        _browseCacheEnd = end;
        _browseCache = list;
        return list;
    }

    #endregion

    private static (DateTime Start, DateTime End) GetDateRange(DateTime date, int dataType)
    {
        return dataType switch
        {
            0 => (date.Date, date.Date.AddDays(1).AddTicks(-1)),
            1 => GetWeekRange(date),
            2 => (new DateTime(date.Year, date.Month, 1), new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month), 23, 59, 59)),
            3 => (new DateTime(date.Year, 1, 1), new DateTime(date.Year, 12, 31, 23, 59, 59)),
            _ => (date, date)
        };
    }

    private static (DateTime Start, DateTime End) GetWeekRange(DateTime date)
    {
        var dayOfWeek = (int)date.DayOfWeek;
        dayOfWeek = dayOfWeek == 0 ? 7 : dayOfWeek;
        var start = date.Date.AddDays(-(dayOfWeek - 1));
        var end = start.AddDays(6).Date.AddHours(23).AddMinutes(59).AddSeconds(59);
        return (start, end);
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalMinutes < 1)
            return $"{ts.Seconds}s";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }
}
