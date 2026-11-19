using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using ShiJian.Client.Controls.Models;
using ShiJian.Client.Foundation;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models;
using ShiJian.Client.Servicers.Interfaces;

namespace ShiJian.Client.Controls;

public class PageContainer : TemplatedControl
{
    public static readonly StyledProperty<List<string>> IndexUriListProperty =
        AvaloniaProperty.Register<PageContainer, List<string>>(nameof(IndexUriList));

    public static readonly StyledProperty<ICommand> BackCommandProperty =
        AvaloniaProperty.Register<PageContainer, ICommand>(nameof(BackCommand));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PageContainer, string>(nameof(Title));

    public static readonly StyledProperty<object> ContentProperty =
        AvaloniaProperty.Register<PageContainer, object>(nameof(Content));

    public static readonly StyledProperty<string> UriProperty =
        AvaloniaProperty.Register<PageContainer, string>(nameof(Uri), string.Empty);

    public static readonly StyledProperty<PageContainer> InstanceProperty =
        AvaloniaProperty.Register<PageContainer, PageContainer>(nameof(Instance));

    private readonly List<string> Historys;
    private readonly Dictionary<string, double> ScrollPositions;
    private readonly Dictionary<string, object?> NavigationDatas;

    private static readonly Dictionary<string, Type> PageTypeMap = new()
    {
        ["IndexPage"] = typeof(Views.IndexPage),
        ["DataPage"] = typeof(Views.DataPage),
        ["SettingPage"] = typeof(Views.SettingPage),
        ["DetailPage"] = typeof(Views.DetailPage),
        ["CategoryPage"] = typeof(Views.CategoryPage),
        ["CategoryAppListPage"] = typeof(Views.CategoryAppListPage),
        ["CategoryWebSiteListPage"] = typeof(Views.CategoryWebSiteListPage),
        ["CategorySummaryPage"] = typeof(Views.CategorySummaryPage),
        ["ChartPage"] = typeof(Views.ChartPage),
        ["WebSiteDetailPage"] = typeof(Views.WebSiteDetailPage),
    };

    private ContentPresenter ContentPresenter;
    public int Index, OldIndex;
    private bool IsBack;
    private ScrollViewer ScrollViewer;
    private string? PendingUri;
    private CancellationTokenSource? _navigationCts;
    private int _navigationVersion;
    private bool _isExiting;

    public PageContainer()
    {
        Historys = new List<string>();
        ScrollPositions = new Dictionary<string, double>();
        NavigationDatas = new Dictionary<string, object?>();
        BackCommand = AsyncRelayCommand.Create<object>(OnBackCommand);
    }

    public List<string> IndexUriList
    {
        get => GetValue(IndexUriListProperty);
        set => SetValue(IndexUriListProperty, value);
    }

    public ICommand BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public string Uri
    {
        get => GetValue(UriProperty);
        set => SetValue(UriProperty, value);
    }

    public PageContainer Instance
    {
        get => GetValue(InstanceProperty);
        set => SetValue(InstanceProperty, value);
    }

    public ModelBase? CurrentViewModel { get; private set; }

    /// <summary>是否还能返回上一页（顶层导航页会清空历史，此时为 false）</summary>
    public bool CanGoBack => Index > 0;

    protected override Type StyleKeyOverride => typeof(PageContainer);

    public event EventHandler OnLoadPaged;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        ScrollViewer = e.NameScope.Get<ScrollViewer>("ScrollViewer");
        ContentPresenter = e.NameScope.Get<ContentPresenter>("Frame");
        Loaded += PageContainer_Loaded;
        Unloaded += PageContainer_Unloaded;
    }

    private void PageContainer_Loaded(object? sender, RoutedEventArgs e)
    {
        Instance = this;
    }

    private void PageContainer_Unloaded(object? sender, RoutedEventArgs e)
    {
        CancelPendingNavigation();
        Loaded -= PageContainer_Loaded;
        Unloaded -= PageContainer_Unloaded;
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        if (PendingUri != null)
        {
            var uri = PendingUri;
            PendingUri = null;
            Uri = uri;
        }
    }

    private void OnBackCommand(object obj)
    {
        Back();
    }

    public void Back()
    {
        if (Index - 1 >= 0)
        {
            OldIndex = Index;
            Index--;
            var uri = Historys[Index];

            var preIndex = Index + 1;
            var pageUri = Historys[preIndex];

            ScrollPositions.Remove(pageUri);
            NavigationDatas.Remove(pageUri);
            Historys.RemoveRange(preIndex, 1);

            IsBack = true;

            if (NavigationDatas.TryGetValue(uri, out var data))
            {
                var navService = ServiceLocator.GetService<INavigationDataService>() as MainWindowModel;
                if (navService != null)
                    navService.Data = data;
            }

            Uri = uri;
        }
    }

    public void ClearHistorys()
    {
        Historys.Clear();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UriProperty) OnUriChanged(change);
    }

    private void OnUriChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var control = e.Sender as PageContainer;
        if (string.IsNullOrEmpty(e.NewValue?.ToString())) return;

        // 如果模板尚未应用，挂起本次导航，等附加到逻辑树后再执行
        if (control.ContentPresenter == null || control.ScrollViewer == null)
        {
            control.PendingUri = e.NewValue?.ToString();
            return;
        }

        var oldUri = e.OldValue?.ToString();
        if (oldUri != null)
        {
            control.ScrollPositions[oldUri] = control.ScrollViewer?.Offset.Y ?? 0;
        }

        control.LoadPage();
    }

    private PageModel GetPage(string targetUri)
    {
        UserControl? page = null;
        if (PageTypeMap.TryGetValue(targetUri, out var pageType))
            page = ServiceLocator.GetRequiredService(pageType) as UserControl;
        return new PageModel
        {
            Instance = page,
            ScrollValue = ScrollPositions.TryGetValue(targetUri, out var scroll) ? scroll : 0
        };
    }

    private void LoadPage()
    {
        var isBack = IsBack;
        IsBack = false;

        try
        {
            // 模板未就绪时挂起
            if (ContentPresenter == null || ScrollViewer == null)
            {
                PendingUri = Uri;
                IsBack = isBack;
                return;
            }

            var targetUri = Uri;
            if (targetUri == string.Empty) return;

            var isTopLevel = IndexUriList != null && IndexUriList.Contains(targetUri);
            var direction = isBack
                ? PageTransitionDirection.Back
                : isTopLevel
                    ? PageTransitionDirection.Replace
                    : PageTransitionDirection.Forward;

            if (isTopLevel)
            {
                Historys.Clear();
                Index = 0;
                OldIndex = 0;
                Historys.Add(targetUri);
                if (!isBack) ClearCache();
            }
            else
            {
                // 处理历史记录
                if (OldIndex == Index)
                {
                    // 新开页面时保存导航数据
                    var navService = ServiceLocator.GetService<INavigationDataService>() as MainWindowModel;
                    if (navService?.Data != null)
                        NavigationDatas[targetUri] = navService.Data;

                    Historys.Add(targetUri);
                    Index++;
                }

                OldIndex = Index;
            }

            var wasExiting = _isExiting;
            var (version, cts) = BeginNavigation();

            var leavingPage = Content as UserControl;
            var canAnimateExit = Motion.IsEnabled
                                 && !wasExiting
                                 && leavingPage != null
                                 && TopLevel.GetTopLevel(this) != null
                                 && TopLevel.GetTopLevel(leavingPage) != null;

            if (canAnimateExit)
            {
                _ = TransitionPageAsync(leavingPage!, targetUri, isBack, direction, version, cts.Token);
                return;
            }

            DisposeCurrentPage();
            MountAndEnterPage(targetUri, isBack, direction, cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"页面加载失败 [{Uri}]: {ex}");
            throw;
        }
    }

    private async Task TransitionPageAsync(
        UserControl leavingPage,
        string targetUri,
        bool isBack,
        PageTransitionDirection direction,
        int version,
        CancellationToken token)
    {
        _isExiting = true;
        leavingPage.IsHitTestVisible = false;

        try
        {
            await PageMotion.PlayExitAsync(leavingPage, direction, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _isExiting = false;
        }

        if (token.IsCancellationRequested || version != _navigationVersion)
            return;

        DisposePage(leavingPage);
        MountAndEnterPage(targetUri, isBack, direction, token);
    }

    private void MountAndEnterPage(
        string targetUri,
        bool isBack,
        PageTransitionDirection direction,
        CancellationToken token)
    {
        var page = GetPage(targetUri);
        if (page?.Instance == null)
        {
            Debug.WriteLine("找不到Page：" + targetUri + "，请确认已被注入");
            CurrentViewModel = null;
            return;
        }

        // 先设置进场首帧态，再附加新内容，避免首帧闪出完整内容
        if (Motion.IsEnabled)
        {
            PageMotion.Prepare(page.Instance, direction);
        }
        else
        {
            PageMotion.SnapToRest(page.Instance);
        }

        // 安全序列：先分离旧内容逻辑树，再附加新内容，避免 Avalonia 12 逻辑树竞态
        ContentPresenter.Content = null;
        ContentPresenter.Content = page.Instance;

        // 同步 Content 属性供外部 Binding 观察
        SetValue(ContentProperty, page.Instance);

        // 统一进场动效
        _ = PageMotion.PlayEnterAsync(page.Instance, direction, token);

        // 滚动条位置处理：先中止前序惯性滚动
        SmoothScroll.CancelAnimation(ScrollViewer);
        if (isBack)
            ScrollViewer.Offset = new Vector(0, page.ScrollValue);
        else
            ScrollViewer?.ScrollToHome();

        OnLoadPaged?.Invoke(this, EventArgs.Empty);

        if (page.Instance.DataContext is ModelBase newVm)
        {
            CurrentViewModel = newVm;
            _ = newVm.OnNavigatedToAsync();
        }
        else
        {
            CurrentViewModel = null;
        }
    }

    private (int Version, CancellationTokenSource Cts) BeginNavigation()
    {
        var previous = _navigationCts;
        var next = new CancellationTokenSource();
        _navigationCts = next;
        var version = ++_navigationVersion;

        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        return (version, next);
    }

    private void CancelPendingNavigation()
    {
        _navigationVersion++;
        _isExiting = false;
        var previous = _navigationCts;
        _navigationCts = null;
        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    private void DisposeCurrentPage()
    {
        if (Content is UserControl oldPage)
        {
            DisposePage(oldPage);
        }
    }

    private static void DisposePage(UserControl page)
    {
        if (page.DataContext is ModelBase oldVm)
        {
            oldVm.OnNavigatedFrom();
            if (oldVm is IDisposable disposable)
                disposable.Dispose();
            page.DataContext = null;
        }
        else if (page.DataContext is IDisposable disposable)
        {
            disposable.Dispose();
            page.DataContext = null;
        }
    }

    private void ClearCache()
    {
        System.Diagnostics.Debug.WriteLine("[PageContainer] ClearCache called - clearing all state cache");
        ScrollPositions.Clear();
        NavigationDatas.Clear();

        // 清除状态服务中的所有缓存
        var stateService = ServiceLocator.GetService<IStateService>();
        stateService?.Clear();
        System.Diagnostics.Debug.WriteLine("[PageContainer] StateService cleared");
    }
}
