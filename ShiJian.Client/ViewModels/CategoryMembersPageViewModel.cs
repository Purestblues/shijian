using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Events;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models;
using ShiJian.Client.Models.CategoryAppList;
using ShiJian.Client.Servicers;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Shared.Models;
using ShiJian.Client.Shared.Models.Web;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.Views;
using CategoryModel = ShiJian.Client.Models.Category.CategoryModel;

namespace ShiJian.Client.ViewModels;

/// <summary>
/// N18：统一分类成员管理页（同一页面内通过 应用 | 网站 切换查看、添加、移除成员）
/// </summary>
public class CategoryMembersPageViewModel : ModelBase
{
    private readonly IAppData _appDataService;
    private readonly IWebData _webDataService;
    private readonly ICategorys _categoryService;
    private readonly INavigationDataService _navigationData;
    private readonly INavigationService _navigationService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IAppEventService _appEventService;
    private readonly IAppMergeService _appMergeService;

    private CategoryModel _category = new();
    private SelectItemModel _selectedTab;
    private string _mainSearchInput = string.Empty;
    private string _searchInput = string.Empty;
    private bool _chooseVisibility;

    private List<AppModel> _originalApps = [];
    private List<WebSiteModel> _originalSites = [];
    private List<AppModel> _apps = [];
    private ObservableCollection<WebSiteModel> _sites = new();
    private AppModel? _selectedApp;
    private WebSiteModel? _selectedSite;

    private List<ChooseAppModel> _allAppOptions = [];
    private List<ChooseAppModel> _appOptions = [];
    private List<CategoryWebSiteListPageModel.OptionModel> _allSiteOptions = [];
    private ObservableCollection<CategoryWebSiteListPageModel.OptionModel> _siteOptions = new();

    public CategoryMembersPageViewModel(
        IAppData appData,
        IWebData webData,
        ICategorys categorys,
        INavigationDataService navigationData,
        INavigationService navigationService,
        IAppUpdateService appUpdateService,
        IAppEventService appEventService,
        IAppMergeService appMergeService)
    {
        _appDataService = appData;
        _webDataService = webData;
        _categoryService = categorys;
        _navigationData = navigationData;
        _navigationService = navigationService;
        _appUpdateService = appUpdateService;
        _appEventService = appEventService;
        _appMergeService = appMergeService;

        TabOptions =
        [
            new SelectItemModel { Id = 0, Name = ResourceStrings.App },
            new SelectItemModel { Id = 1, Name = ResourceStrings.Website }
        ];
        _selectedTab = TabOptions[0];

        GoBackCommand = AsyncRelayCommand.Create<object>(_ => _navigationService.GoBack()).DisposeWith(Disposables);
        ShowChooseCommand = AsyncRelayCommand.CreateFromTask<object>(OnShowChooseAsync).DisposeWith(Disposables);
        ChooseCloseCommand = AsyncRelayCommand.Create<object>(_ => CloseChoose()).DisposeWith(Disposables);
        ChoosedCommand = AsyncRelayCommand.CreateFromTask<object>(_ => SaveChooseAsync()).DisposeWith(Disposables);
        RemoveAppCommand = AsyncRelayCommand.CreateFromTask<object>(OnRemoveAppAsync).DisposeWith(Disposables);
        RemoveSiteCommand = AsyncRelayCommand.CreateFromTask<object>(OnRemoveSiteAsync).DisposeWith(Disposables);
        GotoAppDetailCommand = AsyncRelayCommand.Create<object>(OnGotoAppDetail).DisposeWith(Disposables);
        GotoSiteDetailCommand = AsyncRelayCommand.Create<object>(OnGotoSiteDetail).DisposeWith(Disposables);

        RefreshOnChange(
            _appEventService.AppChanged.Where(e => e.ChangeType.HasFlag(AppChangeType.Category)),
            RefreshAsync);
        RefreshOnChange(
            _appEventService.WebSiteChanged.Where(e => e.ChangeType.HasFlag(AppChangeType.WebSiteCategory)),
            RefreshAsync);

        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.MainSearchInput)
            .Skip(1)
            .Subscribe(_ => ApplyMainFilter())
            .DisposeWith(Disposables);

        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.SearchInput)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(AvaloniaContextScheduler.Instance)
            .Subscribe(FilterChooser)
            .DisposeWith(Disposables);
    }

    public ICommand GoBackCommand { get; }
    public ICommand ShowChooseCommand { get; }
    public ICommand ChooseCloseCommand { get; }
    public ICommand ChoosedCommand { get; }
    public ICommand RemoveAppCommand { get; }
    public ICommand RemoveSiteCommand { get; }
    public ICommand GotoAppDetailCommand { get; }
    public ICommand GotoSiteDetailCommand { get; }

    public List<SelectItemModel> TabOptions { get; }

    public SelectItemModel SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Equals(_selectedTab, value)) return;
            _selectedTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAppTab));
            OnPropertyChanged(nameof(IsSiteTab));
            OnPropertyChanged(nameof(ChooserTitle));
            OnPropertyChanged(nameof(ChooserSearchPlaceholder));
        }
    }

    public bool IsAppTab => SelectedTab?.Id != 1;
    public bool IsSiteTab => SelectedTab?.Id == 1;

    public string ChooserTitle => IsAppTab
        ? Application.Current?.FindResource("SelectiveApplication") as string ?? string.Empty
        : Application.Current?.FindResource("SelectSite") as string ?? string.Empty;

    public string ChooserSearchPlaceholder => IsAppTab
        ? Application.Current?.FindResource("SearchApplication") as string ?? string.Empty
        : Application.Current?.FindResource("SearchSitePlaceholder") as string ?? string.Empty;

    public CategoryModel Category
    {
        get => _category;
        set
        {
            _category = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSystemCategory));
        }
    }

    public bool IsSystemCategory => Category?.Data?.IsSystem ?? false;

    public string MainSearchInput
    {
        get => _mainSearchInput;
        set
        {
            if (_mainSearchInput == value) return;
            _mainSearchInput = value;
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

    public bool ChooseVisibility
    {
        get => _chooseVisibility;
        set
        {
            if (_chooseVisibility == value) return;
            _chooseVisibility = value;
            OnPropertyChanged();
        }
    }

    public List<AppModel> Apps
    {
        get => _apps;
        set
        {
            _apps = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<WebSiteModel> Sites
    {
        get => _sites;
        set
        {
            _sites = value;
            OnPropertyChanged();
        }
    }

    public AppModel? SelectedApp
    {
        get => _selectedApp;
        set
        {
            _selectedApp = value;
            OnPropertyChanged();
        }
    }

    public WebSiteModel? SelectedSite
    {
        get => _selectedSite;
        set
        {
            _selectedSite = value;
            OnPropertyChanged();
        }
    }

    public List<ChooseAppModel> AppOptions
    {
        get => _appOptions;
        set
        {
            _appOptions = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<CategoryWebSiteListPageModel.OptionModel> SiteOptions
    {
        get => _siteOptions;
        set
        {
            _siteOptions = value;
            OnPropertyChanged();
        }
    }

    public override Task OnNavigatedToAsync()
    {
        if (_navigationData.Data is CategoryModel model)
            Category = model;

        return ExecuteAsync(LoadAllAsync);
    }

    public override Task RefreshAsync() => ExecuteAsync(LoadAllAsync);

    private async Task LoadAllAsync(CancellationToken cancellationToken)
    {
        if (Category?.Data == null) return;

        var appsTask = _appDataService.GetAppsByCategoryIDAsync(Category.Data.ID);
        var groupsTask = _appMergeService.GetGroupsAsync();
        var sitesTask = _webDataService.GetWebSitesAsync(Category.Data.ID);

        await Task.WhenAll(appsTask, groupsTask, sitesTask);
        cancellationToken.ThrowIfCancellationRequested();

        _originalApps = AppMergePlanner.FoldApps((await appsTask).ToList(), await groupsTask);
        _originalSites = (await sitesTask).ToList();

        ApplyMainFilter();
    }

    private void ApplyMainFilter()
    {
        var trimmed = (MainSearchInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Apps = _originalApps;
            Sites = new ObservableCollection<WebSiteModel>(_originalSites);
            return;
        }

        Apps = _originalApps.Where(m => CategoryEntityViewHelper.MatchesSearchWithAlias(
            trimmed,
            m.Alias,
            m.Description,
            m.Name)).ToList();

        Sites = new ObservableCollection<WebSiteModel>(
            CategoryWebSiteListPageViewModel.FilterWebSites(_originalSites, trimmed));
    }

    private async Task OnShowChooseAsync(object _)
    {
        if (Category?.Data == null) return;
        SearchInput = string.Empty;
        ChooseVisibility = true;

        if (IsAppTab)
        {
            var allApps = await _appDataService.GetAllAppsAsync();
            _allAppOptions = CategoryAppListPageViewModel.BuildCandidateOptions(allApps, Category.Data.ID);
            AppOptions = _allAppOptions;
        }
        else
        {
            var unset = await _webDataService.GetUnSetCategoryWebSitesAsync();
            _allSiteOptions = CategoryWebSiteListPageViewModel.BuildChooserOptions(unset, _originalSites, Category.Data.ID);
            SiteOptions = new ObservableCollection<CategoryWebSiteListPageModel.OptionModel>(_allSiteOptions);
        }
    }

    private void CloseChoose()
    {
        ChooseVisibility = false;
        SearchInput = string.Empty;
    }

    private void FilterChooser(string? keyword)
    {
        if (IsAppTab)
        {
            AppOptions = CategoryAppListPageViewModel.FilterCandidateOptions(_allAppOptions, keyword);
        }
        else
        {
            var filtered = CategoryWebSiteListPageViewModel.FilterChooserOptions(_allSiteOptions, keyword);
            SiteOptions = new ObservableCollection<CategoryWebSiteListPageModel.OptionModel>(filtered);
        }
    }

    private async Task SaveChooseAsync()
    {
        if (Category?.Data == null) return;
        ChooseVisibility = false;
        SearchInput = string.Empty;

        if (IsAppTab)
        {
            var addIds = new List<int>();
            var removeIds = new List<int>();
            foreach (var item in _allAppOptions)
            {
                if (item.IsChoosed)
                    addIds.Add(item.App.ID);
                else if (_originalApps.Any(a => a.ID == item.App.ID))
                    removeIds.Add(item.App.ID);
            }

            if (addIds.Count > 0)
                await _appUpdateService.UpdateCategoryBatchAsync(addIds, Category.Data.ID);
            foreach (var appId in removeIds)
                await _appUpdateService.ClearCategoryAsync(appId);
        }
        else
        {
            var addSiteIds = _allSiteOptions
                .Where(o => o.IsChecked && o.WebSite.CategoryID != Category.Data.ID)
                .Select(o => o.WebSite.ID)
                .ToArray();

            var removeSiteIds = _allSiteOptions
                .Where(o => !o.IsChecked && o.WebSite.CategoryID == Category.Data.ID)
                .Select(o => o.WebSite.ID)
                .ToArray();

            if (addSiteIds.Length > 0)
                await _webDataService.UpdateWebSitesCategoryAsync(addSiteIds, Category.Data.ID);

            if (removeSiteIds.Length > 0)
            {
                var categories = await _categoryService.GetCategoriesAsync();
                var uncategorizedId = categories.FirstOrDefault(c => c.IsSystem)?.ID ?? 0;
                await _webDataService.UpdateWebSitesCategoryAsync(removeSiteIds, uncategorizedId);
            }
        }

        await ExecuteAsync(LoadAllAsync);
    }

    private async Task OnRemoveAppAsync(object obj)
    {
        if (IsSystemCategory) return;
        var target = obj as AppModel ?? SelectedApp;
        if (target == null) return;

        await _appUpdateService.ClearCategoryAsync(target.ID);
        _originalApps = _originalApps.Where(m => m.ID != target.ID).ToList();
        if (ReferenceEquals(SelectedApp, target) || SelectedApp?.ID == target.ID)
            SelectedApp = null;
        ApplyMainFilter();
    }

    private async Task OnRemoveSiteAsync(object obj)
    {
        if (IsSystemCategory) return;
        var target = obj as WebSiteModel ?? SelectedSite;
        if (target == null) return;

        var categories = await _categoryService.GetCategoriesAsync();
        var systemCategory = categories.FirstOrDefault(c => c.IsSystem);
        var categoryId = systemCategory?.ID ?? 0;

        await _webDataService.UpdateWebSitesCategoryAsync([target.ID], categoryId);
        var removedSite = target with { CategoryID = categoryId, Category = null };
        _appEventService.PublishWebSiteChanged(removedSite, AppChangeType.WebSiteCategory);

        _originalSites.RemoveAll(m => m.ID == target.ID);
        if (ReferenceEquals(SelectedSite, target) || SelectedSite?.ID == target.ID)
            SelectedSite = null;
        ApplyMainFilter();
    }

    private void OnGotoAppDetail(object obj)
    {
        var app = obj as AppModel ?? SelectedApp;
        if (app != null)
            _navigationService.NavigateTo(nameof(DetailPage), app);
    }

    private void OnGotoSiteDetail(object obj)
    {
        var site = obj as WebSiteModel ?? SelectedSite;
        if (site != null)
            _navigationService.NavigateTo(nameof(WebSiteDetailPage), site);
    }

    public override void Dispose()
    {
        Apps = [];
        Sites = new ObservableCollection<WebSiteModel>();
        AppOptions = [];
        SiteOptions = new ObservableCollection<CategoryWebSiteListPageModel.OptionModel>();
        base.Dispose();
    }
}
