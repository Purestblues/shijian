using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Logging;
using ShiJian.Client.Models;
using ShiJian.Client.Models.Navigation;
using ShiJian.Client.Servicers;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Shared.Librarys;
using ShiJian.Client.Shared.Models.Entity;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.Views;

namespace ShiJian.Client.ViewModels;

/// <summary>
/// N17：概览页下钻全量应用/网站总时长排行页。
/// </summary>
public class UsageRankingPageViewModel : ModelBase
{
    private readonly IEntityData _entityData;
    private readonly IAppData _appDataService;
    private readonly IWebData _webDataService;
    private readonly INavigationDataService _navigationData;
    private readonly INavigationService _navigationService;

    private UsageRankingNavigationContext _context = new(
        DateTime.Now.Date,
        DateTime.Now.Date,
        ResourceStrings.Today,
        0,
        false,
        UsageRankingFilter.All);

    private List<EntityModel> _allRows = [];
    private List<ChartsDataModel> _items = [];
    private List<SelectItemModel> _filterOptions = [];
    private SelectItemModel? _selectedFilterOption;
    private string _searchInput = string.Empty;
    private string _periodLabel = string.Empty;
    private string _summaryText = string.Empty;
    private bool _hasEntityError;
    private string _entityErrorText = string.Empty;

    public UsageRankingPageViewModel(
        IEntityData entityData,
        IAppData appData,
        IWebData webData,
        INavigationDataService navigationData,
        INavigationService navigationService)
    {
        _entityData = entityData;
        _appDataService = appData;
        _webDataService = webData;
        _navigationData = navigationData;
        _navigationService = navigationService;

        GoBackCommand = AsyncRelayCommand.Create<object>(_ => _navigationService.GoBack()).DisposeWith(Disposables);
        RefreshCommand = AsyncRelayCommand.CreateFromTask<object>(_ => ExecuteAsync(LoadAsync)).DisposeWith(Disposables);
        ToDetailCommand = AsyncRelayCommand.CreateFromTask<object>(OnToDetailAsync).DisposeWith(Disposables);
        CategoryClickCommand = AsyncRelayCommand.Create<object>(OnCategoryClick).DisposeWith(Disposables);

        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.SearchInput)
            .Skip(1)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(Disposables);

        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.SelectedFilterOption)
            .Skip(1)
            .Subscribe(_ =>
            {
                OnPropertyChanged(nameof(PageTitle));
                ApplyFilter();
            })
            .DisposeWith(Disposables);
    }

    public ICommand GoBackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToDetailCommand { get; }
    public ICommand CategoryClickCommand { get; }

    public UsageRankingFilter CurrentFilter =>
        SelectedFilterOption?.Id switch
        {
            (int)UsageRankingFilter.Apps => UsageRankingFilter.Apps,
            (int)UsageRankingFilter.Sites => UsageRankingFilter.Sites,
            _ => UsageRankingFilter.All
        };

    public string PageTitle => CurrentFilter switch
    {
        UsageRankingFilter.Apps => ResourceStrings.UsageRankingApps,
        UsageRankingFilter.Sites => ResourceStrings.UsageRankingSites,
        _ => ResourceStrings.UsageRankingAll
    };

    public List<SelectItemModel> FilterOptions
    {
        get => _filterOptions;
        private set
        {
            _filterOptions = value;
            OnPropertyChanged();
        }
    }

    public SelectItemModel? SelectedFilterOption
    {
        get => _selectedFilterOption;
        set
        {
            if (Equals(_selectedFilterOption, value)) return;
            _selectedFilterOption = value;
            OnPropertyChanged();
        }
    }

    public string SearchInput
    {
        get => _searchInput;
        set
        {
            if (_searchInput == value) return;
            _searchInput = value;
            OnPropertyChanged();
        }
    }

    public string PeriodLabel
    {
        get => _periodLabel;
        private set
        {
            _periodLabel = value;
            OnPropertyChanged();
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set
        {
            _summaryText = value;
            OnPropertyChanged();
        }
    }

    public List<ChartsDataModel> Items
    {
        get => _items;
        private set
        {
            _items = value;
            OnPropertyChanged();
        }
    }

    public bool HasEntityError
    {
        get => _hasEntityError;
        private set
        {
            _hasEntityError = value;
            OnPropertyChanged();
        }
    }

    public string EntityErrorText
    {
        get => _entityErrorText;
        private set
        {
            _entityErrorText = value;
            OnPropertyChanged();
        }
    }

    public override Task OnNavigatedToAsync()
    {
        if (_navigationData.Data is UsageRankingNavigationContext ctx)
            _context = ctx;

        PeriodLabel = string.IsNullOrWhiteSpace(_context.PeriodTitle)
            ? FormatRange(_context.StartDate, _context.EndDate)
            : _context.PeriodTitle;

        var options = new List<SelectItemModel>
        {
            new() { Id = (int)UsageRankingFilter.All, Name = ResourceStrings.FilterAll },
            new() { Id = (int)UsageRankingFilter.Apps, Name = ResourceStrings.App }
        };
        if (!_context.IsWebBrowserMerged)
            options.Add(new SelectItemModel { Id = (int)UsageRankingFilter.Sites, Name = ResourceStrings.Website });

        FilterOptions = options;
        var initialId = (int)_context.InitialFilter;
        SelectedFilterOption = options.FirstOrDefault(o => o.Id == initialId) ?? options[0];
        OnPropertyChanged(nameof(PageTitle));

        return ExecuteAsync(LoadAsync);
    }

    public override Task RefreshAsync() => ExecuteAsync(LoadAsync);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        HasEntityError = false;
        EntityErrorText = string.Empty;

        try
        {
            var entityList = await _entityData.GetEntityListAsync(
                _context.StartDate,
                _context.EndDate,
                _context.IsWebBrowserMerged,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _allRows = entityList?.Rows ?? [];
            ApplyFilter();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HasEntityError = true;
            EntityErrorText = ChartDataMapper.FormatEntityLoadError(ex);
            Logger.Warn($"排行页实体加载失败：{ex.Message}");
        }
    }

    internal static List<EntityModel> FilterAndSortRows(
        IReadOnlyList<EntityModel>? rows,
        UsageRankingFilter filter,
        string? keyword)
    {
        if (rows == null || rows.Count == 0) return [];

        IEnumerable<EntityModel> query = filter switch
        {
            UsageRankingFilter.Apps => rows.Where(r => r.Kind != "site"),
            UsageRankingFilter.Sites => rows.Where(r => r.Kind == "site"),
            _ => rows
        };

        var trimmed = (keyword ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            query = query.Where(r =>
                (r.Name?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.CategoryName?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false) ||
                r.Members.Any(m => m.Name?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return query
            .OrderByDescending(r => r.Seconds)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ApplyFilter()
    {
        var filtered = FilterAndSortRows(_allRows, CurrentFilter, SearchInput);
        Items = ChartDataMapper.MapFromEntities(filtered, includeBadges: true);

        var totalSeconds = filtered.Sum(r => (long)r.Seconds);
        var countText = string.Format(ResourceStrings.RankingTotalCount, filtered.Count);
        SummaryText = totalSeconds > 0
            ? $"{countText} · {Time.ToString((int)Math.Min(int.MaxValue, totalSeconds))}"
            : countText;
    }

    private void OnCategoryClick(object obj)
    {
        if (obj is not ChartBadgeModel badge || badge.CategoryId <= 0) return;
        _navigationService.NavigateTo(nameof(CategorySummaryPage),
            CategorySummaryNavigationContext.Create(CategorySummaryKind.Unified, badge.CategoryId, badge.Name));
    }

    private async Task OnToDetailAsync(object obj)
    {
        if (obj is not ChartsDataModel { Data: EntityModel entity }) return;

        try
        {
            if (entity.Kind == "site" || entity.Key.StartsWith("site:", StringComparison.Ordinal))
            {
                var site = await _webDataService.GetWebSiteAsync(entity.EntityId);
                if (site != null)
                    _navigationService.NavigateTo(nameof(WebSiteDetailPage),
                        WebSiteDetailNavigationContext.Create(site, _context.PeriodTabIndex, _context.StartDate));
                return;
            }

            var app = await _appDataService.GetAppAsync(entity.EntityId);
            if (app != null)
                _navigationService.NavigateTo(nameof(DetailPage),
                    DetailNavigationContext.Create(app, _context.PeriodTabIndex, _context.StartDate));
        }
        catch (Exception ex)
        {
            Logger.Error("打开详情页失败", ex);
        }
    }

    private static string FormatRange(DateTime start, DateTime end) =>
        start.Date == end.Date
            ? start.ToString("yyyy/M/d")
            : $"{start:M/d} - {end:M/d}";

    public override void Dispose()
    {
        _allRows = [];
        Items = [];
        base.Dispose();
    }
}
