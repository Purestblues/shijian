using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShiJian.Client.Controls.Base;
using ShiJian.Client.Controls.Tabbar;
using Xunit;

namespace ShiJian.Tests;

public class TabbarTests
{
    [AvaloniaFact]
    public void Tabbar_WithIcons_RendersIconAndTextAndUpdatesColorsOnSelection()
    {
        var selectedColor = Color.Parse("#527CE6");
        var unselectedColor = Color.Parse("#646E82");

        var tabbar = new Tabbar
        {
            Width = 700,
            SelectedTextColor = selectedColor,
            UnselectedTextColor = unselectedColor,
            Data = new ObservableCollection<string>(["常规", "外观", "统计", "数据", "关于"]),
            Icons = new ObservableCollection<IconTypes>(
            [
                IconTypes.Settings,
                IconTypes.SetlockScreen,
                IconTypes.BulletedList,
                IconTypes.Folder,
                IconTypes.Info
            ]),
            SelectedIndex = 0
        };

        var window = new Window { Width = 800, Height = 120, Content = tabbar };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var icons = tabbar.GetVisualDescendants().OfType<Icon>().ToList();
            var labels = tabbar.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t.FindAncestorOfType<Icon>() == null)
                .ToList();

            Assert.Equal(5, icons.Count);
            Assert.Equal(5, labels.Count);
            Assert.Equal(IconTypes.Settings, icons[0].IconType);
            Assert.Equal(IconTypes.Info, icons[4].IconType);
            Assert.Equal(selectedColor, ((ISolidColorBrush)icons[0].Foreground!).Color);
            Assert.Equal(unselectedColor, ((ISolidColorBrush)icons[2].Foreground!).Color);

            tabbar.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(unselectedColor, ((ISolidColorBrush)icons[0].Foreground!).Color);
            Assert.Equal(selectedColor, ((ISolidColorBrush)icons[2].Foreground!).Color);
            Assert.Equal(selectedColor, ((ISolidColorBrush)labels[2].Foreground!).Color);
        }
        finally
        {
            window.Close();
        }
    }
}
