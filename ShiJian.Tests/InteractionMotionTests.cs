using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShiJian.Client;
using ShiJian.Client.Controls.Button;
using ShiJian.Client.Controls.Navigation;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Librarys;
using ShiJian.Client.Servicers.Updater;
using ShiJian.Client.Shared.Models.Config;
using ShiJian.Client.ViewModels;
using ShiJian.Client.Views;
using Xunit;

namespace ShiJian.Tests;

public class InteractionMotionTests
{
    [AvaloniaFact]
    public async Task OverlayTransitionController_RapidOpenCloseOpen_NeverHidesReopenedOverlay()
    {
        var container = new Grid { IsVisible = false };
        var scrim = new Border { Opacity = 0 };
        var card = new Border { Width = 200, Height = 120, Opacity = 0 };
        container.Children.Add(scrim);
        container.Children.Add(card);

        var window = new Window { Width = 400, Height = 300, Content = container };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Motion.SetEnabled(true);
            var controller = new OverlayTransitionController();

            controller.Open(container, scrim, card);
            Assert.True(container.IsVisible);
            Assert.True(container.IsHitTestVisible);

            // 启动异步退场，但在动画中途立刻重新 Open
            var closeTask = controller.CloseAsync(container, scrim, card);
            Assert.False(container.IsHitTestVisible);

            await Task.Delay(25);
            controller.Open(container, scrim, card);

            // 等待前一次 CloseAsync 结束与新一轮 Open 动画跑完
            await closeTask;
            await Task.Delay(280);
            Dispatcher.UIThread.RunJobs();

            Assert.True(container.IsVisible, "新一轮 Open 后旧的 CloseAsync 不得误关容器");
            Assert.True(container.IsHitTestVisible);
            Assert.Equal(1d, card.Opacity, 2);
            Assert.Null(card.RenderTransform);
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task OverlayTransitionController_WhenMotionDisabled_ClosesSynchronouslyWithCallback()
    {
        var container = new Grid { IsVisible = false };
        var scrim = new Border { Opacity = 0 };
        var card = new Border { Width = 160, Height = 100, Opacity = 0 };
        container.Children.Add(scrim);
        container.Children.Add(card);

        var window = new Window { Width = 300, Height = 200, Content = container };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Motion.SetEnabled(false);
            var controller = new OverlayTransitionController();

            controller.Open(container, scrim, card);
            Assert.True(container.IsVisible);
            Assert.Equal(1d, scrim.Opacity);
            Assert.Equal(1d, card.Opacity);

            var hiddenCallbackInvoked = false;
            await controller.CloseAsync(container, scrim, card, onHidden: () => hiddenCallbackInvoked = true);

            Assert.True(hiddenCallbackInvoked);
            Assert.False(container.IsVisible);
            Assert.False(container.IsHitTestVisible);
            Assert.Equal(0d, scrim.Opacity);
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TabSwitch_UsesRenderTransformAndKeepsZeroMargin()
    {
        var tabSwitch = new TabSwitch
        {
            IsShowIcon = false,
            Options =
            [
                new SelectItemModel { Name = "日" },
                new SelectItemModel { Name = "周" },
                new SelectItemModel { Name = "月" }
            ],
            SelectedIndex = 0
        };

        var window = new Window
        {
            Width = 360,
            Height = 120,
            Content = new StackPanel { Children = { tabSwitch } }
        };

        try
        {
            Motion.SetEnabled(true);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            tabSwitch.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();

            var slider = tabSwitch.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Slider");
            Assert.Equal(new Thickness(0), slider.Margin);
            Assert.True(slider.IsVisible);
            Assert.NotNull(slider.RenderTransform);
            var txAnimated = Assert.IsType<TransformOperations>(slider.RenderTransform).Value.M31;
            Assert.True(txAnimated > 0, $"开启动效切换后滑块应当平移到第 3 项，实际 translateX={txAnimated}");

            Motion.SetEnabled(false);
            tabSwitch.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var txInstant = Assert.IsType<TransformOperations>(slider.RenderTransform).Value.M31;
            Assert.Equal(new Thickness(0), slider.Margin);
            Assert.True(txInstant > 0 && txInstant < txAnimated,
                $"切到第 2 项时 translateX={txInstant} 应位于 (0, {txAnimated})");
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [Fact]
    public void PopupMotion_ResolveEntryOffset_MatchesPlacementDirections()
    {
        var (tx, ty, topOrigin) = PopupMotion.ResolveEntryOffset(PlacementMode.Top);
        Assert.Equal(0d, tx);
        Assert.Equal(PopupMotion.OffsetPx, ty);
        Assert.Equal(1.0, topOrigin.Point.Y);

        var (bx, by, botOrigin) = PopupMotion.ResolveEntryOffset(PlacementMode.Bottom);
        Assert.Equal(0d, bx);
        Assert.Equal(-PopupMotion.OffsetPx, by);
        Assert.Equal(0.0, botOrigin.Point.Y);

        var (lx, ly, leftOrigin) = PopupMotion.ResolveEntryOffset(PlacementMode.Left);
        Assert.Equal(PopupMotion.OffsetPx, lx);
        Assert.Equal(0d, ly);
        Assert.Equal(1.0, leftOrigin.Point.X);

        var (rx, ry, rightOrigin) = PopupMotion.ResolveEntryOffset(PlacementMode.Right);
        Assert.Equal(-PopupMotion.OffsetPx, rx);
        Assert.Equal(0d, ry);
        Assert.Equal(0.0, rightOrigin.Point.X);
    }

    [AvaloniaFact]
    public void PopupMotion_WhenMotionDisabled_OpensPopupVisibleWithZeroTransform()
    {
        PopupMotion.EnsureRegistered();

        var child = new Border { Width = 80, Height = 40 };
        var popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            Child = child
        };
        var window = new Window { Width = 300, Height = 300, Content = popup };

        try
        {
            Motion.SetEnabled(false);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            popup.IsOpen = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1d, child.Opacity);
            Assert.Null(child.RenderTransform);
        }
        finally
        {
            popup.IsOpen = false;
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PressedState_TogglesPressedPseudoClassOnCustomControls()
    {
        PressedState.EnsureRegistered();

        var iconBtn = new IconButton { Width = 36, Height = 36 };
        var navItem = new NavigationItem { Title = "测试", Width = 140 };
        var panel = new StackPanel { Spacing = 8, Children = { iconBtn, navItem } };
        var window = new Window { Width = 240, Height = 160, Content = panel };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var btnPt = iconBtn.TranslatePoint(new Point(18, 18), window)!.Value;
            window.MouseDown(btnPt, MouseButton.Left);
            Assert.Contains(":pressed", iconBtn.Classes);
            window.MouseUp(btnPt, MouseButton.Left);
            Assert.DoesNotContain(":pressed", iconBtn.Classes);

            var navPt = navItem.TranslatePoint(new Point(40, 18), window)!.Value;
            window.MouseDown(navPt, MouseButton.Left);
            Assert.Contains(":pressed", navItem.Classes);
            window.MouseUp(navPt, MouseButton.Left);
            Assert.DoesNotContain(":pressed", navItem.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SettingPage_RapidTabSwitchingWhenMotionDisabled_LeavesOnlyTargetVisible()
    {
        var vm = CreateSettingVm();
        var page = new SettingPage(vm);
        var window = new Window { Width = 900, Height = 700, Content = page };

        try
        {
            Motion.SetEnabled(false);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.TabbarSelectedIndex = 2;
            vm.TabbarSelectedIndex = 4;
            vm.TabbarSelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            var expected = page.FindControl<StackPanel>("TabPanelAppearance")!;
            string[] names = ["TabPanelGeneral", "TabPanelAppearance", "TabPanelTracking", "TabPanelData", "TabPanelAbout"];
            foreach (var name in names)
            {
                var tab = page.FindControl<StackPanel>(name)!;
                Assert.Equal(ReferenceEquals(tab, expected), tab.IsVisible);
                Assert.Equal(1d, tab.Opacity);
            }
        }
        finally
        {
            Motion.SetEnabled(true);
            window.Close();
        }
    }

    private static SettingPageViewModel CreateSettingVm()
    {
        var config = new ConfigModel();
        var appConfig = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IAppConfig>.Create(new Dictionary<string, Func<object?[], object?>>
        {
            [nameof(ShiJian.Client.Shared.Servicers.Interfaces.IAppConfig.GetConfig)] = _ => config,
            [nameof(ShiJian.Client.Shared.Servicers.Interfaces.IAppConfig.SaveAsync)] = _ => Task.CompletedTask
        });
        var toast = StubProxy<ShiJian.Client.Servicers.Interfaces.IToastService>.Create([]);
        var data = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IData>.Create([]);
        var webData = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IWebData>.Create([]);
        var appData = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IAppData>.Create([]);
        var webSiteData = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IWebSiteData>.Create([]);
        var dialog = StubProxy<ShiJian.Client.Servicers.Interfaces.IDialogService>.Create([]);
        var process = StubProxy<ShiJian.Client.Servicers.Interfaces.IProcessService>.Create([]);
        var uiServicer = StubProxy<ShiJian.Client.Servicers.IUIServicer>.Create([]);
        var updater = new UpdateCheckerService(uiServicer, null!);
        var merge = StubProxy<ShiJian.Client.Servicers.Interfaces.IAppMergeService>.Create([]);
        var entityData = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IEntityData>.Create([]);
        var nav = StubProxy<ShiJian.Client.Servicers.Interfaces.INavigationService>.Create([]);
        var state = StubProxy<ShiJian.Client.Servicers.Interfaces.IStateService>.Create([]);
        var apiClient = StubProxy<ShiJian.Client.Librarys.Api.IShiJianApiClient>.Create([]);

        return new SettingPageViewModel(
            appConfig, toast, data, webData, appData, webSiteData,
            dialog, process, updater, merge, entityData, nav, state, apiClient);
    }

    private class StubProxy<T> : DispatchProxy where T : class
    {
        private Dictionary<string, Func<object?[], object?>> _handlers = [];

        public static T Create(Dictionary<string, Func<object?[], object?>> handlers)
        {
            var proxy = DispatchProxy.Create<T, StubProxy<T>>();
            ((StubProxy<T>)(object)proxy)._handlers = handlers;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod != null && _handlers.TryGetValue(targetMethod.Name, out var fn))
                return fn(args ?? []);
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType == typeof(Task))
                return Task.CompletedTask;
            if (targetMethod?.ReturnType.IsValueType == true)
                return Activator.CreateInstance(targetMethod.ReturnType);
            return null;
        }
    }
}
