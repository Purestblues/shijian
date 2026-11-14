using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using ShiJian.Client.Controls.Category;
using ShiJian.Client.Models.Category;
using Xunit;
using Card = ShiJian.Client.Controls.Category.CategoryCard;

namespace ShiJian.Tests;

/// <summary>
/// N7-b 分类卡片的两种形态：紧凑用一份模板，卡片形态靠 <c>IsCard</c> 换模板。
/// 断言尺寸而不是像素是故意的——这里要锁的是「形态真的切了」，不是具体画成什么样。
/// </summary>
public class CategoryCardTests
{
    private static CategoryModel AppCard()
    {
        var model = new CategoryModel
        {
            Data = new ShiJian.Client.Shared.Models.CategoryModel
            {
                ID = 1,
                Name = "开发",
                Color = "#00AA00"
            },
            Count = 8,
            Duration = 3000,
            Ratio = 0.5
        };
        return model;
    }

    private static (Card Card, Window Window) Create()
    {
        var card = new Card { DataContext = AppCard() };
        var window = new Window { Width = 400, Height = 400, Content = card };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (card, window);
    }

    [AvaloniaFact]
    public void DefaultsToCompactSize()
    {
        var (card, window) = Create();

        try
        {
            Assert.False(card.IsCard);
            Assert.Equal(HorizontalAlignment.Stretch, card.HorizontalAlignment);
            Assert.Equal(156, card.MinWidth);
            Assert.True(card.Bounds.Width > 156);
            Assert.Equal(92, card.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CardMode_SwitchesToBiggerSize()
    {
        var (card, window) = Create();

        try
        {
            card.IsCard = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(HorizontalAlignment.Stretch, card.HorizontalAlignment);
            Assert.Equal(156, card.MinWidth);
            Assert.True(card.Bounds.Width > 156);
            Assert.Equal(148, card.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 卡片绑定的是基类属性（Name / IconFile / DurationText …），
    /// 模板里不该出现 `Data.xxx`——否则应用分类与网站分类就得两套模板。
    /// </summary>
    [AvaloniaFact]
    public void CardModel_ExposesFlattenedFields()
    {
        var card = AppCard();
        Assert.Equal("开发", card.Name);
        Assert.Equal(8, card.Count);
        Assert.Equal("×8", card.CountText);
        Assert.Equal("50m", card.DurationText);
        Assert.NotEmpty(card.DisplayColor);
    }
}
