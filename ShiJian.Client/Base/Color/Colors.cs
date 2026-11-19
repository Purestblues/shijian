using System.Collections.Generic;
using Avalonia.Media;

namespace ShiJian.Client.Base.Color;

public class Colors
{
    public static Dictionary<ColorTypes, IColor> ColorList = new()
    {
        { ColorTypes.Aquamarine, new IColor { Name = "碧绿", Color = "#00AA90" } },
        { ColorTypes.Black, new IColor { Name = "暗黑", Color = "#080808" } },
        { ColorTypes.Blue, new IColor { Name = "蓝色", Color = "#0078d4" } },
        { ColorTypes.Cyan, new IColor { Name = "青色", Color = "#51A8DD" } },
        { ColorTypes.Gold, new IColor { Name = "金色", Color = "#EFBB24" } },
        { ColorTypes.Gray, new IColor { Name = "灰色", Color = "#828282" } },
        { ColorTypes.Green, new IColor { Name = "绿色", Color = "#56C773" } },
        { ColorTypes.Orange, new IColor { Name = "橙色", Color = "#E98B2A" } },
        { ColorTypes.Pink, new IColor { Name = "粉红", Color = "#B5495B" } },
        { ColorTypes.Red, new IColor { Name = "赤红", Color = "#F3221B" } },
        { ColorTypes.Violet, new IColor { Name = "紫色", Color = "#77428D" } },
        { ColorTypes.Yellow, new IColor { Name = "黄色", Color = "#FFC408" } },
        { ColorTypes.White, new IColor { Name = "白色", Color = "#FFFFFF" } }
    };

    public static string[] MainColors =
    {
        // 原有 12 色（Nord / Everforest 经典色，保持索引稳定）
        "#527CE6", // 矢车菊蓝 (Cornflower Frost - 默认主色)
        "#5E81AC", // 北极深冰蓝 (Nord Frost Blue)
        "#4D968E", // 松石青绿 (Sage Teal)
        "#689D6A", // 苔原森绿 (Aurora Moss)
        "#8DA101", // 橄榄嫩绿 (Everforest Olive)
        "#C8834B", // 暖砂琥珀 (Warm Ochre)
        "#D08770", // 陶土暖橙 (Aurora Coral)
        "#BF616A", // 柔和绯红 (Aurora Crimson)
        "#B85D82", // 雾玫瑰红 (Dusty Rose)
        "#9D7298", // 雾梅紫 (Aurora Plum)
        "#7E6BC4", // 薰衣草紫 (Muted Lavender)
        "#4C566A", // 极夜岩灰 (Polar Slate)

        // N21 扩充 12 色（参考“长期洗涤”低饱和自然/影视氛围色系）
        "#4A8FE7", // 晴空蔚蓝 (Sky Azure)
        "#5D9CEC", // 浅雾天蓝 (Mist Blue)
        "#3B9B96", // 翡翠海青 (Seafoam Jade)
        "#529A74", // 冷杉森林 (Fir Forest)
        "#78A55A", // 豆蔻青绿 (Matcha Sage)
        "#B89B4D", // 麦穗暖金 (Wheat Ochre)
        "#D99B52", // 落日杏黄 (Apricot Glow)
        "#C97A63", // 赤陶珊瑚 (Terracotta)
        "#C46D7A", // 干枯玫瑰 (Muted Rose)
        "#A876A2", // 烟霞藕紫 (Mauve Haze)
        "#6F7BB5", // 暮色靛蓝 (Twilight Indigo)
        "#72808E", // 雾霾石青 (Fog Slate)
    };

    public static string[] TimelinePaletteDark { get; } = GenerateTimelinePalette(45, isDark: true);

    public static string[] TimelinePaletteLight { get; } = GenerateTimelinePalette(45, isDark: false);

    // 颜色缓存，避免重复计算 hash
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Key, bool IsDark), string> _timelineColorCache = new();

    public static string GetTimelinePaletteColor(string key, bool isDark)
    {
        if (string.IsNullOrEmpty(key)) return isDark ? TimelinePaletteDark[0] : TimelinePaletteLight[0];
        return _timelineColorCache.GetOrAdd((key, isDark), static k =>
        {
            var palette = k.IsDark ? TimelinePaletteDark : TimelinePaletteLight;
            var hash = k.Key.GetHashCode();
            var index = System.Math.Abs(hash) % palette.Length;
            return palette[index];
        });
    }

    private static string[] GenerateTimelinePalette(int count, bool isDark)
    {
        var baseL = isDark ? 0.66 : 0.52;
        var saturation = isDark ? 0.54 : 0.48;
        var result = new string[count];

        for (var i = 0; i < count; i++)
        {
            var h = i * 360.0 / count;
            var l = h switch
            {
                >= 36 and <= 72 => baseL + 0.10,
                >= 200 and <= 260 => baseL - 0.05,
                >= 270 and <= 310 => baseL + 0.03,
                _ => baseL
            };
            if (l < 0.25) l = 0.25;
            if (l > 0.85) l = 0.85;
            result[i] = HslToHex(h, saturation, l);
        }

        return result;
    }

    private static string HslToHex(double h, double s, double l)
    {
        var c = (1.0 - System.Math.Abs(2.0 * l - 1.0)) * s;
        var x = c * (1.0 - System.Math.Abs((h / 60.0) % 2.0 - 1.0));
        var m = l - c / 2.0;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return $"#{(byte)((r + m) * 255):X2}{(byte)((g + m) * 255):X2}{(byte)((b + m) * 255):X2}";
    }

    public static IColor Get(ColorTypes color)
    {
        return ColorList[color];
    }

    public static SolidColorBrush GetColor(ColorTypes color, double opacity = 1)
    {
        return GetFromString(ColorList[color].Color, opacity);
    }

    public static SolidColorBrush GetFromString(string color, double opacity = 1)
    {
        if (string.IsNullOrEmpty(color)) color = StateData.ThemeColor;

        return new SolidColorBrush(Avalonia.Media.Color.Parse(color), opacity);
    }

    public struct IColor
    {
        /// <summary>
        /// 颜色名称
        /// </summary>
        public string Name;

        /// <summary>
        /// 颜色
        /// </summary>
        public string Color;
    }
}
