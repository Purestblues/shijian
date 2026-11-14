using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ShiJian.Client.Events;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models;
using ShiJian.Client.Models.CategoryAppList;
using ShiJian.Client.Servicers;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Shared.Models;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.Views;
using CategoryModel = ShiJian.Client.Models.Category.CategoryModel;

namespace ShiJian.Client.ViewModels;

public class CategoryAppListPageViewModel : CategoryAppListPageModel
{
    private readonly IAppData _appDataService;
    private readonly INavigationDataService _navigationData;
    private readonly INavigationService _navigationService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IAppEventService _appEventService;
    private readonly IAppMergeService _appMergeService;
    private List<ChooseAppModel> _appList = [];

    public CategoryAppListPageViewModel(
        IAppData appData,
        INavigationDataService navigationData,
        INavigationService navigationService,
        IAppUpdateService appUpdateService,
        IAppEventService appEventService,
        IAppMergeService appMergeService)
    {
        _appDataService = appData;
        _navigationData = navigationData;
        _navigationService = navigationService;
        _appUpdateService = appUpdateService;
        _appEventService = appEventService;
        _appMergeService = appMergeService;

        ShowChooseCommand = AsyncRelayCommand.CreateFromTask<object>(OnShowChooseAsync).DisposeWith(Disposables);
        ChoosedCommand = AsyncRelayCommand.CreateFromTask<object>(OnChoosed).DisposeWith(Disposables);
        GotoDetailCommand = AsyncRelayCommand.Create<object>(OnGotoDetail).DisposeWith(Disposables);
        SearchCommand = AsyncRelayCommand.CreateFromTask<object>(OnSearchAsync).DisposeWith(Disposables);
        ChooseCloseCommand = AsyncRelayCommand.Create<object>(OnChooseClose).DisposeWith(Disposables);
        DelCommand = AsyncRelayCommand.CreateFromTask<object>(OnDel).DisposeWith(Disposables);

        RefreshOnChange(
            _appEventService.AppChanged
                .Where(e => e.ChangeType.HasFlag(AppChangeType.Category)),
            RefreshAsync);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainSearchInput))
                FilterMainData();
        };

        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.SearchInput)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(AvaloniaContextScheduler.Instance)
            .Subscribe(keyword => _ = OnSearchAsync(keyword))
            .DisposeWith(Disposables);
    }

    public ICommand ShowChooseCommand { get; }
    public ICommand ChoosedCommand { get; }
    public ICommand GotoDetailCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ChooseCloseCommand { get; }
    public ICommand DelCommand { get; }

    public override void Dispose()
    {
        Data = [];
        AppList = [];
        base.Dispose();
    }

    private async Task OnDel(object obj)
    {
        if (SelectedItem == null) return;

        var list = Data.ToList();
        list.Remove(SelectedItem);

        await _appUpdateService.ClearCategoryAsync(SelectedItem.ID);

        Data = list;
        MainPageOriginalData = list;
    }

    private void OnChooseClose(object obj)
    {
        ChooseVisibility = false;
        SearchInput = "";
        AppList = _appList;
    }

    private Task OnSearchAsync(object? obj)
    {
        var keyword = obj?.ToString() ?? SearchInput;
        return SearchAsync(keyword);
    }

    private void OnGotoDetail(object obj)
    {
        if (SelectedItem != null)
        {
            _navigationService.NavigateTo(nameof(DetailPage), SelectedItem);
        }
    }

    private async Task OnChoosed(object obj)
    {
        ChooseVisibility = false;
        SearchInput = "";
        var data = new List<AppModel>();
        var appIdsToAdd = new List<int>();
        var appIdsToRemove = new List<int>();

        foreach (var item in _appList)
        {
            if (item.IsChoosed)
            {
                appIdsToAdd.Add(item.App.ID);
            }
            else
            {
                var isHas = Data.Any(m => m.ID == item.App.ID);
                if (isHas)
                {
                    appIdsToRemove.Add(item.App.ID);
                }
            }
        }

        if (appIdsToAdd.Count > 0)
        {
            await _appUpdateService.UpdateCategoryBatchAsync(appIdsToAdd, Category.Data.ID);
        }

        if (appIdsToRemove.Count > 0)
        {
            foreach (var appId in appIdsToRemove)
            {
                await _appUpdateService.ClearCategoryAsync(appId);
            }
        }

        var addedApps = (await _appDataService.GetAppsByCategoryIDAsync(Category.Data.ID)).ToList();
        var foldedApps = AppMergePlanner.FoldApps(addedApps, await _appMergeService.GetGroupsAsync());
        Data = foldedApps;
        MainPageOriginalData = foldedApps;
        MainSearchInput = string.Empty;
    }

    private async Task OnShowChooseAsync(object obj)
    {
        ChooseVisibility = true;
        await LoadAppsAsync();
    }

    public override async Task OnNavigatedToAsync()
    {
        Category = _navigationData.Data as CategoryModel ?? new CategoryModel();
        var rawApps = Category.Data != null
            ? (await _appDataService.GetAppsByCategoryIDAsync(Category.Data.ID)).ToList()
            : [];
        var foldedApps = AppMergePlanner.FoldApps(rawApps, await _appMergeService.GetGroupsAsync());
        MainPageOriginalData = foldedApps;
        Data = MainPageOriginalData;
        MainSearchInput = string.Empty;
    }

    public override async Task RefreshAsync()
    {
        if (Category?.Data == null) return;
        var rawApps = (await _appDataService.GetAppsByCategoryIDAsync(Category.Data.ID)).ToList();
        MainPageOriginalData = AppMergePlanner.FoldApps(rawApps, await _appMergeService.GetGroupsAsync());
        // 保留当前搜索关键字：setter 会触发 FilterMainData，重新筛选保留过滤状态
        if (string.IsNullOrEmpty(MainSearchInput))
        {
            Data = MainPageOriginalData;
        }
        else
        {
            FilterMainData();
        }
    }

    private async Task LoadAppsAsync()
    {
        _appList = [];

        if (Category == null) return;
        var allApps = await _appDataService.GetAllAppsAsync();
        _appList = BuildCandidateOptions(allApps, Category.Data.ID);
        AppList = _appList;
    }

    internal static List<ChooseAppModel> BuildCandidateOptions(IEnumerable<AppModel>? allApps, int categoryId)
    {
        if (allApps == null) return [];

        var seenIds = new HashSet<int>();
        var seenDisplayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ChooseAppModel>();

        // 已选中或累计时长高的记录优先作为代表，消除空白项与同进程/同名重复卡片
        var ordered = allApps
            .Where(a => a != null && seenIds.Add(a.ID))
            .Where(a => a.CategoryID == categoryId || a.Category?.IsSystem == true)
            .OrderByDescending(a => a.CategoryID == categoryId)
            .ThenByDescending(a => a.TotalTime)
            .ThenBy(a => a.ID);

        foreach (var item in ordered)
        {
            var displayName = (item.GetDisplayName() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            var processName = (item.Name ?? string.Empty).Trim();
            var normalizedFile = (item.File ?? string.Empty).Trim();
            var dedupeKey = $"{displayName}\0{processName}\0{normalizedFile}";
            if (!seenDisplayKeys.Add(dedupeKey)) continue;

            var subTitle = FormatCandidateSubTitle(item, displayName);
            result.Add(new ChooseAppModel
            {
                App = item,
                IsChoosed = item.CategoryID == categoryId,
                Value =
                {
                    Name = displayName,
                    SubTitle = string.IsNullOrEmpty(subTitle) ? null : subTitle,
                    Img = item.IconFile
                }
            });
        }

        return result
            .OrderByDescending(m => m.IsChoosed)
            .ThenBy(m => m.Value.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static string FormatCandidateSubTitle(AppModel item, string displayName)
    {
        var parts = new List<string>(2);
        var processName = (item.Name ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(processName) &&
            !string.Equals(processName, displayName, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(processName);
        }

        var file = (item.File ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(file))
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(file));
            if (!string.IsNullOrEmpty(folder) &&
                !string.Equals(folder, processName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(folder, displayName, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(folder);
            }
            else if (parts.Count == 0)
            {
                parts.Add(Path.GetFileName(file));
            }
        }

        return string.Join(" · ", parts);
    }

    internal static List<ChooseAppModel> FilterCandidateOptions(List<ChooseAppModel> source, string? keyword)
    {
        var trimmed = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            return source;

        return source.Where(item => CategoryEntityViewHelper.MatchesSearchWithAlias(
            trimmed,
            item.App.Alias,
            item.App.Description,
            item.App.Name,
            item.Value.Name)).ToList();
    }

    private Task SearchAsync(string? keyword)
    {
        AppList = FilterCandidateOptions(_appList, keyword);
        return Task.CompletedTask;
    }

    private void FilterMainData()
    {
        var trimmed = (MainSearchInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Data = MainPageOriginalData;
            return;
        }

        Data = MainPageOriginalData.Where(m => CategoryEntityViewHelper.MatchesSearchWithAlias(
            trimmed,
            m.Alias,
            m.Description,
            m.Name)).ToList();
    }
}
