using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Styling;
using ShiJian.Client.Librarys;
using ShiJian.Client.ViewModels;

namespace ShiJian.Client.Views;

public partial class IndexPage : UserControl
{
    /// <summary>总览卡错峰进场（比整页动得轻一点）。整页淡入由 PageContainer 统一播放</summary>
    private static readonly Animation CardAnimation = CreateFade((int)MotionTokens.M.TotalMilliseconds);

    /// <summary>相邻两张卡的开始间隔，太小看不出错峰，太大整页显得拖拉</summary>
    private const int CardStaggerMs = 40;

    private bool _entered;

    private IndexPageViewModel? _viewModel;

    public IndexPage()
    {
        InitializeComponent();
        DataContext = ServiceLocator.GetRequiredService<IndexPageViewModel>();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel == null && DataContext is IndexPageViewModel vm)
        {
            _viewModel = vm;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        SyncStatCardsLayout();

        if (_entered) return;
        _entered = true;

        // 首帧直接用最终状态，别在页面进场时再叠一层展开动画
        if (CustomRangeHost != null)
            ExpandMotion.ApplyFinal(CustomRangeHost, _viewModel?.IsCustomRange == true);

        await StaggerStatCardsAsync();
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    /// <summary>「自定义」范围面板：切换周期时按参考项目的方式做高度展开 / 收起</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IndexPageViewModel.IsWebBrowserMerged))
        {
            SyncStatCardsLayout();
            return;
        }

        if (e.PropertyName != nameof(IndexPageViewModel.IsCustomRange) || CustomRangeHost == null) return;
        _ = ExpandMotion.SetExpandedAsync(CustomRangeHost, _viewModel?.IsCustomRange == true);
    }

    /// <summary>「并入浏览器」时隐藏站点卡片并把总览网格切为 3 列等宽</summary>
    private void SyncStatCardsLayout()
    {
        if (StatCardsPanel == null || SiteStatCard == null || WebPageStatCard == null) return;

        var merged = _viewModel?.IsWebBrowserMerged == true;
        SiteStatCard.IsVisible = !merged;
        Grid.SetColumn(WebPageStatCard, merged ? 4 : 6);
        StatCardsPanel.ColumnDefinitions = ColumnDefinitions.Parse(
            merged ? "*,10,*,10,*" : "*,10,*,10,*,10,*");
    }

    /// <summary>
    /// 四张总览卡逐张进场，避免整排"啪"地一起出现
    /// </summary>
    private async Task StaggerStatCardsAsync()
    {
        var panel = StatCardsPanel;
        if (panel == null) return;

        var cards = panel.Children.OfType<Control>().Where(c => c.IsVisible).ToList();
        if (!Motion.IsEnabled)
        {
            foreach (var card in cards) card.Opacity = 1;

            return;
        }
        var running = new List<Task>(cards.Count);

        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            card.Opacity = 0;
            running.Add(AnimateCardAsync(card, TimeSpan.FromMilliseconds(CardStaggerMs * index)));
        }

        await Task.WhenAll(running);
    }

    private static async Task AnimateCardAsync(Control card, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay);

        try
        {
            await CardAnimation.RunAsync(card);
        }
        catch
        {
            card.Opacity = 1;
        }
    }

    private static Animation CreateFade(int durationMs)
    {
        return new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = MotionTokens.Enter(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0d)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1d)
                    }
                }
            }
        };
    }
}
