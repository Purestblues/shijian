using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using ShiJian.Client.Controls.Charts;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Controls.Timeline;
using ShiJian.Client.Events;
using ShiJian.Client.Models.Navigation;
using ShiJian.Client.Servicers;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Shared.Models.Entity;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.ViewModels;
using ShiJian.Client.Views;
using Xunit;
using AppColors = ShiJian.Client.Base.Color.Colors;
using CategoryCardModelItem = ShiJian.Client.Models.Category.CategoryModel;

namespace ShiJian.Tests;

public class N16ToN21Tests
{
    [Fact]
    public void N16_ResolvePeriodRange_StepsHistoryAndLocksFuture()
    {
        var today = new DateTime(2026, 11, 19); // Thursday

        var currentDay = IndexPageViewModel.ResolvePeriodRange(0, 0, today, today, today);
        Assert.Equal(today, currentDay.Start);
        Assert.Equal(today, currentDay.End);
        Assert.Equal(today.AddDays(-1), currentDay.PrevStart);
        Assert.True(currentDay.IsHourly);
        Assert.False(currentDay.CanPageForward);

        var yesterday = IndexPageViewModel.ResolvePeriodRange(0, -1, today, today, today);
        Assert.Equal(new DateTime(2026, 11, 18), yesterday.Start);
        Assert.Equal(new DateTime(2026, 11, 17), yesterday.PrevStart);
        Assert.True(yesterday.CanPageForward);

        var thisWeek = IndexPageViewModel.ResolvePeriodRange(1, 0, today, today, today);
        Assert.Equal(new DateTime(2026, 11, 16), thisWeek.Start);
        Assert.Equal(new DateTime(2026, 11, 22), thisWeek.End);
        Assert.False(thisWeek.IsHourly);
        Assert.False(thisWeek.CanPageForward);

        var lastWeek = IndexPageViewModel.ResolvePeriodRange(1, -1, today, today, today);
        Assert.Equal(new DateTime(2026, 11, 9), lastWeek.Start);
        Assert.Equal(new DateTime(2026, 11, 15), lastWeek.End);
        Assert.Equal(new DateTime(2026, 11, 2), lastWeek.PrevStart);
        Assert.True(lastWeek.CanPageForward);

        var (shiftedBackStart, shiftedBackEnd, canForward) = IndexPageViewModel.ShiftCustomRange(
            new DateTime(2026, 11, 17),
            new DateTime(2026, 11, 19),
            delta: -1,
            today);
        Assert.Equal(new DateTime(2026, 11, 14), shiftedBackStart);
        Assert.Equal(new DateTime(2026, 11, 16), shiftedBackEnd);
        Assert.True(canForward);

        var (clampedStart, clampedEnd, canForwardAfterClamp) = IndexPageViewModel.ShiftCustomRange(
            new DateTime(2026, 11, 17),
            new DateTime(2026, 11, 19),
            delta: 1,
            today);
        Assert.Equal(new DateTime(2026, 11, 17), clampedStart);
        Assert.Equal(today, clampedEnd);
        Assert.False(canForwardAfterClamp);
    }

    [Fact]
    public void N17_UsageRanking_FilterAndSortRows_FiltersByKindKeywordAndOrdersDescending()
    {
        var rows = new List<EntityModel>
        {
            new() { Key = "app:1", Kind = "app", Name = "VS Code", Seconds = 1800, CategoryName = "开发" },
            new() { Key = "site:2", Kind = "site", Name = "github.com", Seconds = 2400, CategoryName = "开发" },
            new()
            {
                Key = "app:3",
                Kind = "app",
                Name = "JetBrains",
                Seconds = 900,
                CategoryName = "开发",
                Members = [new EntityMemberModel { Key = "app:30", Name = "Rider", Seconds = 900 }]
            },
            new() { Key = "site:4", Kind = "site", Name = "bilibili.com", Seconds = 600, CategoryName = "娱乐" }
        };

        var all = UsageRankingPageViewModel.FilterAndSortRows(rows, UsageRankingFilter.All, null);
        Assert.Equal(["github.com", "VS Code", "JetBrains", "bilibili.com"], all.Select(r => r.Name));

        var appsOnly = UsageRankingPageViewModel.FilterAndSortRows(rows, UsageRankingFilter.Apps, null);
        Assert.Equal(["VS Code", "JetBrains"], appsOnly.Select(r => r.Name));

        var sitesOnly = UsageRankingPageViewModel.FilterAndSortRows(rows, UsageRankingFilter.Sites, null);
        Assert.Equal(["github.com", "bilibili.com"], sitesOnly.Select(r => r.Name));

        var memberSearch = UsageRankingPageViewModel.FilterAndSortRows(rows, UsageRankingFilter.All, "rider");
        Assert.Single(memberSearch);
        Assert.Equal("JetBrains", memberSearch[0].Name);
    }

    [AvaloniaFact]
    public void N18_CategoryPage_GotoMembersCommand_And_BackNavigation()
    {
        string? navigatedUri = null;
        object? navigatedData = null;
        var goBackCount = 0;

        var navService = StubProxy<INavigationService>.Create(new()
        {
            [nameof(INavigationService.NavigateTo)] = args =>
            {
                navigatedUri = args[0] as string;
                navigatedData = args[1];
                return null;
            },
            [nameof(INavigationService.GoBack)] = _ =>
            {
                goBackCount++;
                return null;
            }
        });

        var categoryVm = new CategoryPageViewModel(
            StubProxy<ICategorys>.Create(new()),
            navService,
            StubProxy<IAppData>.Create(new()),
            StubProxy<IData>.Create(new()),
            StubProxy<IEntityData>.Create(new()),
            StubProxy<IWebData>.Create(new()),
            StubProxy<IWebSiteData>.Create(new()),
            StubProxy<IAppUpdateService>.Create(new()),
            StubProxy<IAppMergeService>.Create(new()),
            StubProxy<IDialogService>.Create(new()),
            StubProxy<IToastService>.Create(new()),
            StubProxy<IStateService>.Create(new()));

        var membersVm = new CategoryMembersPageViewModel(
            StubProxy<IAppData>.Create(new()),
            StubProxy<IWebData>.Create(new()),
            StubProxy<ICategorys>.Create(new()),
            StubProxy<INavigationDataService>.Create(new()),
            navService,
            StubProxy<IAppUpdateService>.Create(new()),
            new AppEventService(),
            StubProxy<IAppMergeService>.Create(new()));

        try
        {
            var card = new CategoryCardModelItem
            {
                Data = new ShiJian.Client.Shared.Models.CategoryModel { ID = 7, Name = "开发" }
            };

            categoryVm.GotoMembersCommand.Execute(card);
            Assert.Equal(nameof(CategoryMembersPage), navigatedUri);
            Assert.Same(card, navigatedData);

            membersVm.GoBackCommand.Execute(null);
            Assert.Equal(1, goBackCount);
        }
        finally
        {
            categoryVm.Dispose();
            membersVm.Dispose();
        }
    }

    [Fact]
    public void N19_RadarChartView_AlignSeries_AlignsCategoriesAndSharedMax()
    {
        var current = new List<ChartsDataModel>
        {
            new() { Name = "开发", Value = 1200 },
            new() { Name = "社交", Value = 600 },
            new() { Name = "娱乐", Value = 300 }
        };
        var previous = new List<ChartsDataModel>
        {
            new() { Name = "娱乐", Value = 1800 },
            new() { Name = "开发", Value = 900 },
            new() { Name = "已忽略分类", Value = 5000 }
        };

        var (currVals, compVals, maxVal) = RadarChartView.AlignSeries(current, previous);

        Assert.Equal([1200d, 600d, 300d], currVals);
        Assert.Equal([900d, 0d, 1800d], compVals);
        Assert.Equal(1800d, maxVal);
    }

    [Fact]
    public void N19_IndexPage_BuildCategoryShareSeries_ComputesDualBarRatiosAndCompareColor()
    {
        var current = new List<ChartsDataModel>
        {
            new() { Name = "娱乐", Value = 600, Tag = "10m", Color = "#4D968E" },
            new() { Name = "工具", Value = 1200, Tag = "20m", Color = "#527CE6" }
        };
        var prev = new List<ChartsDataModel>
        {
            new() { Name = "工具", Value = 2400, Tag = "40m", Color = "#527CE6" },
            new() { Name = "娱乐", Value = 300, Tag = "5m", Color = "#4D968E" }
        };

        var result = IndexPageViewModel.BuildCategoryShareSeries(current, prev);

        Assert.Equal(["工具", "娱乐"], result.Select(x => x.Name));
        Assert.Equal(0.5, result[0].Ratio, 3);
        Assert.Equal(1.0, result[0].CompareRatio, 3);
        Assert.StartsWith("#4D", result[0].CompareColor, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0.25, result[1].Ratio, 3);
        Assert.Equal(0.125, result[1].CompareRatio, 3);
        Assert.StartsWith("#4D", result[1].CompareColor, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void N19_RadarChart_RendersWithComparePolygonInHeadlessWindow()
    {
        var radar = new RadarChart
        {
            Width = 260,
            Height = 260,
            Labels = ["开发", "社交", "娱乐", "办公"],
            Values = [1200, 800, 400, 600],
            CompareValues = [900, 1000, 500, 300],
            MaxValue = 1200
        };

        var window = new Window { Width = 300, Height = 300, Content = radar };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(radar.Bounds.Width > 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void N20_CategoryCardModel_AccentTintColor_And_ClockRingThickness()
    {
        var card = new CategoryCardModelItem
        {
            Data = new ShiJian.Client.Shared.Models.CategoryModel
            {
                ID = 1,
                Name = "开发",
                Color = "#527CE6"
            }
        };

        Assert.StartsWith("#24", card.AccentTintColor, StringComparison.OrdinalIgnoreCase);
        Assert.True(Color.TryParse(card.AccentTintColor, out var parsedTint));
        Assert.Equal(0x24, parsedTint.A);

        var ring = new ClockRing
        {
            Width = 320,
            Height = 320,
            Thickness = 42
        };
        var window = new Window { Width = 360, Height = 360, Content = ring };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(42, ring.Thickness);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void N21_MainColors_Has24UniqueValidPaletteColors()
    {
        var palette = AppColors.MainColors;

        Assert.Equal(24, palette.Length);
        Assert.Equal("#527CE6", palette[0]);
        Assert.Equal("#4C566A", palette[11]);

        var distinct = new HashSet<string>(palette, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(24, distinct.Count);

        foreach (var hex in palette)
        {
            Assert.StartsWith("#", hex);
            Assert.Equal(7, hex.Length);
            Assert.True(Color.TryParse(hex, out _), $"Invalid hex color: {hex}");
        }
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
            if (targetMethod?.ReturnType is { IsGenericType: true } rt && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = rt.GetGenericArguments()[0];
                var defaultVal = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [defaultVal]);
            }
            if (targetMethod?.ReturnType.IsValueType == true)
                return Activator.CreateInstance(targetMethod.ReturnType);
            return null;
        }
    }
}
