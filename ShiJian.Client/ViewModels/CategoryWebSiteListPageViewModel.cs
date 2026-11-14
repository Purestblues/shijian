using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Controls.Window;
using ShiJian.Client.Events;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models;
using ShiJian.Client.Shared.Librarys;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Shared.Models;
using ShiJian.Client.Shared.Models.Web;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.Views;

namespace ShiJian.Client.ViewModels;

public class CategoryWebSiteListPageViewModel : CategoryWebSiteListPageModel
{
    private readonly INavigationDataService _navigationData;
    private readonly INavigationService _navigationService;
    private readonly IToastService _toastService;
    private readonly ICategorys _categoryService;
    private readonly IWebData _webDataService;
    private readonly IAppEventService _appEventService;

    private List<OptionModel> _webSiteOptionsTemp = [];
    private List<WebSiteModel> _originalWebSiteList = [];
    private IDisposable? _searchSubscription;

    public CategoryWebSiteListPageViewModel(
        INavigationDataService navigationData,
        INavigationService navigationService,
        IToastService toastService,
        ICategorys categoryService,
        IWebData webData,
        IAppEventService appEventService)
    {
        _navigationData = navigationData;
        _navigationService = navigationService;
        _toastService = toastService;
        _categoryService = categoryService;
        _webDataService = webData;
        _appEventService = appEventService;
        Category = _navigationData.Data switch
        {
            CategoryModel unified => unified,
            WebSiteCategoryModel legacy => new CategoryModel
            {
                ID = legacy.ID,
                Name = legacy.Name,
                IconFile = legacy.IconFile,
                Color = legacy.Color,
                IsSystem = legacy.IsSystem,
                IsUrlMatch = legacy.IsUrlMatch,
                UrlPatterns = legacy.UrlPatterns
            },
            _ => new CategoryModel()
        };

        ShowChooseCommand = AsyncRelayCommand.CreateFromTask<object>(OnShowChooseAsync).DisposeWith(Disposables);
        ChoosedCommand = AsyncRelayCommand.Create<object>(OnChoosed).DisposeWith(Disposables);
        GotoDetailCommand = AsyncRelayCommand.Create<object>(OnGotoDetail).DisposeWith(Disposables);
        ChooseCloseCommand = AsyncRelayCommand.Create<object>(OnChooseClose).DisposeWith(Disposables);
        DelCommand = AsyncRelayCommand.CreateFromTask<object>(OnDelAsync).DisposeWith(Disposables);

        _searchSubscription = ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.SearchInput)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(AvaloniaContextScheduler.Instance)
            .Subscribe(DoSearch);
        _searchSubscription.DisposeWith(Disposables);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainSearchInput))
                ApplyMainFilter();
        };

        RefreshOnChange(
            _appEventService.WebSiteChanged
                .Where(e => e.ChangeType.HasFlag(AppChangeType.WebSiteCategory)),
            RefreshAsync);
    }

    public override Task OnNavigatedToAsync()
    {
        _ = ExecuteAsync(LoadDataCoreAsync);
        return Task.CompletedTask;
    }

    public override Task RefreshAsync() => ExecuteAsync(LoadDataCoreAsync);

    public ICommand ShowChooseCommand { get; }
    public ICommand ChoosedCommand { get; }
    public ICommand GotoDetailCommand { get; }
    public ICommand ChooseCloseCommand { get; }
    public ICommand DelCommand { get; }

    private async Task LoadDataCoreAsync(CancellationToken cancellationToken)
    {
        if (Category == null)
        {
            _toastService.Toast(ResourceStrings.InvalidParameter, ToastType.Error, IconTypes.Error);
            return;
        }

        var list = await _webDataService.GetWebSitesAsync(Category.ID);
        cancellationToken.ThrowIfCancellationRequested();
        _originalWebSiteList = list.ToList();
        ApplyMainFilter();
    }

    internal static List<WebSiteModel> FilterWebSites(IEnumerable<WebSiteModel>? source, string? keyword)
    {
        var items = (source ?? []).Where(s => s != null).ToList();
        var trimmed = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            return items;

        return items
            .Where(s => CategoryEntityViewHelper.MatchesSearchWithAlias(trimmed, s.Title, s.Domain, s.Alias))
            .ToList();
    }

    private void ApplyMainFilter()
    {
        CategoryWebSiteList = new ObservableCollection<WebSiteModel>(FilterWebSites(_originalWebSiteList, MainSearchInput));
    }

    private async Task LoadChooserDataAsync(CancellationToken cancellationToken = default)
    {
        var unsetList = await _webDataService.GetUnSetCategoryWebSitesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var optionList = BuildChooserOptions(unsetList, _originalWebSiteList, Category?.ID ?? 0);
        _webSiteOptionsTemp = optionList;

        WebSiteOptionList.Clear();
        foreach (var item in _webSiteOptionsTemp)
            WebSiteOptionList.Add(item);
    }

    internal static List<OptionModel> BuildChooserOptions(
        IEnumerable<WebSiteModel>? unsetList,
        IEnumerable<WebSiteModel>? currentCategoryList,
        int categoryId)
    {
        var combinedList = (unsetList ?? [])
            .Concat(currentCategoryList ?? [])
            .Where(s => s != null && (!string.IsNullOrWhiteSpace(s.Title) || !string.IsNullOrWhiteSpace(s.Domain)))
            .GroupBy(s => s.ID)
            .Select(g => g.First())
            .OrderByDescending(m => m.CategoryID == categoryId)
            .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => m.Domain, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var optionList = new List<OptionModel>(combinedList.Count);
        foreach (var site in combinedList)
        {
            optionList.Add(new OptionModel
            {
                IsChecked = site.CategoryID == categoryId,
                OptionValue = new SelectItemModel
                {
                    Name = FormatSiteOptionName(site),
                    Img = site.IconFile
                },
                WebSite = site
            });
        }
        return optionList;
    }

    private static string FormatSiteOptionName(WebSiteModel site)
    {
        var title = (site.Title ?? string.Empty).Trim();
        var domain = (site.Domain ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(title)) return domain;
        if (string.IsNullOrEmpty(domain) || string.Equals(title, domain, StringComparison.OrdinalIgnoreCase)) return title;
        return $"{title} - {domain}";
    }

    private async Task OnDelAsync(object obj)
    {
        if (SelectedItem != null)
        {
            var categories = await _categoryService.GetCategoriesAsync();
            var systemCategory = categories.FirstOrDefault(c => c.IsSystem);
            var categoryId = systemCategory?.ID ?? 0;

            await _webDataService.UpdateWebSitesCategoryAsync(new[] { SelectedItem.ID }, categoryId);
            
            var removedSite = SelectedItem with { CategoryID = categoryId, Category = ToWebSiteCategoryModel(systemCategory) };
            _appEventService.PublishWebSiteChanged(removedSite, AppChangeType.WebSiteCategory);
            
            _originalWebSiteList.RemoveAll(m => m.ID == SelectedItem.ID);
            CategoryWebSiteList.Remove(SelectedItem);
            if (CategoryWebSiteList.Count == 0) CategoryWebSiteList = new ObservableCollection<WebSiteModel>();

            await ExecuteAsync(LoadDataCoreAsync);
        }
    }

    private void OnChooseClose(object obj)
    {
        ChooseVisibility = false;
        SearchInput = "";
    }

    private void DoSearch(string? keyword)
    {
        var filtered = FilterChooserOptions(_webSiteOptionsTemp, keyword);
        SyncToList(filtered);
    }

    internal static List<OptionModel> FilterChooserOptions(List<OptionModel> source, string? keyword)
    {
        var trimmed = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            return source;

        return source
            .Where(m => CategoryEntityViewHelper.MatchesSearchWithAlias(
                trimmed,
                m.WebSite.Title,
                m.WebSite.Domain,
                m.WebSite.Alias))
            .ToList();
    }

    private void SyncToList(List<OptionModel> targetList)
    {
        var currentSet = new HashSet<OptionModel>(WebSiteOptionList);
        var targetSet = new HashSet<OptionModel>(targetList);

        var toRemove = currentSet.Except(targetSet).ToList();
        var toAdd = targetSet.Except(currentSet).ToList();

        foreach (var item in toRemove)
            WebSiteOptionList.Remove(item);

        var insertIndex = 0;
        foreach (var item in targetList)
        {
            if (toAdd.Contains(item))
            {
                WebSiteOptionList.Insert(Math.Min(insertIndex, WebSiteOptionList.Count), item);
            }
            insertIndex++;
        }
    }

    private void OnGotoDetail(object obj)
    {
        if (SelectedItem != null)
        {
            _navigationService.NavigateTo(nameof(WebSiteDetailPage), SelectedItem);
        }
    }

    private void OnChoosed(object obj)
    {
        ChooseVisibility = false;
        SearchInput = "";
        _ = UpdateCategoryAsync();
    }

    private async Task UpdateCategoryAsync()
    {
        var userCheckedIds = _webSiteOptionsTemp
            .Where(m => m.IsChecked)
            .Select(m => m.WebSite.ID)
            .ToList();

        var toRemove = _originalWebSiteList
            .Where(m => !userCheckedIds.Contains(m.ID))
            .Select(m => m.ID)
            .ToList();
        var toAdd = userCheckedIds
            .Where(id => !_originalWebSiteList.Any(s => s.ID == id))
            .ToList();

        foreach (var id in toRemove)
            _originalWebSiteList.RemoveAll(m => m.ID == id);

        var addSites = _webSiteOptionsTemp
            .Where(m => toAdd.Contains(m.WebSite.ID))
            .Select(m => m.WebSite)
            .ToList();
        foreach (var item in addSites)
            _originalWebSiteList.Add(item);

        MainSearchInput = string.Empty;
        ApplyMainFilter();

        if (toRemove.Count > 0)
        {
            var categories = await _categoryService.GetCategoriesAsync();
            var systemCategory = categories.FirstOrDefault(c => c.IsSystem);
            var removeCategoryId = systemCategory?.ID ?? 0;
            await _webDataService.UpdateWebSitesCategoryAsync(toRemove.ToArray(), removeCategoryId);
            
            var legacySystem = ToWebSiteCategoryModel(systemCategory);
            foreach (var siteId in toRemove)
            {
                var site = _webSiteOptionsTemp.FirstOrDefault(m => m.WebSite.ID == siteId)?.WebSite;
                if (site != null)
                    _appEventService.PublishWebSiteChanged(site with { CategoryID = removeCategoryId, Category = legacySystem }, AppChangeType.WebSiteCategory);
            }
        }

        if (toAdd.Count > 0 && Category != null)
        {
            await _webDataService.UpdateWebSitesCategoryAsync(toAdd.ToArray(), Category.ID);
            
            var legacyCategory = ToWebSiteCategoryModel(Category);
            foreach (var site in addSites)
                _appEventService.PublishWebSiteChanged(site with { CategoryID = Category.ID, Category = legacyCategory }, AppChangeType.WebSiteCategory);
        }
    }

    private static WebSiteCategoryModel? ToWebSiteCategoryModel(CategoryModel? category) =>
        category == null
            ? null
            : new WebSiteCategoryModel
            {
                ID = category.ID,
                Name = category.Name,
                IconFile = category.IconFile,
                Color = category.Color,
                IsSystem = category.IsSystem,
                IsUrlMatch = category.IsUrlMatch,
                UrlPatterns = category.UrlPatterns
            };

    private async Task OnShowChooseAsync(object obj)
    {
        ChooseVisibility = true;
        SearchInput = "";
        await ExecuteAsync(LoadChooserDataAsync);
    }
}
