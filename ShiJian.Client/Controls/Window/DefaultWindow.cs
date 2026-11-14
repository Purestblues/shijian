using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Input;
using ShiJian.Client.Foundation;
using ShiJian.Client.Foundation.Rx;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models;
using ShiJian.Client.Servicers.Interfaces;

namespace ShiJian.Client.Controls.Window;

public class DefaultWindow : Avalonia.Controls.Window
{
    private const double CustomWindowChromeTitleBarHeight = 36;

    public static readonly StyledProperty<IImage?> IconSourceProperty =
        AvaloniaProperty.Register<DefaultWindow, IImage?>(nameof(IconSource));

    public static readonly StyledProperty<bool> MaximizeVisibleProperty =
        AvaloniaProperty.Register<DefaultWindow, bool>(nameof(MaximizeVisible));

    public static readonly StyledProperty<bool> RestoreVisibleProperty =
        AvaloniaProperty.Register<DefaultWindow, bool>(nameof(RestoreVisible));

    public static readonly StyledProperty<PageContainer> PageContainerProperty =
        AvaloniaProperty.Register<DefaultWindow, PageContainer>(nameof(PageContainer));

    public static readonly StyledProperty<ToastType> ToastTypeProperty =
        AvaloniaProperty.Register<DefaultWindow, ToastType>(nameof(ToastType));


    public static readonly StyledProperty<string> DialogTitleProperty =
        AvaloniaProperty.Register<DefaultWindow, string>(nameof(DialogTitle));

    public static readonly StyledProperty<IconTypes> ToastIconProperty =
        AvaloniaProperty.Register<DefaultWindow, IconTypes>(nameof(ToastIcon));

    public static readonly StyledProperty<string> ToastContentProperty =
        AvaloniaProperty.Register<DefaultWindow, string>(nameof(ToastContent));


    public static readonly StyledProperty<string> DialogMessageProperty =
        AvaloniaProperty.Register<DefaultWindow, string>(nameof(DialogMessage));


    public static readonly StyledProperty<string> InputModalValueProperty =
        AvaloniaProperty.Register<DefaultWindow, string>(nameof(InputModalValue));

    public static readonly StyledProperty<bool> IsShowToastProperty =
        AvaloniaProperty.Register<DefaultWindow, bool>(nameof(IsShowToast));

    public static readonly StyledProperty<bool> IsCanBackProperty =
        AvaloniaProperty.Register<DefaultWindow, bool>(nameof(IsCanBack));

    public static readonly StyledProperty<bool> UseCustomWindowChromeProperty =
        AvaloniaProperty.Register<DefaultWindow, bool>(
            nameof(UseCustomWindowChrome),
            true);

    public static readonly StyledProperty<ConnectionStatus> ConnectionStatusProperty =
        AvaloniaProperty.Register<DefaultWindow, ConnectionStatus>(nameof(ConnectionStatus), ConnectionStatus.Checking);

    public static readonly StyledProperty<ICommand?> TitleBarCommandProperty =
        AvaloniaProperty.Register<DefaultWindow, ICommand?>(nameof(TitleBarCommand));

    public static readonly StyledProperty<bool> TitleBarCommandVisibleProperty =
        AvaloniaProperty.Register<DefaultWindow, bool>(nameof(TitleBarCommandVisible), true);

    private CancellationTokenSource _toastCancellationTokenSource;
    private Button.Button CancelBtn, ConfirmBtn, InputModalCancelBtn, InputModalConfirmBtn;
    private InputBox InputModalInputBox;
    private Func<string, bool> InputModalValidFnc;
    private string? InputValue;
    private bool IsDialogConfirm;
    private bool IsShowConfirmDialog, IsShowInputModal, IsShowActionDialog;
    private int ActionDialogResult = -1;
    private Border? _statusDot;
    private EventHandler? _onLoadPagedHandler;
    private readonly IDisposable _windowStateSubscription;


    private Grid titleBar;

    private Size? _normalSize;
    private PixelPoint? _normalPosition;

    private Border ToastBorder, Masklayer, DialogBorder, InputModalBorder, ActionDialogBorder;
    private StackPanel ActionDialogButtonsPanel;
    private Grid ToastGrid;
    public DefaultWindow()
    {
        ApplyWindowChromeMode();

        _windowStateSubscription = RxObservable.Merge(
                ObservablePropertyChangedExtensions.WhenPropertyChanged(this, x => x.MaximizeVisible).Select(_ => true),
                this.GetObservable(Avalonia.Controls.Window.WindowStateProperty).Select(_ => true)
            )
            .Subscribe(_ =>
            {
                var windowState = WindowState;
                switch (windowState)
                {
                    case WindowState.Normal:
                        MaximizeVisible = true;
                        RestoreVisible = false;
                        break;
                    case WindowState.Maximized:
                        RestoreVisible = true;
                        MaximizeVisible = false;
                        break;
                }
            });

        MinimizeWindowCommand = AsyncRelayCommand.Create(() => { WindowState = WindowState.Minimized; });

        RestoreWindowCommand = AsyncRelayCommand.Create(() => { WindowState = WindowState.Normal; });

        MaximizeWindowCommand = AsyncRelayCommand.Create(() => { WindowState = WindowState.Maximized; });

        CloseWindowCommand = AsyncRelayCommand.Create(() => { RequestClose?.Invoke(this, EventArgs.Empty); });

        BackCommand = AsyncRelayCommand.Create(() =>
        {
            // 使用 INavigationService.GoBack() 以正确设置 IsNavigatingBack 标记
            var navigationService = ServiceLocator.GetService<INavigationService>();
            if (navigationService != null)
            {
                navigationService.GoBack();
                if (PageContainer != null && PageContainer.Index == 0) IsCanBack = false;
            }
            else
            {
                // 兜底：直接调用 PageContainer.Back()
                if (PageContainer != null)
                {
                    PageContainer.Back();
                    if (PageContainer.Index == 0) IsCanBack = false;
                }
            }
        });
    }

    public bool MaximizeVisible
    {
        get => GetValue(MaximizeVisibleProperty);
        set => SetValue(MaximizeVisibleProperty, value);
    }


    public bool RestoreVisible
    {
        get => GetValue(RestoreVisibleProperty);
        set => SetValue(RestoreVisibleProperty, value);
    }

    public PageContainer PageContainer
    {
        get => GetValue(PageContainerProperty);
        set => SetValue(PageContainerProperty, value);
    }

    /// <summary>
    /// toast type
    /// </summary>
    public ToastType ToastType
    {
        get => GetValue(ToastTypeProperty);
        set => SetValue(ToastTypeProperty, value);
    }

    /// <summary>
    /// toast content
    /// </summary>
    public string ToastContent
    {
        get => (string)GetValue(ToastContentProperty);
        set => SetValue(ToastContentProperty, value);
    }

    public IconTypes ToastIcon
    {
        get => GetValue(ToastIconProperty);
        set => SetValue(ToastIconProperty, value);
    }

    /// <summary>
    /// Dialog title
    /// </summary>
    public string DialogTitle
    {
        get => (string)GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    /// <summary>
    /// Dialog title
    /// </summary>
    public string InputModalValue
    {
        get => (string)GetValue(InputModalValueProperty);
        set => SetValue(InputModalValueProperty, value);
    }

    /// <summary>
    /// Dialog message
    /// </summary>
    public string DialogMessage
    {
        get => (string)GetValue(DialogMessageProperty);
        set => SetValue(DialogMessageProperty, value);
    }

    public bool IsShowToast
    {
        get => GetValue(IsShowToastProperty);
        set => SetValue(IsShowToastProperty, value);
    }

    /// <summary>
    /// 是否可以返回
    /// </summary>
    public bool IsCanBack
    {
        get => GetValue(IsCanBackProperty);
        set => SetValue(IsCanBackProperty, value);
    }

    public bool UseCustomWindowChrome
    {
        get => GetValue(UseCustomWindowChromeProperty);
        set => SetValue(UseCustomWindowChromeProperty, value);
    }

    public ConnectionStatus ConnectionStatus
    {
        get => GetValue(ConnectionStatusProperty);
        set => SetValue(ConnectionStatusProperty, value);
    }

    public event EventHandler? RequestClose;

    public bool IsWindowClosed { get; private set; }

    public IImage? IconSource
    {
        get => GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public ICommand? TitleBarCommand
    {
        get => GetValue(TitleBarCommandProperty);
        set => SetValue(TitleBarCommandProperty, value);
    }

    public bool TitleBarCommandVisible
    {
        get => GetValue(TitleBarCommandVisibleProperty);
        set => SetValue(TitleBarCommandVisibleProperty, value);
    }

    internal void SetTitleBarHeight(double height)
    {
        if (height > 0 && titleBar != null)
        {
            titleBar.Height = height;
        }
    }


    protected override Type StyleKeyOverride => typeof(DefaultWindow);


    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageContainerProperty) OnPageContainerChanged(change);
        if (change.Property == IsCanBackProperty) OnIsCanBackChanged(change);
        if (change.Property == IsShowToastProperty) OnIsShowToastChanged(change);
        if (change.Property == ConnectionStatusProperty) UpdateConnectionStatus();
        if (change.Property == UseCustomWindowChromeProperty) ApplyWindowChromeMode();
    }

    private void ApplyWindowChromeMode()
    {
        if (UseCustomWindowChrome)
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = CustomWindowChromeTitleBarHeight;
            WindowDecorations = WindowDecorations.None;
            return;
        }

        ExtendClientAreaToDecorationsHint = false;
        ExtendClientAreaTitleBarHeightHint = 0;
        WindowDecorations = WindowDecorations.Full;
    }

    private const double ToastHiddenOffsetY = -44;
    private const double ToastExitOffsetY = -32;
    private const int ToastDisplayDurationMs = 2000;

    private static void OnIsShowToastChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var that = e.Sender as DefaultWindow;
        if (that != null)
        {
            if (that.IsShowToast)
                _ = that.ShowToastAsync();
            else
                _ = that.HideToastAsync();
        }
    }

    private (CancellationTokenSource Cts, CancellationToken Token) ResetToastCts()
    {
        var previous = _toastCancellationTokenSource;
        var next = new CancellationTokenSource();
        _toastCancellationTokenSource = next;

        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        return (next, next.Token);
    }

    private async Task ShowToastAsync()
    {
        if (ToastBorder == null)
            return;

        var (cts, token) = ResetToastCts();

        // 连续触发通知时从当前位置顺滑过渡，而不是生硬跳回起点
        var wasVisible = ToastBorder.IsVisible && ToastBorder.Opacity > 0.05;
        var fromY = wasVisible ? SlideMotion.GetTranslateY(ToastBorder) : ToastHiddenOffsetY;
        var fromOpacity = wasVisible ? ToastBorder.Opacity : 0;

        ToastBorder.IsVisible = true;

        try
        {
            await SlideMotion.AnimateYAndOpacityAsync(
                ToastBorder,
                fromY,
                0,
                fromOpacity,
                1,
                MotionTokens.M,
                MotionTokens.Enter(),
                resetTransformOnComplete: false,
                cancellationToken: token);

            if (token.IsCancellationRequested)
                return;

            await Task.Delay(ToastDisplayDurationMs, token);

            if (!token.IsCancellationRequested && ReferenceEquals(_toastCancellationTokenSource, cts))
            {
                IsShowToast = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        IsShowConfirmDialog = true;
        ToastGrid.IsVisible = true;
        DialogBorder.IsVisible = true;
        ActionDialogBorder.IsVisible = false;
        InputModalBorder.IsVisible = false;

        DialogMessage = message;
        DialogTitle = title;
        DialogBorder.Opacity = 1;
        DialogBorder.RenderTransform = TransformOperations.Parse("translateY(0px)");
        Masklayer.Opacity = 1;

        return Task.Run(async () =>
        {
            while (IsShowConfirmDialog) await Task.Delay(10);

            return IsDialogConfirm;
        });
    }

    public Task<int> ShowActionDialogAsync(string title, string message, string[] buttons)
    {
        IsShowActionDialog = true;
        ActionDialogResult = -1;
        ToastGrid.IsVisible = true;
        DialogBorder.IsVisible = false;
        InputModalBorder.IsVisible = false;
        ActionDialogBorder.IsVisible = true;

        DialogMessage = message;
        DialogTitle = title;
        ActionDialogBorder.Opacity = 1;
        ActionDialogBorder.RenderTransform = TransformOperations.Parse("translateY(0px)");
        Masklayer.Opacity = 1;

        // 清空按钮面板并动态添加新按钮
        if (ActionDialogButtonsPanel != null)
        {
            ActionDialogButtonsPanel.Children.Clear();
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                var btn = new Button.Button
                {
                    Content = buttons[i],
                    MinWidth = 88,
                    Margin = new Thickness(i > 0 ? 8 : 0, 0, 0, 0)
                };
                btn.Classes.Add(i == 0 ? "primary" : "ghost");
                btn.Click += (s, e) =>
                {
                    ActionDialogResult = index;
                    HideActionDialog();
                };
                ActionDialogButtonsPanel.Children.Add(btn);
            }
        }

        return Task.Run(async () =>
        {
            while (IsShowActionDialog) await Task.Delay(10);

            return ActionDialogResult;
        });
    }

    private void ToastBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        IsShowToast = false;
    }

    public Task<string?> ShowInputModalAsync(string title, string message, string value, Func<string, bool> validate)
    {
        IsShowInputModal = true;
        ToastGrid.IsVisible = true;
        DialogBorder.IsVisible = false;
        ActionDialogBorder.IsVisible = false;
        InputModalBorder.IsVisible = true;

        DialogMessage = message;
        DialogTitle = title;
        InputModalValue = value;
        InputModalValidFnc = validate;

        InputModalInputBox.Text = InputModalValue;
        InputModalBorder.Opacity = 1;
        InputModalBorder.RenderTransform = TransformOperations.Parse("translateY(0px)");

        Masklayer.Opacity = 1;

        return Task.Run(async () =>
        {
            while (IsShowInputModal) await Task.Delay(10);

            return IsDialogConfirm ? InputValue : null;
        });
    }


    private async Task HideToastAsync()
    {
        var (cts, token) = ResetToastCts();

        if (ToastBorder == null || !ToastBorder.IsVisible)
            return;

        var fromY = SlideMotion.GetTranslateY(ToastBorder);
        var fromOpacity = ToastBorder.Opacity;

        try
        {
            await SlideMotion.AnimateYAndOpacityAsync(
                ToastBorder,
                fromY,
                ToastExitOffsetY,
                fromOpacity,
                0,
                MotionTokens.S,
                MotionTokens.Leave(),
                resetTransformOnComplete: false,
                cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested && ReferenceEquals(_toastCancellationTokenSource, cts))
        {
            ToastBorder.IsVisible = false;
            ToastBorder.Opacity = 0;
            if (ToastBorder.RenderTransform is TranslateTransform transform)
            {
                transform.Y = ToastHiddenOffsetY;
            }
        }
    }

    private static void OnIsCanBackChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var that = e.Sender as DefaultWindow;
        if (that != null)
        {
            //if (that.IsCanBack)
            //{
            //    VisualStateManager.GoToState(that, "CanBackState", true);
            //}
            //else
            //{
            //    VisualStateManager.GoToState(that, "Normal", true);
            //}
        }
    }

    private static void OnPageContainerChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var that = (DefaultWindow)e.Sender;
        if (that == null) return;

        if (e.OldValue is PageContainer oldPc && that._onLoadPagedHandler != null)
        {
            oldPc.OnLoadPaged -= that._onLoadPagedHandler;
        }

        if (e.NewValue is PageContainer newPc)
        {
            that.IsCanBack = newPc.Index >= 1;

            that._onLoadPagedHandler = (s, v) =>
            {
                var pc = s as PageContainer;
                that.IsCanBack = pc?.Index >= 1;
            };

            newPc.OnLoadPaged += that._onLoadPagedHandler;
        }
        else
        {
            that._onLoadPagedHandler = null;
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        titleBar = e.NameScope.Find<Grid>("TitleBar");
        if (ToastBorder != null)
            ToastBorder.PointerPressed -= ToastBorder_PointerPressed;
        ToastBorder = e.NameScope.Find<Border>("ToastBorder");
        if (ToastBorder != null)
            ToastBorder.PointerPressed += ToastBorder_PointerPressed;
        Masklayer = e.NameScope.Find<Border>("Masklayer");
        ToastGrid = e.NameScope.Find<Grid>("ToastGrid");
        DialogBorder = e.NameScope.Find<Border>("DialogBorder");
        ActionDialogBorder = e.NameScope.Find<Border>("ActionDialogBorder");
        ActionDialogButtonsPanel = e.NameScope.Find<StackPanel>("ActionDialogButtonsPanel");
        CancelBtn = e.NameScope.Find<Button.Button>("CancelBtn");
        ConfirmBtn = e.NameScope.Find<Button.Button>("ConfirmBtn");
        InputModalBorder = e.NameScope.Find<Border>("InputModalBorder");
        InputModalCancelBtn = e.NameScope.Find<Button.Button>("InputModalCancelBtn");
        InputModalConfirmBtn = e.NameScope.Find<Button.Button>("InputModalConfirmBtn");
        InputModalInputBox = e.NameScope.Find<InputBox>("InputModalInputBox");

        _statusDot = e.NameScope.Find<Border>("StatusDot");

        UpdateConnectionStatus();

        if (CancelBtn != null)
            CancelBtn.Click += (e, c) =>
            {
                IsDialogConfirm = false;
                HideDialog();
            };

        if (ConfirmBtn != null)
            ConfirmBtn.Click += (e, c) =>
            {
                IsDialogConfirm = true;
                HideDialog();
            };

        if (InputModalCancelBtn != null)
            InputModalCancelBtn.Click += (e, c) =>
            {
                IsDialogConfirm = false;
                HideInputModal();
            };

        if (InputModalConfirmBtn != null)
            InputModalConfirmBtn.Click += (e, c) =>
            {
                if (InputModalValidFnc != null && !InputModalValidFnc(InputValue)) return;
                IsDialogConfirm = true;
                HideInputModal();
            };

        if (InputModalInputBox != null)
            InputModalInputBox.TextChanged += (e, c) => { InputValue = InputModalInputBox.Text; };

        if (IsShowToast)
            _ = ShowToastAsync();
    }

    private void HideDialog()
    {
        DialogBorder.Opacity = 0;
        DialogBorder.RenderTransform = TransformOperations.Parse("translateY(8px)");
        Masklayer.Opacity = 0;
        ToastGrid.IsVisible = false;
        IsShowConfirmDialog = false;
    }

    private void HideActionDialog()
    {
        ActionDialogBorder.Opacity = 0;
        ActionDialogBorder.RenderTransform = TransformOperations.Parse("translateY(8px)");
        Masklayer.Opacity = 0;
        ToastGrid.IsVisible = false;
        IsShowActionDialog = false;
    }

    private void HideInputModal()
    {
        InputModalBorder.Opacity = 0;
        InputModalBorder.RenderTransform = TransformOperations.Parse("translateY(8px)");
        Masklayer.Opacity = 0;
        ToastGrid.IsVisible = false;
        IsShowInputModal = false;
    }

    private void UpdateConnectionStatus()
    {
        if (_statusDot == null) return;

        var app = Application.Current;
        IBrush ResolveBrush(string key, string fallback) =>
            app != null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush b
                ? b
                : new SolidColorBrush(Color.Parse(fallback));

        IBrush brush;
        string toolTipKey;

        switch (ConnectionStatus)
        {
            case ConnectionStatus.Connected:
                brush = ResolveBrush("SuccessBrush", "#5C9A64");
                toolTipKey = "ConnectionStatusConnected";
                break;
            case ConnectionStatus.Disconnected:
                brush = ResolveBrush("DangerBrush", "#BF616A");
                toolTipKey = "ConnectionStatusDisconnected";
                break;
            case ConnectionStatus.Checking:
            default:
                brush = ResolveBrush("DisabledTextBrush", "#9AA3B5");
                toolTipKey = "ConnectionStatusChecking";
                break;
        }

        _statusDot.Background = brush;
        ToolTip.SetTip(_statusDot, app?.FindResource(toolTipKey) ?? string.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        _toastCancellationTokenSource?.Cancel();
        _toastCancellationTokenSource?.Dispose();
        _toastCancellationTokenSource = null;
        _windowStateSubscription.Dispose();
        base.OnClosed(e);
        IsWindowClosed = true;
    }

    /// <summary>
    /// 若当前正展示窗口级确认框、动作选择框或输入框，则按取消关闭。
    /// 返回 true 表示确有激活的对话框被关闭。
    /// </summary>
    public bool CancelActiveDialog()
    {
        if (IsShowConfirmDialog)
        {
            IsDialogConfirm = false;
            HideDialog();
            return true;
        }

        if (IsShowActionDialog)
        {
            ActionDialogResult = -1;
            HideActionDialog();
            return true;
        }

        if (IsShowInputModal)
        {
            IsDialogConfirm = false;
            HideInputModal();
            return true;
        }

        return false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.Handled || titleBar == null) return;

        try
        {
            // TitleBar 位于带 Margin 的 WindowBorder 内，必须转换到 titleBar 自身坐标系判定，
            // 否则 TitleBar 下半部分（受 Margin 与 DPI 缩放影响）会落出 bounds。
            var point = e.GetCurrentPoint(titleBar);
            var titleBarBounds = new Rect(titleBar.Bounds.Size);
            if (titleBarBounds.Contains(point.Position) && point.Properties.IsLeftButtonPressed)
            {
                // 双击标题栏：最大化 / 还原
                if (e.ClickCount >= 2)
                {
                    ToggleWindowState();
                    e.Handled = true;
                    return;
                }

                BeginMoveDrag(e);
            }
        }
        catch
        {
            // ignored
        }
    }


    /// <summary>
    /// 在 最大化 / 还原 之间切换（双击标题栏、双击窗口上沿都会用到）
    /// </summary>
    public void ToggleWindowState()
    {
        if (WindowState == WindowState.Minimized) return;

        if (WindowState is WindowState.Maximized or WindowState.FullScreen)
        {
            RestoreNormalBounds();
            return;
        }

        // 最大化前把当前正常尺寸记下来，还原时直接回写，
        // 不依赖平台的 restore bounds（自定义 chrome 下它会被中间尺寸污染）
        RememberNormalBounds();
        WindowState = WindowState.Maximized;
    }

    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;

        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        _normalSize = size;
        _normalPosition = Position;
    }

    private void RestoreNormalBounds()
    {
        WindowState = WindowState.Normal;

        if (_normalSize is not { } size) return;

        // 等平台完成状态切换再回写，否则会被它自己的还原尺寸覆盖
        Dispatcher.UIThread.Post(() =>
        {
            if (WindowState != WindowState.Normal) return;

            if (Math.Abs(Width - size.Width) > 0.5 || Math.Abs(Height - size.Height) > 0.5)
            {
                Width = size.Width;
                Height = size.Height;
            }

            if (_normalPosition is { } position && Position != position)
            {
                Position = position;
            }
        }, DispatcherPriority.Loaded);
    }


    #region sys command

    public static ICommand MinimizeWindowCommand { get; private set; }
    public static ICommand RestoreWindowCommand { get; private set; }
    public static ICommand MaximizeWindowCommand { get; private set; }
    public static ICommand CloseWindowCommand { get; private set; }
    public static ICommand LogoButtonClickCommand { get; private set; }
    public static ICommand BackCommand { get; private set; }

    #endregion
}
