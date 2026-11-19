using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.VisualTree;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models;
using ShiJian.Client.ViewModels;

namespace ShiJian.Client.Views;

public partial class SettingPage : UserControl
{
    private const double TabSlideOffsetPx = 12.0;

    private readonly StackPanel[] _tabPanels;
    private IDisposable? _heightBinding;
    private ScrollViewer? _outerScrollViewer;
    private SettingPageViewModel? _boundVm;
    private CancellationTokenSource? _tabSwitchCts;
    private int _activeTabIndex;

    public SettingPage()
        : this(ServiceLocator.GetRequiredService<SettingPageViewModel>())
    {
    }

    public SettingPage(SettingPageViewModel vm)
    {
        InitializeComponent();
        _tabPanels =
        [
            TabPanelGeneral,
            TabPanelAppearance,
            TabPanelTracking,
            TabPanelData,
            TabPanelAbout,
            TabPanelAiMcp
        ];

        _boundVm = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = vm;
        ApplyTabImmediate(vm.TabbarSelectedIndex);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingPageModel.TabbarSelectedIndex) || _boundVm == null) return;

        SmoothScroll.CancelAnimation(ContentScrollViewer);
        if (ContentScrollViewer != null)
        {
            ContentScrollViewer.Offset = default;
        }

        SwitchToTab(_boundVm.TabbarSelectedIndex);
    }

    private void SwitchToTab(int rawIndex)
    {
        var nextIndex = Math.Clamp(rawIndex, 0, _tabPanels.Length - 1);
        var prevIndex = _activeTabIndex;

        CancelTabSwitch();

        if (prevIndex == nextIndex || !Motion.IsEnabled || TopLevel.GetTopLevel(this) == null)
        {
            ApplyTabImmediate(nextIndex);
            return;
        }

        _activeTabIndex = nextIndex;
        var cts = new CancellationTokenSource();
        _tabSwitchCts = cts;
        var direction = nextIndex > prevIndex ? 1 : -1;

        for (int i = 0; i < _tabPanels.Length; i++)
        {
            if (i != prevIndex && i != nextIndex)
            {
                _tabPanels[i].IsVisible = false;
                _tabPanels[i].Opacity = 1;
                SlideMotion.Reset(_tabPanels[i]);
            }
        }

        _ = AnimateTabSwitchAsync(_tabPanels[prevIndex], _tabPanels[nextIndex], direction, cts.Token);
    }

    private async Task AnimateTabSwitchAsync(
        StackPanel oldPanel,
        StackPanel newPanel,
        int direction,
        CancellationToken token)
    {
        try
        {
            if (oldPanel.IsVisible)
            {
                await SlideMotion.AnimateTransformAndOpacityAsync(
                    oldPanel,
                    0, -TabSlideOffsetPx * direction,
                    0, 0,
                    1d, 1d,
                    oldPanel.Opacity, 0d,
                    MotionTokens.Xs,
                    MotionTokens.Leave(),
                    resetTransformOnComplete: false,
                    cancellationToken: token);

                if (token.IsCancellationRequested) return;

                oldPanel.IsVisible = false;
                oldPanel.Opacity = 1;
                SlideMotion.Reset(oldPanel);
            }

            if (token.IsCancellationRequested) return;

            newPanel.IsVisible = true;
            await SlideMotion.AnimateTransformAndOpacityAsync(
                newPanel,
                TabSlideOffsetPx * direction, 0,
                0, 0,
                1d, 1d,
                0d, 1d,
                MotionTokens.S,
                MotionTokens.Enter(),
                resetTransformOnComplete: true,
                cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                newPanel.IsVisible = true;
                newPanel.Opacity = 1;
                SlideMotion.Reset(newPanel);
            }
        }
    }

    private void ApplyTabImmediate(int rawIndex)
    {
        var target = Math.Clamp(rawIndex, 0, _tabPanels.Length - 1);
        _activeTabIndex = target;

        for (int i = 0; i < _tabPanels.Length; i++)
        {
            var panel = _tabPanels[i];
            panel.IsVisible = i == target;
            panel.Opacity = 1;
            SlideMotion.Reset(panel);
        }
    }

    private void CancelTabSwitch()
    {
        var previous = _tabSwitchCts;
        _tabSwitchCts = null;
        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _outerScrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_outerScrollViewer != null)
        {
            _outerScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

            var binding = new Binding
            {
                Source = _outerScrollViewer,
                Path = "Viewport.Height"
            };
            _heightBinding = this.Bind(HeightProperty, binding);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        CancelTabSwitch();

        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm = null;
        }

        _heightBinding?.Dispose();
        _heightBinding = null;

        if (_outerScrollViewer != null)
        {
            _outerScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _outerScrollViewer = null;
        }
    }
}
