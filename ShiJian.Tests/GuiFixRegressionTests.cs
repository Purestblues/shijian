using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShiJian.Client;
using ShiJian.Client.Behaviors;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Charts;
using ShiJian.Client.Controls.Charts.Model;
using ShiJian.Client.Controls.Select;
using ShiJian.Client.Controls.Timeline;
using ShiJian.Client.Librarys;
using ShiJian.Client.Librarys.Api;
using ShiJian.Client.Librarys.Image;
using ShiJian.Client.Models;
using ShiJian.Client.Resources.Localization;
using ShiJian.Client.Servicers;
using ShiJian.Client.Servicers.Interfaces;
using ShiJian.Client.Shared.Models;
using ShiJian.Client.Shared.Models.Config;
using ShiJian.Client.Shared.Models.Entity;
using ShiJian.Client.Shared.Models.Search;
using ShiJian.Client.Shared.Models.Web;
using ShiJian.Client.Shared.Servicers.Interfaces;
using ShiJian.Client.ViewModels;
using ShiJian.Client.Views;
using Xunit;

namespace ShiJian.Tests;

public class GuiFixRegressionTests
{
    [Fact]
    public void IndexPageViewModel_BuildOverviewRankings_HandlesEmptyAndShortListsWithoutThrow()
    {
        var shortApps = new List<ChartsDataModel>
        {
            new() { Name = "Terminal", Value = 1800 }
        };
        var webs = new List<ChartsDataModel>
        {
            new() { Name = "GitHub", Value = 1500 },
            new() { Name = "Docs", Value = 1200 },
            new() { Name = "Bilibili", Value = 900 },
            new() { Name = "Zhihu", Value = 300 }
        };

        var (appFreq, webFreq, more) = IndexPageViewModel.BuildOverviewRankings(shortApps, webs, frequentUseNum: 2, moreNum: 11);

        Assert.Single(appFreq);
        Assert.Equal("Terminal", appFreq[0].Name);
        Assert.Equal(2, webFreq.Count);
        Assert.Equal(["Bilibili", "Zhihu"], more.Select(m => m.Name));

        var (appOnly, webEmpty, moreApps) = IndexPageViewModel.BuildOverviewRankings(
            [
                new() { Name = "A1", Value = 500 },
                new() { Name = "A2", Value = 400 },
                new() { Name = "A3", Value = 300 },
                new() { Name = "A4", Value = 100 }
            ],
            [],
            frequentUseNum: 2,
            moreNum: 2);

        Assert.Equal(2, appOnly.Count);
        Assert.Empty(webEmpty);
        Assert.Equal(["A3", "A4"], moreApps.Select(m => m.Name));

        var (negApp, negWeb, negMore) = IndexPageViewModel.BuildOverviewRankings(shortApps, webs, frequentUseNum: -2, moreNum: -1);
        Assert.Empty(negApp);
        Assert.Empty(negWeb);
        Assert.Empty(negMore);
    }

    [AvaloniaFact]
    public void ColumnChart_StackedMultiRowCategoryLegend_DoesNotClipBelowMinHeight()
    {
        var data = Enumerable.Range(1, 14)
            .Select(i => new ChartsDataModel
            {
                Name = $"分类条目{i:D2}",
                Color = i % 2 == 0 ? "#FF4A90E2" : "#FF2ECC71",
                Values = [100 + i * 10, 80, 120, 60, 90, 70, 50]
            })
            .ToList();

        var chart = new ColumnChart
        {
            IsStack = true,
            IsShowCategory = true,
            MinHeight = 310,
            Width = 480,
            Data = data
        };

        var window = new Window
        {
            Width = 600,
            Height = 900,
            Content = new StackPanel { Children = { chart } }
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(600, 900));
            window.Arrange(new Rect(0, 0, 600, 900));
            Dispatcher.UIThread.RunJobs();

            var legendGrid = chart.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "CategoryLegend");
            var chartBorder = chart.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ChartBorder");
            var wrapPanel = legendGrid.GetVisualDescendants().OfType<WrapPanel>().First();

            Assert.True(chart.Bounds.Height >= 310);
            Assert.True(chartBorder.Bounds.Height >= 230);
            Assert.True(legendGrid.Bounds.Height > 70, $"Expected multi-row legend height > 70, got {legendGrid.Bounds.Height}");
            Assert.True(wrapPanel.Bounds.Height <= legendGrid.Bounds.Height + 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void ColumnChartCanvas_ResolveReferenceLabelPositions_PreventsVerticalOverlapAndBottomOverflow()
    {
        var clustered = new[]
        {
            new ColumnChartCanvas.ReferenceLabelSlot(LineY: 100, Height: 14, Priority: 0),
            new ColumnChartCanvas.ReferenceLabelSlot(LineY: 102, Height: 14, Priority: 1),
            new ColumnChartCanvas.ReferenceLabelSlot(LineY: 104, Height: 14, Priority: 2)
        };

        var tops = ColumnChartCanvas.ResolveReferenceLabelPositions(clustered, minY: 0, maxY: 200, minGap: 2.0);
        Assert.Equal(3, tops.Length);
        var sorted = tops.OrderBy(t => t).ToArray();
        Assert.True(sorted[1] >= sorted[0] + 16.0 - 1e-6);
        Assert.True(sorted[2] >= sorted[1] + 16.0 - 1e-6);

        var bottomCluster = new[]
        {
            new ColumnChartCanvas.ReferenceLabelSlot(LineY: 194, Height: 14, Priority: 0),
            new ColumnChartCanvas.ReferenceLabelSlot(LineY: 197, Height: 14, Priority: 1),
            new ColumnChartCanvas.ReferenceLabelSlot(LineY: 200, Height: 14, Priority: 2)
        };

        var clamped = ColumnChartCanvas.ResolveReferenceLabelPositions(bottomCluster, minY: 0, maxY: 200, minGap: 2.0);
        Assert.All(clamped, top =>
        {
            Assert.True(top >= 0);
            Assert.True(top + 14 <= 200 + 1e-6);
        });
        var clampedSorted = clamped.OrderBy(t => t).ToArray();
        Assert.True(clampedSorted[1] >= clampedSorted[0] + 16.0 - 1e-6);
        Assert.True(clampedSorted[2] >= clampedSorted[1] + 16.0 - 1e-6);
    }

    [AvaloniaFact]
    public void HeatGrid_YearTwelveMonthHeatmap_PreservesReadableRowHeightInsideScrollViewer()
    {
        var measured = HeatGrid.MeasurePreferredSize(
            new Size(368, double.PositiveInfinity),
            rowCount: 12,
            columnCount: 31,
            maxCellHeight: 16,
            squareCells: false,
            rowLabelWidth: 28);

        Assert.True(measured.Height >= 12 * 14 + 11 * 2 + 18);
        Assert.Equal(1, HeatGrid.ComputeRowLabelStep(rowPitch: 16, labelHeight: 11));
        Assert.True(HeatGrid.ComputeRowLabelStep(rowPitch: 7, labelHeight: 11) >= 2);

        var rows = Enumerable.Range(1, 12)
            .Select(month => new HeatRow
            {
                Label = $"{month}月",
                Cells = Enumerable.Range(1, 31)
                    .Select(day => new HeatCell { Value = day * month * 60 })
                    .ToList()
            })
            .ToList();

        var grid = new HeatGrid
        {
            Rows = rows,
            ColumnLabels = ["1", "", "", "", "", "15", "", "", "", "", "31"],
            MaxValue = 12 * 31 * 60,
            RowLabelWidth = 28,
            MaxCellHeight = 16
        };

        var scrollViewer = new ScrollViewer
        {
            Width = 372,
            Height = 180,
            Content = grid
        };

        var window = new Window
        {
            Width = 420,
            Height = 300,
            Content = scrollViewer
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(grid.Bounds.Height >= 200, $"Expected 12-month HeatGrid height >= 200 inside ScrollViewer, got {grid.Bounds.Height}");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void CategorySearch_FiltersByTrimmedSubstringAliasAndDeduplicatesBlankEntries()
    {
        Assert.True(CategoryEntityViewHelper.MatchesSearchWithAlias("  git  ", "GitHub"));
        Assert.True(CategoryEntityViewHelper.MatchesSearchWithAlias("gh", "GitHub (gh)"));
        Assert.True(CategoryEntityViewHelper.MatchesSearchWithAlias("*.org", "docs.python.org"));
        Assert.False(CategoryEntityViewHelper.MatchesSearchWithAlias("*.com", "docs.python.org"));

        var unsetSites = new[]
        {
            new WebSiteModel { ID = 1, Title = " ", Domain = " " },
            new WebSiteModel { ID = 2, Title = "GitHub", Domain = "github.com", Alias = "代码托管", CategoryID = 0 },
            new WebSiteModel { ID = 3, Title = "example.com", Domain = "example.com", CategoryID = 0 }
        };
        var currentSites = new[]
        {
            new WebSiteModel { ID = 2, Title = "GitHub", Domain = "github.com", Alias = "代码托管", CategoryID = 5 },
            new WebSiteModel { ID = 4, Title = "Ant Design", Domain = "ant.design", CategoryID = 5 }
        };

        var siteOptions = CategoryWebSiteListPageViewModel.BuildChooserOptions(unsetSites, currentSites, categoryId: 5);
        Assert.Equal(3, siteOptions.Count);
        Assert.Equal("example.com", siteOptions.First(o => o.WebSite?.ID == 3).OptionValue.Name);

        var filteredSites = CategoryWebSiteListPageViewModel.FilterChooserOptions(siteOptions, "  代码 ");
        Assert.Single(filteredSites);
        Assert.Equal(2, filteredSites[0].WebSite?.ID);

        var sysCat = new CategoryModel { ID = 0, IsSystem = true };
        var apps = new[]
        {
            new AppModel { ID = 10, Name = "   ", CategoryID = 0, Category = sysCat, TotalTime = 100 },
            new AppModel { ID = 11, Name = "Code", Description = "Visual Studio Code", CategoryID = 0, Category = sysCat, TotalTime = 500 },
            new AppModel { ID = 12, Name = "Code", Description = "Visual Studio Code", CategoryID = 3, TotalTime = 100 },
            new AppModel { ID = 13, Name = "wt", Description = "Windows Terminal", Alias = "终端", CategoryID = 0, Category = sysCat, TotalTime = 800 }
        };

        var appOptions = CategoryAppListPageViewModel.BuildCandidateOptions(apps, categoryId: 3);
        Assert.Equal(2, appOptions.Count);
        Assert.Contains(appOptions, o => o.App.ID == 12 && o.IsChoosed);

        var filteredApps = CategoryAppListPageViewModel.FilterCandidateOptions(appOptions, " 终端 ");
        Assert.Single(filteredApps);
        Assert.Equal(13, filteredApps[0].App.ID);
    }

    [Fact]
    public void MainViewModel_SyncNavigationSelection_TracksNavSettingAndSubPages()
    {
        var appConfig = StubProxy<IAppConfig>.Create(new()
        {
            [nameof(IAppConfig.GetConfig)] = _ => new ConfigModel()
        });
        var vm = new MainViewModel(
            StubProxy<IServiceProvider>.Create(new()),
            appConfig,
            StubProxy<IShiJianApiClient>.Create(new()),
            StubProxy<IProcessService>.Create(new()));

        try
        {
            vm.Uri = nameof(IndexPage);
            Assert.Equal(nameof(IndexPage), vm.NavSelectedItem?.Uri);
            Assert.False(vm.IsSettingSelected);

            vm.Uri = nameof(SettingPage);
            Assert.Null(vm.NavSelectedItem);
            Assert.True(vm.IsSettingSelected);

            vm.Uri = nameof(DetailPage);
            Assert.Null(vm.NavSelectedItem);
            Assert.False(vm.IsSettingSelected);

            vm.Uri = nameof(ChartPage);
            Assert.Equal(nameof(ChartPage), vm.NavSelectedItem?.Uri);
            Assert.False(vm.IsSettingSelected);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void DateSelect_InsideInitiallyHiddenContainer_OpensPopupWhenCenterClicked()
    {
        var dateSelect = new DateSelect
        {
            SelectType = DateSelectType.Date,
            Date = new DateTime(2026, 11, 14)
        };
        var host = new Border
        {
            IsVisible = false,
            Child = dateSelect
        };
        var window = new Window
        {
            Width = 500,
            Height = 300,
            Content = host
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            host.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(500, 300));
            window.Arrange(new Rect(0, 0, 500, 300));
            Dispatcher.UIThread.RunJobs();

            var triggerButton = dateSelect.GetVisualDescendants()
                .OfType<Button>()
                .First(b => ReferenceEquals(b.Command, dateSelect.ShowSelectCommand));

            triggerButton.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dateSelect.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CategoryEntityRowAndImager_ProvideFallbackTextAndCacheInvalidFaviconsAcrossWidths()
    {
        var siteRow = new CategoryEntityRow
        {
            IsSite = true,
            Name = "GitHub Docs",
            Subtitle = "docs.github.com"
        };
        var appRow = new CategoryEntityRow
        {
            IsSite = false,
            Name = "Visual Studio Code",
            Subtitle = "Code"
        };
        Assert.Equal("docs.github.com", siteRow.FallbackText);
        Assert.Equal("Visual Studio Code", appRow.FallbackText);

        Assert.True(Imager.IsSupportedImageHeader([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.True(Imager.IsSupportedImageHeader([0x00, 0x00, 0x01, 0x00, 0x01, 0x00]));
        Assert.False(Imager.IsSupportedImageHeader([0x00, 0x00, 0x01, 0x00, 0x00, 0x00]));
        Assert.False(Imager.IsSupportedImageHeader("<!DOCTYPE html><html>404</html>"u8));
        Assert.False(Imager.IsSupportedImageHeader("404 page not found."u8));
        Assert.False(Imager.IsSupportedImageHeader([]));

        var tempFile = Path.Combine(Path.GetTempPath(), $"shijian_bad_favicon_{Guid.NewGuid():N}.ico");
        try
        {
            await File.WriteAllTextAsync(tempFile, "<!DOCTYPE html><html><body>not an icon</body></html>");

            var bmp = await Imager.LoadAsync(tempFile, decodeWidth: 24);
            Assert.NotNull(bmp);
            Assert.True(Imager.IsFailed(tempFile, decodeWidth: 24));
            Assert.True(Imager.IsFailed(tempFile, decodeWidth: 64));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void LocalizationResources_ProvideChineseToggleTextAndNonDuplicatedSearchTips()
    {
        var cn = new StringResourcesCn();
        var en = new StringResourcesEn();

        Assert.Equal("开", cn["ToggleOn"]);
        Assert.Equal("关", cn["ToggleOff"]);
        Assert.Equal("On", en["ToggleOn"]);
        Assert.Equal("Off", en["ToggleOff"]);

        var cnNav = Assert.IsType<string>(cn["SearchTipNav"]);
        var cnClose = Assert.IsType<string>(cn["SearchTipClose"]);
        var enNav = Assert.IsType<string>(en["SearchTipNav"]);
        var enClose = Assert.IsType<string>(en["SearchTipClose"]);

        Assert.DoesNotContain("↑↓", cnNav);
        Assert.DoesNotContain("Esc", cnClose);
        Assert.DoesNotContain("↑↓", enNav);
        Assert.DoesNotContain("Esc", enClose);
    }

    [Fact]
    public void AdaptiveWrapBehavior_ComputeItemWidth_FillsWidthWithoutTrailingGap()
    {
        Assert.True(double.IsNaN(AdaptiveWrapBehavior.ComputeItemWidth(0, 188)));
        Assert.True(double.IsNaN(AdaptiveWrapBehavior.ComputeItemWidth(double.NaN, 188)));

        foreach (var width in new[] { 300d, 640d, 1200d, 1440d, 1900d, 2500d })
        {
            var itemWidth = AdaptiveWrapBehavior.ComputeItemWidth(width, 188);
            var columns = Math.Max(1, (int)Math.Floor(width / 188));
            Assert.True(itemWidth >= 188 || width < 188);
            Assert.True(columns * itemWidth <= width);
            Assert.True(width - columns * itemWidth <= columns + 1);
        }
    }

    [Fact]
    public void ChartPageModel_ResponsiveWidths_BalanceTwoRowAndThreeColumnModes()
    {
        var model = new ChartPageModel { RowWidth = 1400 };
        Assert.Equal(350, model.RadarChartWidth);
        Assert.Equal(320, model.TopListWidth);
        Assert.Equal(1400 - (350 + 320 + 40), model.ColumnChartWidth);

        model.RowWidth = 1100;
        Assert.Equal(1080, model.ColumnChartWidth);
        Assert.True(model.RadarChartWidth > 350);
        Assert.True(model.TopListWidth > 320);
        Assert.Equal(1100 - 30, model.RadarChartWidth + model.TopListWidth);

        model.RowWidth = 600;
        Assert.Equal(580, model.ColumnChartWidth);
        Assert.Equal(580, model.RadarChartWidth);
        Assert.Equal(580, model.TopListWidth);
    }

    [Fact]
    public void SearchPaletteViewModel_ResetForReopen_ClearsKeywordAndSelection()
    {
        var searchService = StubProxy<ISearchService>.Create(new()
        {
            [nameof(ISearchService.GetTodayHighlightsAsync)] = _ => Task.FromResult<IReadOnlyList<SearchResultItem>>([]),
            [nameof(ISearchService.SearchAsync)] = _ => Task.FromResult<IReadOnlyList<SearchResultItem>>([])
        });

        var vm = new SearchPaletteViewModel(searchService)
        {
            Keyword = "chrome",
            SelectedItem = new SearchResultItem(SearchResultType.App, 1, "Chrome", "chrome", null, null)
        };

        try
        {
            vm.ResetForReopen();
            Assert.Equal(string.Empty, vm.Keyword);
            Assert.Null(vm.SelectedItem);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void OverlayDismissTracker_And_PageModal_DismissTopMostFirst()
    {
        OverlayDismissTracker.EnsureInitialized();

        var popup = new Popup { Child = new Border { Width = 60, Height = 30 } };
        var closedByCommand = false;
        var modal = new PageModal
        {
            IsOpen = true,
            ModalContent = new Border { Width = 120, Height = 80 },
            CloseCommand = new RelayActionCommand(() => closedByCommand = true)
        };
        var panel = new Grid();
        panel.Children.Add(popup);
        panel.Children.Add(modal);

        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = panel
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            popup.Open();
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            Assert.True(OverlayDismissTracker.HasOpenOverlay());

            // 浮层打开时，优先关闭浮层且不关闭 PageModal
            Assert.True(OverlayDismissTracker.DismissTopMost());
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            Assert.True(modal.IsOpen);
            Assert.Same(modal, PageModal.ActiveModal);

            modal.Dismiss();
            Assert.True(closedByCommand);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void DataPageViewModel_ClockHelpers_FormatCaptionsAndSplitBrowserSlicesAcrossSites()
    {
        var today = new DateTime(2026, 11, 14);
        Assert.Equal(ResourceStrings.Today,
            DataPageViewModel.FormatClockCaption(0, today, today, today));
        Assert.Equal("11/13",
            DataPageViewModel.FormatClockCaption(0, today.AddDays(-1), today.AddDays(-1), today));
        Assert.Equal("2025/11/13",
            DataPageViewModel.FormatClockCaption(0, new DateTime(2025, 11, 13), new DateTime(2025, 11, 13), today));
        Assert.Equal(ResourceStrings.ThisWeek,
            DataPageViewModel.FormatClockCaption(1, today.AddDays(-5), today.AddDays(1), today));
        Assert.Equal(ResourceStrings.ThisMonth,
            DataPageViewModel.FormatClockCaption(2, new DateTime(2026, 11, 1), new DateTime(2026, 11, 30), today));
        Assert.Equal(ResourceStrings.ThisYear,
            DataPageViewModel.FormatClockCaption(3, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), today));

        var day = new DateTime(2026, 11, 14);
        var sessions = new List<AppSessionModel>
        {
            new()
            {
                ID = 1,
                AppModelID = 10,
                StartTime = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Local),
                EndTime = new DateTime(2026, 11, 14, 10, 30, 0, DateTimeKind.Local),
                Duration = 1800,
                AppModel = new AppModel { ID = 10, Name = "chrome", Description = "Google Chrome", CategoryID = 2 }
            },
            new()
            {
                ID = 2,
                AppModelID = 11,
                StartTime = new DateTime(2026, 11, 14, 11, 0, 0, DateTimeKind.Local),
                EndTime = new DateTime(2026, 11, 14, 11, 10, 0, DateTimeKind.Local),
                Duration = 600,
                AppModel = new AppModel { ID = 11, Name = "Code", Description = "VS Code", CategoryID = 1 }
            }
        };

        var labels = Enumerable.Range(0, 24)
            .Select(h => new DateTimeOffset(day.AddHours(h)).ToString("o"))
            .ToList();
        var siteValues = new double[24];
        siteValues[10] = 1200;
        var entityHours = new EntityHoursModel
        {
            Labels = labels,
            BucketCount = 24,
            Rows =
            [
                new EntityHoursRow
                {
                    Key = "site:1",
                    Kind = "site",
                    Name = "GitHub",
                    CategoryId = 1,
                    CategoryName = "开发",
                    CategoryColor = "#2563EB",
                    Values = siteValues
                }
            ]
        };
        var cats = new Dictionary<int, ShiJian.Client.Shared.Models.CategoryModel>
        {
            [1] = new() { ID = 1, Name = "开发", Color = "#2563EB" },
            [2] = new() { ID = 2, Name = "工具", Color = "#64748B" }
        };

        var daySegments = DataPageViewModel.BuildDayClockSegments(day, sessions, entityHours, cats, isDark: false);
        Assert.Contains(daySegments, s => s.Name == "GitHub" && s.CategoryName == "开发" && Math.Abs(s.Seconds - 1200) < 1);
        Assert.Contains(daySegments, s => s.Name == "VS Code" && Math.Abs(s.Seconds - 600) < 1);
        Assert.Equal(2400, daySegments.Sum(s => s.Seconds), precision: 1);

        var weekStart = new DateTime(2026, 11, 9);
        var weekEnd = new DateTime(2026, 11, 15);
        var hourly = DataPageViewModel.BuildHourlyClockSegments(
            new EntityHoursModel
            {
                Labels = [new DateTimeOffset(new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Local)).ToString("o")],
                BucketCount = 1,
                Rows =
                [
                    new EntityHoursRow
                    {
                        Key = "app:11",
                        Kind = "app",
                        Name = "VS Code",
                        CategoryId = 1,
                        CategoryName = "开发",
                        CategoryColor = "#2563EB",
                        Values = [900]
                    }
                ]
            },
            cats, weekStart, weekEnd, isDark: false);

        Assert.Single(hourly);
        Assert.InRange(hourly[0].StartFraction, 0d, 1d);
        Assert.True(hourly[0].EndFraction > hourly[0].StartFraction);
    }

    [AvaloniaFact]
    public void Imager_ClassifyIconTone_DetectsLightDarkAndNeutralForegrounds()
    {
        static (IconTone WbTone, IconTone PngTone) ClassifySolid(byte b, byte g, byte r, byte a)
        {
            using var bmp = new WriteableBitmap(
                new PixelSize(8, 8),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            using (var fb = bmp.Lock())
            {
                var rowBytes = fb.Size.Width * 4;
                var buffer = new byte[fb.RowBytes * fb.Size.Height];
                for (var y = 0; y < fb.Size.Height; y++)
                {
                    var rowStart = y * fb.RowBytes;
                    for (var x = 0; x < rowBytes; x += 4)
                    {
                        buffer[rowStart + x + 0] = b;
                        buffer[rowStart + x + 1] = g;
                        buffer[rowStart + x + 2] = r;
                        buffer[rowStart + x + 3] = a;
                    }
                }
                Marshal.Copy(buffer, 0, fb.Address, buffer.Length);
            }
            var wbTone = Imager.ClassifyIconTone(bmp);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            using var png = new Bitmap(ms);
            var pngTone = Imager.ClassifyIconTone(png);
            return (wbTone, pngTone);
        }

        Assert.Equal((IconTone.LightForeground, IconTone.LightForeground), ClassifySolid(250, 250, 250, 255));
        Assert.Equal((IconTone.DarkForeground, IconTone.DarkForeground), ClassifySolid(18, 18, 18, 255));
        Assert.Equal((IconTone.Neutral, IconTone.Neutral), ClassifySolid(235, 99, 37, 255));
    }

    [AvaloniaFact]
    public void ColumnChart_BuildLegend_RespectsIsShowLegendTotalFlag()
    {
        var chart = new ColumnChart();
        var expectedTotal = Application.Current?.FindResource("Total") as string ?? "Total";
        var data = new List<ChartsDataModel>
        {
            new() { Name = "开发", Color = "#2563EB", Values = [120, 60] },
            new() { Name = "办公", Color = "#0D9488", Values = [60, 0] }
        };

        Assert.False(chart.IsShowLegendTotal);
        var defaultLegend = chart.BuildLegend(data, colIndex: -1);
        Assert.Equal(2, defaultLegend.Count);
        Assert.DoesNotContain(defaultLegend, l => l.Name == expectedTotal);

        chart.IsShowLegendTotal = true;
        var legendWithTotal = chart.BuildLegend(data, colIndex: -1);
        Assert.Equal(3, legendWithTotal.Count);
        Assert.Equal(expectedTotal, legendWithTotal[^1].Name);
        Assert.Equal("4m", legendWithTotal[^1].Text);
        Assert.True(string.IsNullOrEmpty(legendWithTotal[^1].Color));
    }

    [Fact]
    public void CategoryLists_MainFilterAndCandidateSubTitle_WorkAccurately()
    {
        var sites = new[]
        {
            new WebSiteModel { ID = 1, Title = "GitHub", Domain = "github.com", Alias = "代码仓库" },
            new WebSiteModel { ID = 2, Title = "Bilibili", Domain = "bilibili.com", Alias = "B站" }
        };

        Assert.Equal(2, CategoryWebSiteListPageViewModel.FilterWebSites(sites, null).Count);
        var filtered = CategoryWebSiteListPageViewModel.FilterWebSites(sites, "仓库");
        Assert.Single(filtered);
        Assert.Equal(1, filtered[0].ID);

        var sysCat = new CategoryModel { ID = 0, IsSystem = true };
        var apps = new[]
        {
            new AppModel
            {
                ID = 1, Name = "msedge", Description = "Microsoft Edge",
                File = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                CategoryID = 0, Category = sysCat, TotalTime = 100
            },
            new AppModel
            {
                ID = 2, Name = "msedge", Description = "Microsoft Edge",
                File = @"D:\Portable\EdgeBeta\msedge.exe",
                CategoryID = 0, Category = sysCat, TotalTime = 50
            }
        };

        var options = CategoryAppListPageViewModel.BuildCandidateOptions(apps, categoryId: 3);
        Assert.Equal(2, options.Count);
        Assert.Equal("msedge · Application", options.First(o => o.App.ID == 1).Value.SubTitle);
        Assert.Equal("msedge · EdgeBeta", options.First(o => o.App.ID == 2).Value.SubTitle);
    }

    [AvaloniaFact]
    public async Task CategoryPageViewModel_BackNavigationAndBrowseModeSwitch_PreserveCachedCardsWithoutFlash()
    {
        var stateService = new StateService();
        var isBack = false;
        var navService = StubProxy<INavigationService>.Create(new()
        {
            [$"get_{nameof(INavigationService.IsNavigatingBack)}"] = _ => isBack
        });
        var pendingCategories = new TaskCompletionSource<List<CategoryModel>>();
        var usePending = false;
        var sampleCat = new CategoryModel { ID = 1, Name = "开发", Color = "#2563EB" };

        var categoryService = StubProxy<ICategorys>.Create(new()
        {
            [nameof(ICategorys.GetCategoriesAsync)] = _ =>
                usePending ? pendingCategories.Task : Task.FromResult(new List<CategoryModel> { sampleCat })
        });
        var appData = StubProxy<IAppData>.Create(new()
        {
            [nameof(IAppData.GetAppCountByCategoryIDAsync)] = _ => Task.FromResult(3),
            [nameof(IAppData.GetAllAppsAsync)] = _ => Task.FromResult<IReadOnlyCollection<AppModel>>(
                [new AppModel { ID = 10, Name = "Code", Description = "VS Code", CategoryID = 1, TotalTime = 600 }])
        });
        var webData = StubProxy<IWebData>.Create(new()
        {
            [nameof(IWebData.GetWebSitesCountAsync)] = _ => Task.FromResult(2)
        });
        var webSiteData = StubProxy<IWebSiteData>.Create(new()
        {
            [nameof(IWebSiteData.GetAllWebSitesAsync)] = _ => Task.FromResult<IReadOnlyCollection<WebSiteModel>>([])
        });
        var entityData = StubProxy<IEntityData>.Create(new()
        {
            [nameof(IEntityData.GetEntityListAsync)] = _ => Task.FromResult<EntityListModel?>(new EntityListModel
            {
                Rows = [new EntityModel { Key = "app:10", Kind = "app", Name = "Code", CategoryId = 1, Seconds = 600 }]
            })
        });
        var appMerge = StubProxy<IAppMergeService>.Create(new()
        {
            [nameof(IAppMergeService.GetGroupsAsync)] = _ => Task.FromResult<IReadOnlyList<AppMergeGroup>>([])
        });

        CategoryPageViewModel CreateVm() => new(
            categoryService, navService, appData,
            StubProxy<IData>.Create(new()),
            entityData, webData, webSiteData,
            StubProxy<IAppUpdateService>.Create(new()),
            appMerge,
            StubProxy<IDialogService>.Create(new()),
            StubProxy<IToastService>.Create(new()),
            stateService);

        var vm1 = CreateVm();
        try
        {
            await vm1.OnNavigatedToAsync();
            Assert.Single(vm1.Data);
            Assert.Equal(600, vm1.Data[0].Duration);

            // 切到应用与网站视图并验证实体加载
            vm1.BrowseMode = vm1.BrowseModeOptions[1];
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm1.HasFilteredEntities);

            vm1.BrowseMode = vm1.BrowseModeOptions[0];
            vm1.OnNavigatedFrom();
        }
        finally
        {
            vm1.Dispose();
        }

        // 模拟返回导航：服务端暂未响应时应立即显示缓存卡片且 IsLoading == false（无骨架屏闪烁）
        isBack = true;
        usePending = true;
        var vm2 = CreateVm();
        try
        {
            var navTask = vm2.OnNavigatedToAsync();
            Assert.Single(vm2.Data);
            Assert.True(vm2.HasAnyAppCategories);
            Assert.False(vm2.IsLoading);

            pendingCategories.SetResult([sampleCat]);
            await navTask;
            Assert.Single(vm2.Data);
        }
        finally
        {
            vm2.Dispose();
        }
    }

    [AvaloniaFact]
    public void SearchResultItem_FormattedSecondaryText_AndLongTitleLayoutClamping()
    {
        var emptySecondary = new ShiJian.Client.Shared.Models.Search.SearchResultItem(
            ShiJian.Client.Shared.Models.Search.SearchResultType.App,
            1,
            "Cross-platform desktop music player with rich lyric support and wide audio format capabilities",
            string.Empty,
            null,
            null);
        Assert.Equal(string.Empty, emptySecondary.FormattedSecondaryText);

        var withSecondary = new ShiJian.Client.Shared.Models.Search.SearchResultItem(
            ShiJian.Client.Shared.Models.Search.SearchResultType.WebSite,
            2,
            "Bilibili",
            "www.bilibili.com",
            null,
            null);
        Assert.Equal("  www.bilibili.com", withSecondary.FormattedSecondaryText);
    }

    private sealed class RelayActionCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
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
