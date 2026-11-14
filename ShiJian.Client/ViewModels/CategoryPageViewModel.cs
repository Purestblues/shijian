using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows.Input;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Window;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Librarys.Image;
using ShiJian.Client.Logging;
using ShiJian.Client.Models;
using ShiJian.Client.Models.Category;
using ShiJian.Client.Models.Navigation;
using ShiJian.Client.Servicers;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Shared.Models.Entity;
using ShiJian.Client.Shared.Models.Web;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.Views;
using CategoryModel = ShiJian.Client.Models.Category.CategoryModel;

namespace ShiJian.Client.ViewModels;

public class CategoryPageViewModel : CategoryPageModel
{
    private readonly IDialogService _dialogService;
    private readonly IWebData _webDataService;
    private readonly IWebSiteData _webSiteDataService;
    private readonly IAppData _appDataService;
    private readonly IData _dataService;
    private readonly IEntityData _entityDataService;
    private readonly ICategorys _categoryService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IAppMergeService _appMergeService;
    private readonly IToastService _toastService;
    private readonly INavigationService _navigationService;
    private readonly IStateService _stateService;

    private List<string> _originalDirectories = new();
    private List<string> _originalUrlPatterns = new();
    private List<CategoryEntityRow> _allAppEntityRows = new();
    private List<CategoryEntityRow> _allWebEntityRows = new();
    private bool _entitiesLoaded;
    private bool _categoryStatsDirty = true;
    private bool _categoryStatsLoaded;
    private CategoryEntityRow? _tagTargetRow;

    public CategoryPageViewModel(
        ICategorys categorys,
        INavigationService navigationService,
        IAppData appData,
        IData data,
        IEntityData entityData,
        IWebData webData,
        IWebSiteData webSiteData,
        IAppUpdateService appUpdateService,
        IAppMergeService appMergeService,
        IDialogService dialogService,
        IToastService toastService,
        IStateService stateService)
    {
        _categoryService = categorys;
        _navigationService = navigationService;
        _appDataService = appData;
        _dataService = data;
        _entityDataService = entityData;
        _webDataService = webData;
        _webSiteDataService = webSiteData;
        _appUpdateService = appUpdateService;
        _appMergeService = appMergeService;
        _dialogService = dialogService;
        _toastService = toastService;
        _stateService = stateService;
        Data = new ObservableCollection<CategoryModel>();
        EditDirectories = new ObservableCollection<string>();
        EditUrlPatterns = new ObservableCollection<string>();

        GotoListCommand = AsyncRelayCommand.Create<object>(OnGotoList).DisposeWith(Disposables);
        GotoAppListCommand = AsyncRelayCommand.Create<object>(OnGotoAppList).DisposeWith(Disposables);
        GotoWebListCommand = AsyncRelayCommand.Create<object>(OnGotoWebList).DisposeWith(Disposables);
        EditCommand = AsyncRelayCommand.Create<object>(OnEdit).DisposeWith(Disposables);
        EditDoneCommand = AsyncRelayCommand.CreateFromTask<object>(OnEditDoneAsync).DisposeWith(Disposables);
        EditCloseCommand = AsyncRelayCommand.Create<object>(OnEditClose).DisposeWith(Disposables);
        DelCommand = AsyncRelayCommand.CreateFromTask<object>(OnDelAsync).DisposeWith(Disposables);
        RefreshCommand = AsyncRelayCommand.CreateFromTask<object>(OnRefreshAsync).DisposeWith(Disposables);
        AddDirectoryCommand = AsyncRelayCommand.CreateFromTask<object>(OnAddDirectoryAsync).DisposeWith(Disposables);
        DirectoriesCommand = AsyncRelayCommand.Create<object>(OnDirectoriesCommand).DisposeWith(Disposables);
        AddUrlPatternCommand = AsyncRelayCommand.CreateFromTask<object>(OnAddUrlPatternAsync).DisposeWith(Disposables);
        UrlPatternsCommand = AsyncRelayCommand.Create<object>(OnUrlPatternsCommand).DisposeWith(Disposables);
        RestoreSystemCategoryCommand = AsyncRelayCommand.CreateFromTask<object>(OnRestoreSystemCategoryAsync).DisposeWith(Disposables);
        ApplyDirectoryMatchCommand = AsyncRelayCommand.CreateFromTask<object>(OnApplyDirectoryMatchAsync).DisposeWith(Disposables);
        ApplyUrlMatchCommand = AsyncRelayCommand.CreateFromTask<object>(OnApplyUrlMatchAsync).DisposeWith(Disposables);
        ApplyAutoClassifyCommand = AsyncRelayCommand.CreateFromTask<object>(OnApplyAutoClassifyAsync).DisposeWith(Disposables);
        ToggleAppEmptyExpandedCommand = AsyncRelayCommand.Create<object>(_ => IsAppEmptyExpanded = !IsAppEmptyExpanded).DisposeWith(Disposables);
        GotoEntityDetailCommand = AsyncRelayCommand.CreateFromTask<object>(OnGotoEntityDetailAsync).DisposeWith(Disposables);
        AssignEntityCategoryCommand = AsyncRelayCommand.CreateFromTask<object>(OnAssignEntityCategoryAsync).DisposeWith(Disposables);

        // 排序不重拉数据，所以不走 WhenPropertyChanged 那个会 CancelAndResetLoadToken 的重载：
        // 否则加载还没回来时切换排序，会把正在跑的那次加载取消掉。
        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.SortMode)
            .Skip(1)
            .Subscribe(_ => ApplySort())
            .DisposeWith(Disposables);
        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.BrowseMode)
            .Skip(1)
            .Subscribe(_ => OnBrowseModeChanged())
            .DisposeWith(Disposables);
        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.EntitySearchInput)
            .Skip(1)
            .Subscribe(_ => ApplyEntityFilterAndSort(resetScroll: true))
            .DisposeWith(Disposables);
        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.EntityTypeMode)
            .Skip(1)
            .Subscribe(_ => ApplyEntityFilterAndSort(resetScroll: true))
            .DisposeWith(Disposables);
        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.EntityFilterMode)
            .Skip(1)
            .Subscribe(_ => ApplyEntityFilterAndSort(resetScroll: true))
            .DisposeWith(Disposables);
        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.EntitySortMode)
            .Skip(1)
            .Subscribe(_ => ApplyEntityFilterAndSort(resetScroll: true))
            .DisposeWith(Disposables);

        ChartColorHelper.ThemeChanged += OnThemeChanged;
    }

    /// <summary>主题切换后分类圆点要按新背景重算</summary>
    private void OnThemeChanged()
    {
        if (Data.Count == 0) return;
        _ = RefreshAsync();
    }

    private const string AppEntitiesStateKey = "CategoryPage:AppEntities";
    private const string WebEntitiesStateKey = "CategoryPage:WebEntities";

    public override async Task OnNavigatedToAsync()
    {
        var restored = TryRestoreState(_navigationService, _stateService);
        if (Data.Count > 0)
        {
            ComputeHasDirectoryMatchCategory();
            RebuildCategoryBuckets();
        }
        if (restored)
        {
            if (_stateService.Get<string, List<CategoryEntityRow>>(AppEntitiesStateKey) is { } appRows)
                _allAppEntityRows = appRows;
            if (_stateService.Get<string, List<CategoryEntityRow>>(WebEntitiesStateKey) is { } webRows)
                _allWebEntityRows = webRows;
            if (_allAppEntityRows.Count > 0 || _allWebEntityRows.Count > 0)
            {
                _entitiesLoaded = true;
                ApplyEntityFilterAndSort();
            }
            if (Data.Any(m => m.Duration > 0 || m.Count > 0))
            {
                _categoryStatsLoaded = true;
                _categoryStatsDirty = false;
            }
        }

        // 有缓存内容时保持现有卡片/列表直接可见，静默后台刷新，消除返回导航时的骨架闪烁
        var hasCachedContent = IsEntityBrowseMode
            ? _entitiesLoaded || FilteredEntities.Count > 0
            : Data.Count > 0;
        await ExecuteAsync(LoadDataCoreAsync, trackLoading: !hasCachedContent);
    }

    public override void OnNavigatedFrom()
    {
        SaveState(_stateService);
        if (_entitiesLoaded)
        {
            _stateService.Set(AppEntitiesStateKey, _allAppEntityRows);
            _stateService.Set(WebEntitiesStateKey, _allWebEntityRows);
        }
        base.OnNavigatedFrom();
    }

    public ICommand GotoListCommand { get; }
    public ICommand GotoAppListCommand { get; }
    public ICommand GotoWebListCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand EditDoneCommand { get; }
    public ICommand EditCloseCommand { get; }
    public ICommand DelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AddDirectoryCommand { get; }
    public ICommand DirectoriesCommand { get; }
    public ICommand AddUrlPatternCommand { get; }
    public ICommand UrlPatternsCommand { get; }
    public ICommand RestoreSystemCategoryCommand { get; }
    public ICommand ApplyDirectoryMatchCommand { get; }
    public ICommand ApplyUrlMatchCommand { get; }
    public ICommand ApplyAutoClassifyCommand { get; }
    public ICommand ToggleAppEmptyExpandedCommand { get; }
    public ICommand GotoEntityDetailCommand { get; }
    public ICommand AssignEntityCategoryCommand { get; }


    private bool _hasDirectoryMatchCategory;
    public bool HasDirectoryMatchCategory
    {
        get => _hasDirectoryMatchCategory;
        set
        {
            _hasDirectoryMatchCategory = value;
            OnPropertyChanged();
        }
    }

    private CategorySortMode CurrentSortMode => SortMode?.Id switch
    {
        1 => CategorySortMode.Name,
        2 => CategorySortMode.Count,
        _ => CategorySortMode.Duration
    };

    /// <summary>
    /// 切换排序只重排现有卡片：时长与数量在加载时已经全量算好，不需要再发请求。
    /// 重排走 <c>ObservableCollection.Move</c>——容器不重建，选中项与滚动位置都保留。
    /// </summary>
    private void ApplySort()
    {
        if (IsRestoringState) return;

        if (Data.Count > 0)
            CategoryCardStats.ReorderInPlace(Data,
                CategoryCardStats.Sort(Data.ToList(), CurrentSortMode, m => m.Data?.Name));

        RebuildCategoryBuckets();
    }

    /// <summary>单点重建「活跃 / 空分类」分组（载入、排序、新建、编辑、删除、恢复、快速打标全部复用它）</summary>
    private void RebuildCategoryBuckets()
    {
        var (activeApps, emptyApps) = CategoryCardStats.SplitEmpty(Data);
        CategoryCardStats.SyncCollection(ActiveAppCategories, activeApps);
        CategoryCardStats.SyncCollection(EmptyAppCategories, emptyApps);

        NotifyCategoryBucketStates();
    }

    private Task OnRefreshAsync(object _)
    {
        _entitiesLoaded = false;
        _categoryStatsDirty = true;
        return ExecuteAsync(LoadDataCoreAsync);
    }

    public override Task RefreshAsync()
    {
        _entitiesLoaded = false;
        _categoryStatsDirty = true;
        return ExecuteAsync(LoadDataCoreAsync);
    }

    private async Task OnApplyDirectoryMatchAsync(object _)
    {
        try
        {
            var count = await _categoryService.ApplyDirectoryMatchAsync();
            _toastService.Success(string.Format(ResourceStrings.ApplyDirectoryMatchSuccess, count));
            await LoadDataCoreAsync(default);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    private async Task OnApplyUrlMatchAsync(object _)
    {
        try
        {
            var count = await _webDataService.ApplyUrlMatchAsync();
            _toastService.Success(string.Format(ResourceStrings.ApplyUrlMatchSuccess, count));
            await LoadDataCoreAsync(default);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    private async Task OnApplyAutoClassifyAsync(object _)
    {
        try
        {
            var count = await _categoryService.ApplyAutoClassifyAsync();
            _toastService.Success(string.Format(ResourceStrings.AutoClassifySuccess, count));
            _entitiesLoaded = false;
            _categoryStatsDirty = true;
            await LoadDataCoreAsync(default);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    private async Task OnRestoreSystemCategoryAsync(object arg)
    {
        try
        {
            if (arg is not CategoryModel selected || selected.Data == null || !selected.Data.IsSystem)
                return;

            var restored = await _categoryService.RestoreSystemCategoryAsync(selected.Data.ID);

            var editItemIndex = Data.IndexOf(selected);
            if (editItemIndex == -1)
                return;

            var originalCount = selected.Count;
            Data[editItemIndex] = new CategoryModel
            {
                Count = originalCount,
                Duration = selected.Duration,
                Ratio = selected.Ratio,
                Data = restored
            };
            ApplySort();
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
        }
    }

    private Task OnDelAsync(object obj) => DelCategoryAsync(obj as CategoryModel);

    private void OnEditClose(object obj)
    {
        EditVisibility = false;
        ClearEditFields();
    }

    private void ClearEditFields()
    {
        EditName = "";
        EditIconFile = CategoryIconCatalog.DefaultIconKey;
        EditColor = "#00FFAB";
        EditIsDirectoryMatch = false;
        EditIsUrlMatch = false;
        EditDirectories.Clear();
        EditUrlPatterns.Clear();
        IsEditError = false;
        EditErrorText = "";
    }

    private Task OnEditDoneAsync(object obj) => EditCategoryActionAsync();

    private async Task OnAddDirectoryAsync(object obj)
    {
        try
        {
            var path = await _dialogService.ShowFolderPickerAsync(ResourceStrings.SelectDirectory);
            if (string.IsNullOrEmpty(path)) return;

            if (EditDirectories.Contains(path))
            {
                _toastService.Toast(ResourceStrings.DirectoryExists, ToastType.Warning);
                return;
            }

            EditDirectories.Add(path);
            _toastService.Success(ResourceStrings.Added);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
        }
    }

    private void OnDirectoriesCommand(object obj)
    {
        try
        {
            var action = obj?.ToString();
            if (string.IsNullOrEmpty(action) || EditSelectedDirectory == null)
                return;

            switch (action.ToLower())
            {
                case "remove":
                    if (EditDirectories.Contains(EditSelectedDirectory))
                    {
                        EditDirectories.Remove(EditSelectedDirectory);
                        EditSelectedDirectory = null;
                    }
                    break;
                default:
                    Logger.Warn($"Unknown directory command: {action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(ResourceStrings.OperationFailedRetry);
        }
    }

    private async Task OnAddUrlPatternAsync(object obj)
    {
        try
        {
            var pattern = await _dialogService.ShowInputModalAsync(ResourceStrings.AddUrlPattern, ResourceStrings.UrlPatternPlaceholder);
            if (string.IsNullOrEmpty(pattern)) return;

            if (pattern.Length > 256)
            {
                _toastService.Toast(ResourceStrings.UrlPatternTooLong, ToastType.Warning);
                return;
            }

            if (EditUrlPatterns.Count >= 200)
            {
                _toastService.Toast(ResourceStrings.UrlPatternLimitReached, ToastType.Warning);
                return;
            }

            if (EditUrlPatterns.Contains(pattern))
            {
                _toastService.Toast(ResourceStrings.UrlPatternExists, ToastType.Warning);
                return;
            }

            EditUrlPatterns.Add(pattern);
            _toastService.Success(ResourceStrings.Added);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
        }
    }

    private void OnUrlPatternsCommand(object obj)
    {
        try
        {
            var action = obj?.ToString();
            if (string.IsNullOrEmpty(action) || EditSelectedUrlPattern == null)
                return;

            switch (action.ToLower())
            {
                case "remove":
                    if (EditUrlPatterns.Contains(EditSelectedUrlPattern))
                    {
                        EditUrlPatterns.Remove(EditSelectedUrlPattern);
                        EditSelectedUrlPattern = null;
                    }
                    break;
                default:
                    Logger.Warn($"Unknown URL pattern command: {action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Toast(ResourceStrings.OperationFailedRetry);
        }
    }

    private bool IsEditVerify()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(EditName))
            {
                IsEditError = true;
                EditErrorText = ResourceStrings.CategoryCannotEmpty;
                return false;
            }

            if (string.IsNullOrWhiteSpace(EditIconFile))
            {
                _toastService.Toast(ResourceStrings.SelectCategoryIcon, ToastType.Error, IconTypes.ImportantBadge12);
                return false;
            }

            if (string.IsNullOrWhiteSpace(EditColor))
            {
                _toastService.Toast(ResourceStrings.CategoryColor, ToastType.Error, IconTypes.ImportantBadge12);
                return false;
            }

            if (!EditIconFile.StartsWith("avares://")
                && !CategoryIconCatalog.IsSvgIcon(EditIconFile)
                && File.Exists(EditIconFile))
            {
                var fileInfo = new FileInfo(EditIconFile);
                if (fileInfo.Length > 1000000)
                {
                    _toastService.Toast(ResourceStrings.IconFileCannotExceed, ToastType.Error, IconTypes.ImportantBadge12);
                    return false;
                }
            }

            IsEditError = false;
            EditErrorText = "";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            return false;
        }
    }

    private void ComputeHasDirectoryMatchCategory()
    {
        HasDirectoryMatchCategory = Data.Any(m => m.Data.IsDirectoryMatch);
    }

    private static string FormatMatchTip(bool isDirectoryMatch, bool isUrlMatch) => (isDirectoryMatch, isUrlMatch) switch
    {
        (true, true) => $"{ResourceStrings.DirectoryMatch} · {ResourceStrings.UrlMatch}",
        (true, false) => ResourceStrings.DirectoryMatch,
        (false, true) => ResourceStrings.UrlMatch,
        _ => string.Empty
    };

    private async Task EditCategoryActionAsync()
    {
        try
        {
            if (!IsEditVerify()) return;

            var directoriesStr = JsonSerializer.Serialize(EditDirectories.ToList(), ClientJsonContext.Default.ListString);
            var urlPatternsStr = JsonSerializer.Serialize(EditUrlPatterns.ToList(), ClientJsonContext.Default.ListString);

            if (IsCreate)
            {
                if (Data.Any(m => EditName.Equals(m.Data.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _toastService.Toast(ResourceStrings.CategoryNameExists, ToastType.Error, IconTypes.ImportantBadge12);
                    return;
                }

                var res = await _categoryService.CreateAsync(new Shared.Models.CategoryModel
                {
                    Name = EditName,
                    IconFile = EditIconFile,
                    Color = EditColor,
                    IsDirectoryMatch = EditIsDirectoryMatch,
                    Directories = directoriesStr,
                    IsUrlMatch = EditIsUrlMatch,
                    UrlPatterns = urlPatternsStr
                });

                if (res == null)
                {
                    _toastService.Toast(ResourceStrings.CreationFailed, ToastType.Error, IconTypes.ImportantBadge12);
                    return;
                }

                Data.Add(new CategoryModel
                {
                    Data = res,
                    Count = 0,
                    IsMatchFlag = res.IsDirectoryMatch || res.IsUrlMatch,
                    MatchTip = FormatMatchTip(res.IsDirectoryMatch, res.IsUrlMatch)
                });
                _toastService.Success(ResourceStrings.CreationCompleted);
            }
            else
            {
                if (SelectedAppCategoryItem?.Data == null)
                {
                    _toastService.Error(ResourceStrings.SelectedItemNotFound);
                    return;
                }

                if (Data.Any(m => EditName.Equals(m.Data.Name, StringComparison.OrdinalIgnoreCase) &&
                                  m.Data.ID != SelectedAppCategoryItem.Data.ID))
                {
                    _toastService.Toast(ResourceStrings.CategoryNameExists, ToastType.Error, IconTypes.ImportantBadge12);
                    return;
                }

                var category = await _categoryService.GetCategoryAsync(SelectedAppCategoryItem.Data.ID);
                if (category == null)
                {
                    _toastService.Error(ResourceStrings.CategoryNotFound);
                    return;
                }

                category = category with
                {
                    Name = EditName,
                    IconFile = EditIconFile,
                    Color = EditColor,
                    IsDirectoryMatch = EditIsDirectoryMatch,
                    Directories = directoriesStr,
                    IsUrlMatch = EditIsUrlMatch,
                    UrlPatterns = urlPatternsStr
                };

                await _categoryService.UpdateAsync(category);

                var item = Data.FirstOrDefault(m => m.Data.ID == SelectedAppCategoryItem.Data.ID);
                if (item != null)
                {
                    var editItemIndex = Data.IndexOf(item);
                    if (editItemIndex != -1)
                    {
                        Data[editItemIndex] = new CategoryModel
                        {
                            Count = item.Count,
                            Duration = item.Duration,
                            Ratio = item.Ratio,
                            IsMatchFlag = EditIsDirectoryMatch || EditIsUrlMatch,
                            MatchTip = FormatMatchTip(EditIsDirectoryMatch, EditIsUrlMatch),
                            Data = category
                        };
                    }
                }

                _toastService.Success(ResourceStrings.Updated);
            }

            var matchedAny = false;
            if (EditIsDirectoryMatch && EditDirectories.Count > 0)
            {
                try
                {
                    var newDirectories = EditDirectories
                        .Where(d => !_originalDirectories.Contains(d))
                        .ToArray();
                    if (newDirectories.Length > 0)
                    {
                        var matchCount = await _categoryService.ApplyDirectoryMatchAsync(newDirectories);
                        if (matchCount > 0)
                        {
                            matchedAny = true;
                            _toastService.Success(string.Format(ResourceStrings.ApplyDirectoryMatchSuccess, matchCount));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex.Message, ex);
                }
            }

            if (EditIsUrlMatch && EditUrlPatterns.Count > 0)
            {
                try
                {
                    var newPatterns = EditUrlPatterns
                        .Where(p => !_originalUrlPatterns.Contains(p))
                        .ToArray();
                    if (newPatterns.Length > 0)
                    {
                        var matchCount = await _webDataService.ApplyUrlMatchAsync(newPatterns);
                        if (matchCount > 0)
                        {
                            matchedAny = true;
                            _toastService.Success(string.Format(ResourceStrings.ApplyUrlMatchSuccess, matchCount));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex.Message, ex);
                }
            }

            if (matchedAny)
                await LoadDataCoreAsync(default);

            ComputeHasDirectoryMatchCategory();
            _entitiesLoaded = false;
            _categoryStatsDirty = true;
            ApplySort();
            EditVisibility = false;
            SelectedAppCategoryItem = null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    private async Task DelCategoryAsync(CategoryModel? target = null)
    {
        var selected = target ?? SelectedAppCategoryItem;
        if (selected?.Data == null) return;

        try
        {
            var category = await _categoryService.GetCategoryAsync(selected.Data.ID);
            if (category == null || category.IsSystem)
                return;

            var message = selected.Count > 0
                ? string.Format(ResourceStrings.CategoryHasEntities, selected.Count)
                : ResourceStrings.WantDeleteCategory;

            var isConfirm = await _dialogService.ShowConfirmDialogAsync(
                ResourceStrings.DeleteCategory,
                message);

            if (!isConfirm) return;

            await _categoryService.DeleteAsync(category);

            if (selected.Count > 0)
            {
                var sysCategory = Data.FirstOrDefault(m => m.Data.IsSystem);
                if (sysCategory != null)
                {
                    var index = Data.IndexOf(sysCategory);
                    if (index != -1)
                    {
                        Data[index] = new CategoryModel
                        {
                            Data = sysCategory.Data,
                            Count = sysCategory.Count + selected.Count,
                            Duration = sysCategory.Duration + selected.Duration,
                            Ratio = sysCategory.Ratio + selected.Ratio
                        };
                    }
                }
            }

            Data.Remove(selected);
            if (ReferenceEquals(SelectedAppCategoryItem, selected))
                SelectedAppCategoryItem = null;
            _entitiesLoaded = false;
            ApplySort();

            _toastService.Success(ResourceStrings.CategoryDeleted);
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    private void OnEdit(object obj)
    {
        EditVisibility = true;
        IsCreate = obj == null;
        IsSysCategory = false;
        EditDirectories.Clear();
        EditSelectedDirectory = null;
        EditUrlPatterns.Clear();
        EditSelectedUrlPattern = null;
        _originalDirectories.Clear();
        _originalUrlPatterns.Clear();

        if (obj is CategoryModel category && category.Data != null)
        {
            SelectedAppCategoryItem = category;
            IsSysCategory = category.Data.IsSystem;
            EditName = category.Data.Name;
            EditIconFile = CategoryIconCatalog.IsSvgIcon(category.Data.IconFile)
                || CategoryIconCatalog.ResolveKey(category.Data.IconFile) == null && !string.IsNullOrWhiteSpace(category.Data.IconFile)
                ? category.Data.IconFile!
                : CategoryIconCatalog.ResolveKey(category.Data.IconFile) ?? CategoryIconCatalog.DefaultIconKey;
            EditColor = string.IsNullOrWhiteSpace(category.Data.Color) ? "#00FFAB" : category.Data.Color;
            EditIsDirectoryMatch = category.Data.IsDirectoryMatch;
            EditIsUrlMatch = category.Data.IsUrlMatch;

            if (!string.IsNullOrWhiteSpace(category.Data.Directories))
            {
                try
                {
                    var directories = JsonSerializer.Deserialize<List<string>>(category.Data.Directories, ClientJsonContext.Default.ListString);
                    if (directories != null)
                    {
                        foreach (var dir in directories)
                        {
                            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                            {
                                EditDirectories.Add(dir);
                                _originalDirectories.Add(dir);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to load directories: {ex.Message}", ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(category.Data.UrlPatterns))
            {
                try
                {
                    var patterns = JsonSerializer.Deserialize<List<string>>(category.Data.UrlPatterns, ClientJsonContext.Default.ListString);
                    if (patterns != null)
                    {
                        foreach (var pattern in patterns)
                        {
                            if (!string.IsNullOrWhiteSpace(pattern))
                            {
                                EditUrlPatterns.Add(pattern);
                                _originalUrlPatterns.Add(pattern);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to load URL patterns: {ex.Message}", ex);
                }
            }
        }
        else
        {
            EditName = "";
            EditIconFile = CategoryIconCatalog.DefaultIconKey;
            EditColor = "#00FFAB";
            EditIsDirectoryMatch = false;
            EditIsUrlMatch = false;
        }
    }

    private void OnGotoList(object obj)
    {
        try
        {
            var selected = obj as CategoryModel ?? SelectedAppCategoryItem;
            if (selected?.Data != null)
            {
                _navigationService.NavigateTo(nameof(CategorySummaryPage),
                    CategorySummaryNavigationContext.Create(CategorySummaryKind.Unified, selected.Data.ID, selected.Data.Name));
                SelectedAppCategoryItem = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
        }
    }

    private void OnGotoAppList(object obj)
    {
        try
        {
            var selected = obj as CategoryModel ?? SelectedAppCategoryItem;
            if (selected != null)
            {
                _navigationService.NavigateTo(nameof(CategoryAppListPage), selected);
                SelectedAppCategoryItem = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
        }
    }

    private void OnGotoWebList(object obj)
    {
        try
        {
            var selected = obj as CategoryModel ?? SelectedAppCategoryItem;
            if (selected?.Data != null)
            {
                _navigationService.NavigateTo(nameof(CategoryWebSiteListPage), selected.Data);
                SelectedAppCategoryItem = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, ex);
        }
    }

    private void OnBrowseModeChanged()
    {
        if (IsRestoringState) return;

        if (IsEntityBrowseMode)
        {
            if (_entitiesLoaded)
                ApplyEntityFilterAndSort();
            else
                _ = ExecuteAsync(LoadDataCoreAsync);
        }
        else if (_categoryStatsDirty || !_categoryStatsLoaded)
        {
            _ = ExecuteAsync(LoadDataCoreAsync);
        }
    }

    private async Task LoadDataCoreAsync(CancellationToken cancellationToken)
    {
        if (IsEntityBrowseMode)
        {
            await LoadEntitiesAsync(cancellationToken);
            if (!_categoryStatsLoaded)
                _categoryStatsDirty = true;
            return;
        }

        await LoadCategoryCardsAsync(cancellationToken);
        _categoryStatsDirty = false;
        _categoryStatsLoaded = true;
    }

    /// <summary>
    /// 统一分类卡片：一次并发获取全部分类、每个分类的应用数与网站数、及今日实体时长，
    /// 合并聚合在同一张卡片上。
    /// </summary>
    private async Task LoadCategoryCardsAsync(CancellationToken cancellationToken)
    {
        var today = DateTime.Now.Date;

        var categories = await _categoryService.GetCategoriesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var appCountTasks = categories.Select(m => _appDataService.GetAppCountByCategoryIDAsync(m.ID)).ToArray();
        var siteCountTasks = categories.Select(m => _webDataService.GetWebSitesCountAsync(m.ID)).ToArray();
        var entityTask = _entityDataService.GetEntityListAsync(today, today, false, cancellationToken);

        var appCounts = await Task.WhenAll(appCountTasks);
        var siteCounts = await Task.WhenAll(siteCountTasks);
        var entity = await entityTask;
        cancellationToken.ThrowIfCancellationRequested();

        var durationByCategory = SumEntityByCategory(entity);

        var cards = new List<CategoryModel>(categories.Count);
        for (var i = 0; i < categories.Count; i++)
        {
            var cat = categories[i];
            cards.Add(new CategoryModel
            {
                Data = cat,
                Count = appCounts[i] + siteCounts[i],
                IsMatchFlag = cat.IsDirectoryMatch || cat.IsUrlMatch,
                MatchTip = FormatMatchTip(cat.IsDirectoryMatch, cat.IsUrlMatch)
            });
        }

        CategoryCardStats.Assign(cards, m => m.Data.ID, durationByCategory);
        var sorted = CategoryCardStats.Sort(cards, CurrentSortMode, m => m.Data.Name);

        // 替换整集合引用而不原地 Clear，避免影响 StateService 持有的返回导航缓存实例
        Data = new ObservableCollection<CategoryModel>(sorted);
        OnPropertyChanged(nameof(HasAnyAppCategories));

        ComputeHasDirectoryMatchCategory();
        RebuildCategoryBuckets();
    }

    private async Task LoadEntitiesAsync(CancellationToken cancellationToken)
    {
        var today = DateTime.Now.Date;
        var appsTask = _appDataService.GetAllAppsAsync(cancellationToken);
        var sitesTask = _webSiteDataService.GetAllWebSitesAsync(cancellationToken);
        var catsTask = _categoryService.GetCategoriesAsync(cancellationToken);
        var groupsTask = _appMergeService.GetGroupsAsync(cancellationToken: cancellationToken);
        var entityTask = _entityDataService.GetEntityListAsync(today, today, false, cancellationToken);

        await Task.WhenAll(appsTask, sitesTask, catsTask, groupsTask, entityTask);
        cancellationToken.ThrowIfCancellationRequested();

        var categories = catsTask.Result;
        if (Data.Count == 0 && categories.Count > 0)
        {
            foreach (var c in categories)
                Data.Add(new CategoryModel { Data = c });
            ComputeHasDirectoryMatchCategory();
            RebuildCategoryBuckets();
            // Bare category shells have no duration stats yet; mark stats stale so switching
            // back to category view loads real metrics instead of collapsing into empty bucket.
            _categoryStatsLoaded = false;
            _categoryStatsDirty = true;
        }

        _allAppEntityRows = CategoryEntityViewHelper.BuildAppRows(
            appsTask.Result,
            categories,
            groupsTask.Result,
            entityTask.Result,
            ResourceStrings.SystemCategoryUncategorized);
        _allWebEntityRows = CategoryEntityViewHelper.BuildSiteRows(
            sitesTask.Result,
            categories,
            groupsTask.Result,
            entityTask.Result,
            ResourceStrings.SystemCategoryUncategorized);
        _entitiesLoaded = true;
        ApplyEntityFilterAndSort();
    }

    private IReadOnlyList<CategoryEntityRow> CurrentSourceEntities => EntityTypeMode?.Id switch
    {
        1 => _allAppEntityRows,
        2 => _allWebEntityRows,
        _ => _allAppEntityRows.Concat(_allWebEntityRows).ToList()
    };

    public event Action? EntityScrollResetRequested;

    private void ApplyEntityFilterAndSort(bool resetScroll = false)
    {
        var source = CurrentSourceEntities;
        TotalEntityCount = source.Count;
        UncategorizedEntityCount = source.Count(r => r.IsUncategorized);

        var filter = EntityFilterMode?.Id == 1
            ? Librarys.EntityFilterMode.Uncategorized
            : Librarys.EntityFilterMode.All;
        var sort = EntitySortMode?.Id switch
        {
            1 => Librarys.EntitySortMode.TotalDuration,
            2 => Librarys.EntitySortMode.Name,
            3 => Librarys.EntitySortMode.UncategorizedFirst,
            _ => Librarys.EntitySortMode.TodayDuration
        };

        var desired = CategoryEntityViewHelper.FilterAndSort(source, EntitySearchInput, filter, sort);
        CategoryCardStats.SyncCollection(FilteredEntities, desired);
        NotifyEntityListState();
        if (resetScroll)
            EntityScrollResetRequested?.Invoke();
    }

    /// <summary>打开分类选择浮层前刷新统一共享分类清单</summary>
    public void PrepareTagPicker(CategoryEntityRow row)
    {
        _tagTargetRow = row;
        TagPickerOptions.Clear();

        foreach (var cat in Data)
        {
            if (cat.Data == null) continue;
            TagPickerOptions.Add(new CategoryTagOption
            {
                Id = cat.Data.ID,
                Name = cat.Data.Name ?? ResourceStrings.SystemCategoryUncategorized,
                Color = string.IsNullOrWhiteSpace(cat.Data.Color) ? "#8A8A8A" : cat.Data.Color!,
                Icon = cat.Data.IconFile,
                IsSystem = cat.Data.IsSystem,
                IsSelected = cat.Data.ID == row.CategoryId
            });
        }
    }

    private Task OnAssignEntityCategoryAsync(object param) =>
        param is CategoryTagOption option ? AssignEntityCategoryAsync(option) : Task.CompletedTask;

    /// <summary>快速切换实体（应用或网站）的所属分类并立即同步当前行状态</summary>
    public async Task AssignEntityCategoryAsync(CategoryTagOption option)
    {
        if (option == null || _tagTargetRow == null) return;
        var row = _tagTargetRow;
        if (row.CategoryId == option.Id) return;

        try
        {
            if (row.IsSite)
                await _webDataService.UpdateWebSitesCategoryAsync([row.PrimaryId], option.Id);
            else
                await _appUpdateService.UpdateCategoryAsync(row.PrimaryId, option.Id);

            row.CategoryId = option.Id;
            row.CategoryName = option.Name;
            row.CategoryColor = option.Color;
            row.IsUncategorized = option.IsSystem;

            _categoryStatsDirty = true;
            ApplyEntityFilterAndSort();
            _toastService.Success(ResourceStrings.EntityTagChanged);
        }
        catch (Exception ex)
        {
            Logger.Error($"快速修改分类失败：{ex.Message}", ex);
            _toastService.Error(ResourceStrings.OperationFailedRetry);
        }
    }

    private async Task OnGotoEntityDetailAsync(object obj)
    {
        var row = obj as CategoryEntityRow ?? SelectedEntityItem;
        if (row == null) return;

        try
        {
            if (row.IsSite)
            {
                var site = await _webSiteDataService.GetWebSiteAsync(row.PrimaryId);
                if (site != null)
                    _navigationService.NavigateTo(nameof(WebSiteDetailPage),
                        WebSiteDetailNavigationContext.Create(site, 0, DateTime.Today));
                return;
            }

            var app = await _appDataService.GetAppAsync(row.PrimaryId);
            if (app != null)
                _navigationService.NavigateTo(nameof(DetailPage),
                    DetailNavigationContext.Create(app, 0, DateTime.Today));
        }
        catch (Exception ex)
        {
            Logger.Error($"打开详情页失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 实体行按统一分类求和（同时累计应用与网站时长）。
    /// </summary>
    private static Dictionary<int, double> SumEntityByCategory(EntityListModel? entity)
    {
        var result = new Dictionary<int, double>();
        if (entity == null) return result;

        foreach (var row in entity.Rows)
        {
            if (row.CategoryId <= 0 || row.Seconds <= 0) continue;

            result[row.CategoryId] = result.TryGetValue(row.CategoryId, out var current)
                ? current + row.Seconds
                : row.Seconds;
        }

        return result;
    }

    public override void Dispose()
    {
        ChartColorHelper.ThemeChanged -= OnThemeChanged;
        ActiveAppCategories?.Clear();
        EmptyAppCategories?.Clear();
        FilteredEntities?.Clear();
        TagPickerOptions?.Clear();
        EditDirectories?.Clear();
        EditUrlPatterns?.Clear();
        base.Dispose();
    }
}
