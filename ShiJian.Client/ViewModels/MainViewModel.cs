using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Navigation.Models;
using ShiJian.Client.Controls.Window;
using ShiJian.Client.Models;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Servicers.Updater;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.Librarys.Api;
using ShiJian.Client.Views;
using ShiJian.Client.Logging;

namespace ShiJian.Client.ViewModels;

public class MainViewModel : MainWindowModel, IToastService, INavigationService, INavigationDataService
{
    private readonly IAppConfig _appConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly IShiJianApiClient _apiClient;
    private readonly IProcessService _processService;
    private readonly string[] _pages = [nameof(IndexPage), nameof(ChartPage), nameof(DataPage), nameof(CategoryPage)];
    private readonly CompositeDisposable _disposables = new();
    private readonly CancellationTokenSource _connectionCts = new();
    private bool _isConnectionMonitoringStarted;
    private bool _isNavigatingBack;

    private ISearchService? _searchService;
    private SearchPaletteViewModel? _searchVm;
    private bool _isSearchOpen;

    public MainViewModel(
        IServiceProvider serviceProvider,
        IAppConfig appConfig,
        IShiJianApiClient apiClient,
        IProcessService processService
    )
    {
        _serviceProvider = serviceProvider;
        _appConfig = appConfig;
        _apiClient = apiClient;
        _processService = processService;
        ServiceProvider = serviceProvider;

        OnSelectedCommand = AsyncRelayCommand.Create<object>(OnSelectedCommandHandle);
        GotoPageCommand = AsyncRelayCommand.Create<object>(OnGotoPageCommand);
        OpenPluginUiCommand = AsyncRelayCommand.CreateFromTask(OnOpenPluginUiAsync);
        OpenSearchCommand = AsyncRelayCommand.Create(() => IsSearchOpen = true);
        ToggleSidebarCommand = AsyncRelayCommand.Create(() => IsSidebarCollapsed = !IsSidebarCollapsed);

#if !DEBUG
        Title = "时见";
#else
        Title = "时见 -Debug";
#endif

        ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.Uri)
            .Subscribe(uri =>
            {
                if (uri == nameof(IndexPage)) Data = null;
                SyncNavigationSelection(uri);
            })
            .DisposeWith(_disposables);

        _appConfig.ConfigChanged += OnConfigChanged;
        InitNavigation();
    }

    /// <summary>
    /// 根据当前页面 Uri 同步左侧边栏高亮状态：主导航页激活对应图标，设置页激活底部设置项，
    /// 详情页或子页面（如 Ctrl+K 跳转 DetailPage、分类下钻列表）清空侧栏主导航高亮。
    /// </summary>
    internal void SyncNavigationSelection(string? uri)
    {
        IsSettingSelected = string.Equals(uri, nameof(SettingPage), StringComparison.Ordinal);

        var matching = string.IsNullOrEmpty(uri)
            ? null
            : Items?.FirstOrDefault(item => string.Equals(item.Uri, uri, StringComparison.Ordinal));

        if (!ReferenceEquals(NavSelectedItem, matching))
        {
            NavSelectedItem = matching;
        }
    }

    private void StartConnectionMonitoring()
    {
        if (_isConnectionMonitoringStarted)
            return;
        _isConnectionMonitoringStarted = true;

        _ = Task.Run(async () =>
        {
            while (!_connectionCts.IsCancellationRequested)
            {
                await CheckConnectionAsync();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _connectionCts.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        });
    }

    private async Task CheckConnectionAsync()
    {
        try
        {
            var isConnected = await _apiClient.HealthCheckAsync();
            var newStatus = isConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ConnectionStatus != newStatus)
                {
                    ConnectionStatus = newStatus;
                    if (!isConnected)
                    {
                        Error(ResourceStrings.ServerDisconnected);
                    }
                    else
                    {
                        try
                        {
                            _ = _appConfig.LoadAsync();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"重连后同步配置失败: {ex.Message}", ex);
                        }
                    }
                }
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ConnectionStatus != ConnectionStatus.Disconnected)
                {
                    ConnectionStatus = ConnectionStatus.Disconnected;
                    Error(ResourceStrings.ServerDisconnected);
                }
            });
        }
    }

    public ICommand OnSelectedCommand { get; }
    public ICommand GotoPageCommand { get; }

    /// <summary>
    /// 标题栏「插件」按钮：在默认浏览器里打开本地插件 Web UI
    /// </summary>
    public ICommand OpenPluginUiCommand { get; }
    public ICommand OpenSearchCommand { get; }
    public ICommand ToggleSidebarCommand { get; }

    private async Task OnOpenPluginUiAsync()
    {
        // 先探活：shijian-shell 没起来时与其开一个打不开的标签页，不如直接告诉用户
        if (!await _apiClient.HealthCheckAsync())
        {
            Error(ResourceStrings.ServerDisconnected);
            return;
        }

        try
        {
            _processService.OpenUrl(ServerEndpoints.PluginUiUrl);
        }
        catch (Exception ex)
        {
            Logger.Error($"打开插件界面失败: {ex.Message}", ex);
            Error(ResourceStrings.OpenPluginUiFailed);
        }
    }

    private void OnGotoPageCommand(object obj)
    {
        Uri = obj.ToString();
    }

    private void InitNavigation()
    {
        IndexUriList = new List<string>(_pages);

        Items =
        [
            new NavigationItemModel
            {
                Title = ResolveNavTitle("SideOverview", "概览"),
                UnSelectedIcon = IconTypes.Home,
                SelectedIcon = IconTypes.HomeSolid,
                Uri = nameof(IndexPage),
                ID = -1,
            },
            new NavigationItemModel
            {
                Title = ResolveNavTitle("SideStatistics", "统计"),
                UnSelectedIcon = IconTypes.ZeroBars,
                SelectedIcon = IconTypes.ZeroBars,
                Uri = nameof(ChartPage),
                ID = 1,
            },
            new NavigationItemModel
            {
                Title = ResolveNavTitle("SideDetails", "详细"),
                UnSelectedIcon = IconTypes.BulletedList,
                SelectedIcon = IconTypes.BulletedList,
                Uri = nameof(DataPage),
                ID = 2,
            },
            new NavigationItemModel
            {
                Title = ResolveNavTitle("SideSort", "分类"),
                UnSelectedIcon = IconTypes.Folder,
                SelectedIcon = IconTypes.FolderFill,
                Uri = nameof(CategoryPage),
                ID = 3,
            }
        ];
    }

    private void OnConfigChanged(object? sender, Shared.Event.ConfigChangedEventArgs e)
    {
        if (!e.HasChange("General.Language")) return;
        if (Avalonia.Application.Current == null)
        {
            RefreshNavigationTitles();
            return;
        }
        Dispatcher.UIThread.Post(RefreshNavigationTitles);
    }

    private void RefreshNavigationTitles()
    {
        if (Items == null) return;
        foreach (var item in Items)
        {
            item.Title = item.Uri switch
            {
                nameof(IndexPage) => ResolveNavTitle("SideOverview", "概览"),
                nameof(ChartPage) => ResolveNavTitle("SideStatistics", "统计"),
                nameof(DataPage) => ResolveNavTitle("SideDetails", "详细"),
                nameof(CategoryPage) => ResolveNavTitle("SideSort", "分类"),
                _ => item.Title
            };
        }
    }

    private static string ResolveNavTitle(string resourceKey, string fallback)
    {
        var app = Avalonia.Application.Current;
        if (app != null && app.CheckAccess() && app.Resources.TryGetResource(resourceKey, app.ActualThemeVariant, out var value) && value is string s && !string.IsNullOrEmpty(s))
        {
            return s;
        }
        return fallback;
    }


    public void Toast(string content, ToastType type = ToastType.Info, IconTypes icon = IconTypes.Accept)
    {
        ToastContent = content;
        ToastIcon = icon;
        ToastType = type;
        IsShowToast = true;
    }

    public void Error(string message)
    {
        Toast(message, ToastType.Error, IconTypes.Error);
    }

    public void Info(string message)
    {
        Toast(message, ToastType.Info, IconTypes.Info);
    }

    public void Success(string message)
    {
        Toast(message, ToastType.Success);
    }

    public void NavigateTo(string pageName, object? data = null)
    {
        System.Diagnostics.Debug.WriteLine($"[MainViewModel] NavigateTo: {pageName}, setting IsNavigatingBack = false");
        _isNavigatingBack = false; // 新导航重置返回状态
        Data = data;
        Uri = pageName;
    }

    public void GoBack()
    {
        System.Diagnostics.Debug.WriteLine($"[MainViewModel] GoBack called, setting IsNavigatingBack = true");
        _isNavigatingBack = true; // 设置返回导航标记
        PageContainer?.Back();
    }

    /// <summary>
    /// 当前是否为返回导航
    /// </summary>
    public bool IsNavigatingBack => _isNavigatingBack;

    /// <summary>
    /// 重置导航状态
    /// </summary>
    public void ResetNavigationState()
    {
        _isNavigatingBack = false;
    }

    /// <summary>
    /// 刷新当前页面数据
    /// </summary>
    public void RefreshCurrentPage()
    {
        if (PageContainer?.CurrentViewModel != null)
            _ = PageContainer.CurrentViewModel.RefreshAsync();
    }

    private bool _isStartupInitCompleted;

    public void LoadDefaultPage()
    {
        LoadDefaultPageInternal();

        // 订阅全局搜索 Toggle 请求。延迟到这里订阅以规避 MainViewModel↔SearchService 的构造期循环依赖
        // （SearchService 注入 INavigationService=本实例）。此时单例均已构造完毕。
        if (_searchService == null)
        {
            _searchService = _serviceProvider.GetService(typeof(ISearchService)) as ISearchService;
            if (_searchService != null)
            {
                _searchService.SearchToggleRequested += OnSearchToggle;
            }
        }

        if (_isStartupInitCompleted) return;
        _isStartupInitCompleted = true;

        var config = _appConfig.GetConfig();
        if (config.General.IsAutoUpdate)
        {
            var updateService = ServiceLocator.GetService<UpdateCheckerService>();
            if (updateService != null)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    await updateService.AutoCheckForUpdatesAsync();
                });
            }
        }

        StartConnectionMonitoring();
    }

    private void LoadDefaultPageInternal()
    {
        var config = _appConfig.GetConfig();
        var startPageIndex = config.General.StartPage;
        if (startPageIndex >= 0 && startPageIndex < Items.Count)
        {
            NavSelectedItem = Items[startPageIndex];
            Uri = Items[startPageIndex].Uri;
        }
    }

    private void OnSelectedCommandHandle(object obj)
    {
        if (!string.IsNullOrEmpty(NavSelectedItem?.Uri))
            Uri = NavSelectedItem.Uri;
    }

    public void Dispose()
    {
        _appConfig.ConfigChanged -= OnConfigChanged;
        if (_searchService != null)
        {
            _searchService.SearchToggleRequested -= OnSearchToggle;
            _searchService = null;
        }
        _searchVm?.Dispose();
        _searchVm = null;

        _connectionCts.Cancel();
        _connectionCts.Dispose();
        _disposables.Dispose();
    }

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set
        {
            if (_isSearchOpen == value) return;
            _isSearchOpen = value;
            OnPropertyChanged();

            // 展开时确保搜索 VM 就绪，并把它的关闭回调接到本状态
            if (value)
            {
                EnsureSearchVm();
                _searchVm?.ResetForReopen();
                // 每次展开重新加载，确保拿到分类增删改后的最新语料
                _ = _searchVm?.RefreshAsync();
            }
            else
            {
                _searchVm?.ResetForReopen();
            }
        }
    }

    public SearchPaletteViewModel SearchVm
    {
        get
        {
            EnsureSearchVm();
            return _searchVm!;
        }
    }

    private void EnsureSearchVm()
    {
        if (_searchVm != null) return;
        _searchVm = _serviceProvider.GetService(typeof(SearchPaletteViewModel)) as SearchPaletteViewModel;
        if (_searchVm != null)
        {
            // 导航跳转/清除后统一收口到 IsSearchOpen，无需独立窗口的关闭逻辑
            _searchVm.CloseRequested = () => IsSearchOpen = false;
        }
    }

    private void OnSearchToggle()
    {
        IsSearchOpen = !IsSearchOpen;
    }
}
