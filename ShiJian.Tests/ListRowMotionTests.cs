using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ShiJian.Client.Librarys;
using Xunit;

namespace ShiJian.Tests;

public class ListRowMotionTests
{
    [AvaloniaFact]
    public async Task PlayStaggerInAsync_WhenMotionDisabled_KeepsItemsOpaqueImmediately()
    {
        var previous = Motion.IsEnabled;
        Motion.SetEnabled(false);

        var list = new ListBox
        {
            Width = 300,
            Height = 200,
            ItemsSource = new[] { "A", "B", "C" }
        };
        var window = new Window { Width = 320, Height = 240, Content = list };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await ListRowMotion.PlayStaggerInAsync(list);

            for (var i = 0; i < 3; i++)
            {
                var container = list.ContainerFromIndex(i);
                Assert.NotNull(container);
                Assert.Equal(1d, container.Opacity);
            }
        }
        finally
        {
            window.Close();
            Motion.SetEnabled(previous);
        }
    }

    [AvaloniaFact]
    public async Task CollapseRowAsync_RestoresHeightAndOpacityAfterCompletion()
    {
        var previous = Motion.IsEnabled;
        Motion.SetEnabled(true);

        var row = new Border { Height = 48, Width = 280 };
        var window = new Window { Width = 320, Height = 200, Content = row };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 模拟行高已测出后再归还为 Auto（NaN）
            row.Height = double.NaN;
            row.Measure(new Size(280, 200));
            row.Arrange(new Rect(0, 0, 280, 48));

            await ListRowMotion.CollapseRowAsync(row);

            Assert.True(double.IsNaN(row.Height));
            Assert.Equal(1d, row.Opacity);
        }
        finally
        {
            window.Close();
            Motion.SetEnabled(previous);
        }
    }
}
