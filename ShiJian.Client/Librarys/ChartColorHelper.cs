using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ShiJian.Client.Librarys;

/// <summary>
/// 图表配色统一入口。
///
/// 分类颜色来自服务端，系统分类"未分类"默认是 #e4e3df（几乎等于白色背景），
/// 直接拿来画柱子/圆环等于什么都看不见。这里的规则：
/// 1. 系统分类一律用主题色——它本来就是"兜底桶"，用品牌色表达更合理
/// 2. 其他颜色按"和当前主题背景的对比度"调整：浅色背景下太浅的压暗，深色背景下太暗的提亮，
///    只调到刚好够看（WCAG 图形元素 3:1），尽量保留原来的色相与明暗关系
///
/// 判定基准是当前主题实际使用的窗口背景色，所以同一套分类色在浅色 / 深色主题下会自动
/// 收敛到各自可读的版本，不再依赖写死的亮度阈值。
/// </summary>
public static class ChartColorHelper
{
    /// <summary>分类缺失时的兜底色</summary>
    private const string FallbackColor = "#7E8B9B";

    /// <summary>主题色拿不到时的兜底（与 Theme.axaml 的默认 ThemeColor 一致）</summary>
    private const string FallbackAccent = "#527CE6";

    /// <summary>图形元素的最低对比度（WCAG 2.1 AA 对非文本图形的要求）</summary>
    public const double TargetContrast = 3d;

    /// <summary>主题背景拿不到时的浅色 / 深色兜底（与 ThemeServicer 的实色背景一致）</summary>
    private static readonly Color LightBackground = Color.Parse("#ECEFF4");
    private static readonly Color DarkBackground = Color.Parse("#181A20");

    private const int AdjustIterations = 14;

    /// <summary>
    /// 主题背景 / 主题色的缓存。
    /// 分类色会在后台线程（如时间轴批量组装）里解析，而 Application 里的资源只能 UI 线程访问，
    /// 所以统一在主题应用时算好存起来，Resolve 变成纯函数。
    /// </summary>
    private static Color _background = LightBackground;
    private static Color _accent = Color.Parse(FallbackAccent);

    /// <summary>主题（背景色 / 主题色）变化后触发，页面据此重新解析缓存里的颜色</summary>
    public static event Action? ThemeChanged;

    /// <summary>由 <see cref="Servicers.ThemeServicer"/> 在应用完主题后调用（UI 线程）</summary>
    public static void NotifyThemeChanged()
    {
        if (Application.Current is { } app && Dispatcher.UIThread.CheckAccess())
        {
            _background = ReadBackground(app);
            _accent = app.FindResource("ThemeColor") is Color color ? color : Color.Parse(FallbackAccent);
        }

        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// 把分类色解析成可直接画图的高对比颜色
    /// </summary>
    /// <param name="color">分类原始颜色（#RRGGBB / #AARRGGBB）</param>
    /// <param name="isSystem">是否为系统分类（未分类）</param>
    public static string Resolve(string? color, bool isSystem = false) =>
        ResolveFor(color, isSystem, BackgroundColor);

    /// <summary>
    /// 指定背景色的解析版本，便于单测与"按图片取色"等场景
    /// </summary>
    public static string ResolveFor(string? color, bool isSystem, Color background)
    {
        if (isSystem)
            return ToHex(EnsureContrast(ParseOr(AccentHex, FallbackAccent), background));

        var parsed = ParseOr(color, FallbackColor);
        return ToHex(EnsureContrast(parsed, background));
    }

    /// <summary>
    /// 当前图表背景色（缓存值）：主题应用时按实色窗口背景算好，否则按明暗变体兜底
    /// </summary>
    public static Color BackgroundColor => _background;

    /// <summary>
    /// 当前主题色（用户可在设置里改）
    /// </summary>
    public static string AccentHex => ToHex(_accent);

    private static Color ReadBackground(Application app)
    {
        if (app.Resources.TryGetResource("WindowBackground", app.ActualThemeVariant, out var value))
        {
            switch (value)
            {
                case ISolidColorBrush solid:
                    return solid.Color;
                case Color color:
                    return color;
            }
        }

        return app.ActualThemeVariant == ThemeVariant.Dark ? DarkBackground : LightBackground;
    }

    /// <summary>
    /// WCAG 相对亮度（0~1），已按 sRGB 做伽马还原
    /// </summary>
    public static double RelativeLuminance(Color color) =>
        0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

    /// <summary>
    /// 两个颜色的对比度（1~21）
    /// </summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>给颜色加透明度（#AARRGGBB），用于徽章底色等“同色淡底”场景</summary>
    public static string WithAlpha(string? color, byte alpha)
    {
        if (string.IsNullOrWhiteSpace(color) || !Color.TryParse(color, out var parsed))
            return color ?? string.Empty;

        return $"#{alpha:X2}{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}";
    }

    private static readonly int[] RampStops = [50, 100, 200, 300, 400, 500, 600, 700, 800, 900];

    /// <summary>生成 50–900 十档色阶（500 为基准色）</summary>
    public static IReadOnlyDictionary<int, Color> BuildRamp(Color baseColor)
    {
        var white = Color.FromRgb(255, 255, 255);
        var deep = Color.FromRgb(17, 21, 28); // #11151C
        return new Dictionary<int, Color>(10)
        {
            [50] = Blend(baseColor, white, 0.90),
            [100] = Blend(baseColor, white, 0.78),
            [200] = Blend(baseColor, white, 0.60),
            [300] = Blend(baseColor, white, 0.40),
            [400] = Blend(baseColor, white, 0.20),
            [500] = baseColor,
            [600] = Blend(baseColor, deep, 0.14),
            [700] = Blend(baseColor, deep, 0.28),
            [800] = Blend(baseColor, deep, 0.44),
            [900] = Blend(baseColor, deep, 0.62)
        };
    }

    /// <summary>按对比度解析分类基准色后，提取指定色阶档位（50–900）</summary>
    public static Color GetShadeFor(string? color, int shade, bool isSystem, Color background)
    {
        var resolvedHex = ResolveFor(color, isSystem, background);
        var resolved = ParseOr(resolvedHex, FallbackColor);
        var ramp = BuildRamp(resolved);

        var nearestStop = 500;
        var minDist = int.MaxValue;
        foreach (var stop in RampStops)
        {
            var dist = Math.Abs(stop - shade);
            if (dist < minDist)
            {
                minDist = dist;
                nearestStop = stop;
            }
        }

        return ramp[nearestStop];
    }

    /// <summary>将归一化热度（0..1] 映射到 5 档色阶（浅色由浅入深，深色由深到亮）</summary>
    public static Color GetHeatColor(Color baseColor, double normalizedLevel, bool isDark)
    {
        var level = Math.Clamp(normalizedLevel, 0, 1);
        var ramp = BuildRamp(baseColor);
        ReadOnlySpan<int> stops = isDark
            ? [800, 700, 500, 400, 300]
            : [100, 200, 400, 600, 800];

        var eased = Math.Sqrt(level);
        var idx = Math.Clamp((int)Math.Floor(eased * stops.Length), 0, stops.Length - 1);
        if (level >= 0.999) idx = stops.Length - 1;
        return ramp[stops[idx]];
    }

    private static Color EnsureContrast(Color color, Color background)
    {
        if (ContrastRatio(color, background) >= TargetContrast)
            return color;

        // 背景暗就往白里提，背景亮就往黑里压——这样才能拉开对比度
        var towardsWhite = RelativeLuminance(background) < 0.5;
        var extreme = towardsWhite ? Color.FromRgb(255, 255, 255) : Color.FromRgb(0, 0, 0);

        if (ContrastRatio(extreme, background) < TargetContrast)
            return extreme;

        // 二分找"刚好达到目标对比度"的最小改动量，避免把颜色调过头
        double low = 0, high = 1;
        var result = extreme;
        for (var i = 0; i < AdjustIterations; i++)
        {
            var mid = (low + high) / 2;
            var candidate = Blend(color, extreme, mid);
            if (ContrastRatio(candidate, background) >= TargetContrast)
            {
                result = candidate;
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        return result;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Color ParseOr(string? color, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(color) && Color.TryParse(color, out var parsed))
            return parsed;

        return Color.Parse(fallback);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
