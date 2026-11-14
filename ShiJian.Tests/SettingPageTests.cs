using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShiJian.Client;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.SettingPanel;
using ShiJian.Client.Librarys;
using ShiJian.Client.Models;
using ShiJian.Client.Platform.Abstractions.Primitives;
using ShiJian.Client.Servicers.Updater;
using ShiJian.Client.Shared.Helpers;
using ShiJian.Client.Shared.Models.Config;
using Xunit;

namespace ShiJian.Tests;

public class SettingPageTests
{
    [Fact]
    public void AppVersionHelper_PreservesForkSuffixAndNormalizesTag()
    {
        var displayVersion = AppVersionHelper.GetDisplayVersion(typeof(AppVersionHelper).Assembly);
        Assert.False(string.IsNullOrWhiteSpace(displayVersion));
        Assert.DoesNotContain('+', displayVersion);

        Assert.Equal("1.3.4-fork.11", AppVersionHelper.NormalizeTagVersion("v1.3.4-fork.11"));
        Assert.Equal("2.0.0-dev", AppVersionHelper.NormalizeTagVersion("V2.0.0-dev"));
        Assert.Equal(string.Empty, AppVersionHelper.NormalizeTagVersion("   "));
    }

    [Fact]
    public void GithubRelease_IsCanUpdate_HandlesNormalizedVersionsAndEmptyInfo()
    {
        var same = new GithubRelease("https://example.invalid", "1.3.4-fork.11");
        same.Info.Version = "1.3.4-fork.11";
        Assert.False(same.IsCanUpdate());

        var sameWithVPrefix = new GithubRelease("https://example.invalid", "v1.3.4-fork.11");
        sameWithVPrefix.Info.Version = "1.3.4-fork.11";
        Assert.False(sameWithVPrefix.IsCanUpdate());

        var newer = new GithubRelease("https://example.invalid", "1.3.4-fork.11");
        newer.Info.Version = "1.3.4-fork.12";
        Assert.True(newer.IsCanUpdate());

        var emptyRemote = new GithubRelease("https://example.invalid", "1.3.4-fork.11");
        Assert.False(emptyRemote.IsCanUpdate());
    }

    [Fact]
    public void SettingPageModel_ClampsTabIndexAndUpdatesActiveFlags()
    {
        var model = new SettingPageModel();
        var changedProps = new List<string>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        Assert.True(model.IsTab0Selected);
        Assert.False(model.IsTab3Selected);

        model.TabbarSelectedIndex = 3;
        Assert.Equal(3, model.TabbarSelectedIndex);
        Assert.False(model.IsTab0Selected);
        Assert.True(model.IsTab3Selected);
        Assert.Contains(nameof(SettingPageModel.IsTab3Selected), changedProps);

        model.TabbarSelectedIndex = 99;
        Assert.Equal(4, model.TabbarSelectedIndex);
        Assert.True(model.IsTab4Selected);

        model.TabbarSelectedIndex = -5;
        Assert.Equal(0, model.TabbarSelectedIndex);
        Assert.True(model.IsTab0Selected);
    }

    [Fact]
    public void GeneralAndBehaviorModels_MapEveryConfigToDiscreteTabsAndDropRetentionUi()
    {
        var validGeneralGroups = new HashSet<string>(StringComparer.Ordinal)
        {
            "基础", "Basics",
            "概览页", "Overview",
            "外观", "Appearance",
            "采集与统计", "Tracking"
        };
        var validBehaviorGroups = new HashSet<string>(StringComparer.Ordinal)
        {
            "监测偏好", "Monitoring",
            "忽略应用", "Ignore Application",
            "忽略URL", "Ignore URL",
            "应用白名单", "Application Whitelist"
        };

        foreach (var pi in typeof(GeneralModel).GetProperties())
        {
            var attrs = pi.GetCustomAttributes<ConfigAttribute>().ToList();
            if (pi.Name == nameof(GeneralModel.DataRetentionDays))
            {
                Assert.Empty(attrs);
                continue;
            }

            foreach (var attr in attrs)
            {
                Assert.Contains(attr.Group, validGeneralGroups);
            }
        }

        foreach (var pi in typeof(BehaviorModel).GetProperties())
        {
            foreach (var attr in pi.GetCustomAttributes<ConfigAttribute>())
            {
                Assert.Contains(attr.Group, validBehaviorGroups);
            }
        }
    }

    [AvaloniaFact]
    public void SettingPanel_IncludeGroups_FiltersRenderedGroupsAndPreservesSiblingProperties()
    {
        var originalLang = SystemLanguage.CurrentLanguage;
        SystemLanguage.CurrentLanguage = CultureCode.ZhCn;

        var config = new GeneralModel
        {
            Language = 1,
            ThemeColor = "#FF112233",
            Theme = 2
        };

        var basicPanel = new SettingPanel
        {
            IncludeGroups = "基础|Basics|概览页|Overview",
            Data = config
        };
        var appearancePanel = new SettingPanel
        {
            IncludeGroups = "外观|Appearance",
            Data = config
        };

        var window = new Window
        {
            Width = 600,
            Height = 600,
            Content = new StackPanel
            {
                Children = { basicPanel, appearancePanel }
            }
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var basicHeaders = basicPanel.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .ToList();
            Assert.Contains("基础", basicHeaders);
            Assert.Contains("概览页", basicHeaders);
            Assert.DoesNotContain("外观", basicHeaders);

            var appearanceHeaders = appearancePanel.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .ToList();
            Assert.Contains("外观", appearanceHeaders);
            Assert.DoesNotContain("基础", appearanceHeaders);

            // 深拷贝并写回同一份数据对象的某一项时，不应破坏另一分页持有的字段值
            var updated = Assert.IsType<GeneralModel>(SettingPanel.DeepCopy(appearancePanel.Data, typeof(GeneralModel)));
            updated.Theme = 1;
            basicPanel.Data = updated;
            appearancePanel.Data = updated;

            var latestBasic = Assert.IsType<GeneralModel>(basicPanel.Data);
            Assert.Equal("#FF112233", latestBasic.ThemeColor);
            Assert.Equal(1, latestBasic.Language);
            Assert.Equal(1, latestBasic.Theme);
        }
        finally
        {
            SystemLanguage.CurrentLanguage = originalLang;
            window.Close();
        }
    }

    [Fact]
    public void SettingPageModel_WebDavPropertiesAndBusyState_NotifyProperly()
    {
        var model = new SettingPageModel();
        var changed = new List<string>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) changed.Add(e.PropertyName);
        };

        Assert.True(model.WebDavNotBusy);
        model.WebDavBusy = true;
        Assert.False(model.WebDavNotBusy);
        Assert.Contains(nameof(SettingPageModel.WebDavBusy), changed);
        Assert.Contains(nameof(SettingPageModel.WebDavNotBusy), changed);

        Assert.False(model.HasWebDavRemoteBackups);
        model.WebDavRemoteBackups =
        [
            new ShiJian.Client.Shared.Models.Data.RemoteBackupItem
            {
                FileName = "shijian-20261114.shijian-backup",
                SizeBytes = 2048,
                LastModified = "2026-11-14",
                DeviceName = "DEV-PC"
            }
        ];
        Assert.True(model.HasWebDavRemoteBackups);
    }

    [Fact]
    public async Task SettingPageViewModel_ExportBackup_WritesArchiveInsteadOfCsv()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shijian-test-{Guid.NewGuid():N}.shijian-backup");
        var csvDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shijian-csv-{Guid.NewGuid():N}");
        byte[] expectedBytes = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];
        bool csvDataCalled = false;
        bool csvWebCalled = false;
        bool savePickerCalled = false;

        try
        {
            using var vm = CreateViewModel(
                dialogHandlers: new()
                {
                    [nameof(ShiJian.Client.Servicers.Interfaces.IDialogService.ShowSaveFilePickerAsync)] = _ =>
                    {
                        savePickerCalled = true;
                        return Task.FromResult<string?>(tempFile);
                    },
                    [nameof(ShiJian.Client.Servicers.Interfaces.IDialogService.ShowFolderPickerAsync)] = _ => Task.FromResult<string?>(csvDir)
                },
                dataHandlers: new()
                {
                    [nameof(ShiJian.Client.Shared.Servicers.Interfaces.IData.ExportToExcelAsync)] = _ =>
                    {
                        csvDataCalled = true;
                        return Task.CompletedTask;
                    }
                },
                webDataHandlers: new()
                {
                    [nameof(ShiJian.Client.Shared.Servicers.Interfaces.IWebData.ExportAsync)] = _ =>
                    {
                        csvWebCalled = true;
                        return Task.CompletedTask;
                    }
                },
                apiHandlers: new()
                {
                    [nameof(ShiJian.Client.Librarys.Api.IShiJianApiClient.ExportBackupAsync)] = _ =>
                        Task.FromResult((expectedBytes, "backup.shijian-backup"))
                });

            vm.ExportDataCommand.Execute(null);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.True(savePickerCalled);
            Assert.False(csvDataCalled);
            Assert.False(csvWebCalled);
            Assert.True(System.IO.File.Exists(tempFile));
            Assert.Equal(expectedBytes, await System.IO.File.ReadAllBytesAsync(tempFile, TestContext.Current.CancellationToken));

            vm.ExportCsvCommand.Execute(null);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(csvDataCalled);
            Assert.True(csvWebCalled);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [AvaloniaFact]
    public void SettingPage_DataTabAndModals_RenderBackupWebDavAndSaveButton()
    {
        using var vm = CreateViewModel();
        vm.TabbarSelectedIndex = 3;
        vm.MergeEditorVisible = true;
        vm.WebDavRemoteModalVisible = true;
        vm.WebDavRemoteBackups =
        [
            new ShiJian.Client.Shared.Models.Data.RemoteBackupItem
            {
                FileName = "shijian-20261114.shijian-backup",
                SizeBytes = 4096,
                LastModified = "2026-11-14T03:00:00Z",
                DeviceName = "TEST-PC"
            }
        ];

        var page = new ShiJian.Client.Views.SettingPage(vm);
        var window = new Window
        {
            Width = 1280,
            Height = 900,
            Content = page
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts = page.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            Assert.Contains("导出备份", texts);
            Assert.Contains("导入备份", texts);
            Assert.Contains("选择备份包", texts);
            Assert.Contains("导出 CSV", texts);
            Assert.Contains("WebDAV 云同步", texts);
            Assert.Contains("测试连接", texts);
            Assert.Contains("立即同步", texts);
            Assert.Contains("云端备份历史", texts);
            Assert.DoesNotContain("导出为csv格式", texts);

            var toggles = page.GetVisualDescendants()
                .OfType<ShiJian.Client.Controls.Toggle.Toggle>()
                .ToList();
            Assert.Contains(toggles, t => t.OnText == "开" && t.OffText == "关");

            // 弹窗内容现托管在 <base:PageModal> 的 ModalContent 中（关闭态未挂入可视化树，DynamicResource 尚未解析）；
            // 先收集页面上的弹窗，再挂载到同一 Window 下以触发 DynamicResource 解析和可视树布局。
            var modals = page.GetVisualDescendants().OfType<PageModal>().ToList();
            var pageButtons = page.GetVisualDescendants()
                .OfType<ShiJian.Client.Controls.Button.Button>()
                .Select(b => b.Content?.ToString())
                .ToList();
            var pageScrolls = page.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .Where(s => s.Name != "ContentScrollViewer")
                .ToList();

            var modalHost = new Panel { DataContext = vm };
            foreach (var modal in modals)
            {
                if (modal.ModalContent is not { } content) continue;
                modal.ClearValue(PageModal.ModalContentProperty);
                modalHost.Children.Add(content);
            }

            window.Content = modalHost;
            Dispatcher.UIThread.RunJobs();

            var buttonContents = pageButtons
                .Concat(modalHost.GetVisualDescendants()
                    .OfType<ShiJian.Client.Controls.Button.Button>()
                    .Select(b => b.Content?.ToString()))
                .ToList();
            Assert.Contains("保存", buttonContents);

            var modalScrolls = pageScrolls
                .Concat(modalHost.GetVisualDescendants().OfType<ScrollViewer>())
                .Distinct()
                .ToList();
            Assert.True(modalScrolls.Count >= 2);
            Assert.All(modalScrolls, sv => Assert.True(sv.ClipToBounds));
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void SettingPageViewModel_MergeGroupErrorClearsWhenTwoMembersSelected()
    {
        var apps = new IReadOnlyCollection<ShiJian.Client.Shared.Models.AppModel>[]
        {
            new List<ShiJian.Client.Shared.Models.AppModel>
            {
                new() { ID = 1, Name = "Code", Description = "Visual Studio Code", TotalTime = 1200 },
                new() { ID = 2, Name = "devenv", Description = "Visual Studio", TotalTime = 600 }
            }
        }[0];
        var vm = CreateViewModel(
            appDataHandlers: new Dictionary<string, Func<object?[], object?>>
            {
                [nameof(ShiJian.Client.Shared.Servicers.Interfaces.IAppData.GetAllAppsAsync)] =
                    _ => Task.FromResult<IReadOnlyCollection<ShiJian.Client.Shared.Models.AppModel>>(apps)
            },
            webSiteDataHandlers: new Dictionary<string, Func<object?[], object?>>
            {
                [nameof(ShiJian.Client.Shared.Servicers.Interfaces.IWebSiteData.GetAllWebSitesAsync)] =
                    _ => Task.FromResult<IReadOnlyCollection<ShiJian.Client.Shared.Models.Web.WebSiteModel>>([])
            });

        vm.OpenCreateMergeGroupCommand.Execute(null);
        Assert.True(vm.MergeEditorVisible);
        Assert.Equal(2, vm.MergeEditorCandidates.Count);

        vm.MergeEditorName = "IDE 套件";
        vm.ToggleMergeCandidateCommand.Execute(vm.MergeEditorCandidates[0]);
        vm.SaveMergeGroupCommand.Execute(null);

        Assert.True(vm.HasMergeEditorError);
        Assert.False(string.IsNullOrEmpty(vm.MergeEditorError));

        vm.ToggleMergeCandidateCommand.Execute(vm.MergeEditorCandidates[1]);

        Assert.Equal(2, vm.MergeEditorSelectedMembers.Count);
        Assert.False(vm.HasMergeEditorError);
        Assert.Equal(string.Empty, vm.MergeEditorError);
    }

    private static ShiJian.Client.ViewModels.SettingPageViewModel CreateViewModel(
        Dictionary<string, Func<object?[], object?>>? dialogHandlers = null,
        Dictionary<string, Func<object?[], object?>>? dataHandlers = null,
        Dictionary<string, Func<object?[], object?>>? webDataHandlers = null,
        Dictionary<string, Func<object?[], object?>>? apiHandlers = null,
        Dictionary<string, Func<object?[], object?>>? appDataHandlers = null,
        Dictionary<string, Func<object?[], object?>>? webSiteDataHandlers = null)
    {
        var config = new ConfigModel();
        var appConfig = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IAppConfig>.Create(new Dictionary<string, Func<object?[], object?>>
        {
            [nameof(ShiJian.Client.Shared.Servicers.Interfaces.IAppConfig.GetConfig)] = _ => config,
            [nameof(ShiJian.Client.Shared.Servicers.Interfaces.IAppConfig.SaveAsync)] = _ => Task.CompletedTask
        });
        var toast = StubProxy<ShiJian.Client.Servicers.Interfaces.IToastService>.Create(new());
        var data = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IData>.Create(dataHandlers ?? []);
        var webData = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IWebData>.Create(webDataHandlers ?? []);
        var appData = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IAppData>.Create(appDataHandlers ?? []);
        var webSiteData = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IWebSiteData>.Create(webSiteDataHandlers ?? []);
        var dialog = StubProxy<ShiJian.Client.Servicers.Interfaces.IDialogService>.Create(dialogHandlers ?? []);
        var process = StubProxy<ShiJian.Client.Servicers.Interfaces.IProcessService>.Create(new());
        var uiServicer = StubProxy<ShiJian.Client.Servicers.IUIServicer>.Create(new());
        var updater = new UpdateCheckerService(uiServicer, null!);
        var merge = StubProxy<ShiJian.Client.Servicers.Interfaces.IAppMergeService>.Create(new());
        var entityData = StubProxy<ShiJian.Client.Shared.Servicers.Interfaces.IEntityData>.Create(new());
        var nav = StubProxy<ShiJian.Client.Servicers.Interfaces.INavigationService>.Create(new());
        var state = StubProxy<ShiJian.Client.Servicers.Interfaces.IStateService>.Create(new());
        var apiClient = StubProxy<ShiJian.Client.Librarys.Api.IShiJianApiClient>.Create(apiHandlers ?? []);

        return new ShiJian.Client.ViewModels.SettingPageViewModel(
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
