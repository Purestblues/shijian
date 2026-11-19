using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ShiJian.Client.Librarys;
using ShiJian.Client.Shared.Event;
using ShiJian.Client.Shared.Models.Config;
using ShiJian.Client.Shared.Servicers.Interfaces;
using Colors = ShiJian.Client.Base.Color.Colors;

namespace ShiJian.Client.Servicers;

public class ThemeServicer : IThemeServicer
{

    public static readonly ThemeVariant Azure = new("Azure", ThemeVariant.Light);

    private sealed record ThemeDefinition(
        ThemeVariant Variant,
        string? LockedAccent,
        Color? SolidBackground);

    private static readonly ThemeDefinition[] Definitions =
    {
        new(ThemeVariant.Default, null, null),
        new(ThemeVariant.Light, null, null),
        new(ThemeVariant.Dark, null, null),
        new(Azure, "#3B82C4", Color.Parse("#EBF2F7")),
    };

    private static readonly Color LightSolid = Color.Parse("#ECEFF4");
    private static readonly Color DarkSolid = Color.Parse("#181A20");

    private readonly IAppConfig _appConfig;

    public ThemeServicer(IAppConfig appConfig)
    {
        _appConfig = appConfig;
        _appConfig.ConfigChanged += OnConfigChanged;
    }

    public void Init()
    {
        LoadTheme(GetTheme(_appConfig.GetConfig().General.Theme));
    }

    public void LoadTheme(AppTheme theme)
    {
        var definition = Definitions[(int)theme];

        void Apply()
        {
            if (Application.Current == null) return;
            ApplyTheme(definition);
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Apply);
        }
    }

    public void SetMainWindow(Views.MainWindow mainWindow)
    {
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.HasChange("General.Theme") ||
            e.HasChange("General.ThemeColor") ||
            e.HasChange("General.WindowGradientScheme"))
        {
            LoadTheme(GetTheme(e.NewConfig.General.Theme));
        }
    }

    private void ApplyTheme(ThemeDefinition definition)
    {
        var app = Application.Current!;
        var config = _appConfig.GetConfig();

        // 切换变体，ThemeDictionaries 自动选对应字典
        app.RequestedThemeVariant = definition.Variant;
        var actualVariant = app.ActualThemeVariant;
        var isDark = actualVariant == ThemeVariant.Dark;

        // 主题色：Azure 锁定北极冰海蓝，深色使用默认矢车菊蓝时自动映射至柔和冰晶蓝
        var accent = definition.LockedAccent ?? config.General.ThemeColor;
        if (string.IsNullOrWhiteSpace(accent))
        {
            accent = app.Resources.TryGetResource("ThemeColor", actualVariant, out var value)
                ? value?.ToString()
                : (isDark ? "#78A0F0" : "#527CE6");
        }
        else if (definition.LockedAccent is null &&
                 string.Equals(accent, "#527CE6", StringComparison.OrdinalIgnoreCase) &&
                 isDark)
        {
            accent = "#78A0F0";
        }

        if (!Color.TryParse(accent, out var accentColor))
        {
            accent = isDark ? "#78A0F0" : "#527CE6";
            accentColor = Color.Parse(accent);
        }

        StateData.ThemeColor = accent!;
        ApplyAccentResources(app.Resources, definition.Variant, accentColor, isDark);

        // 窗口背景：平铺实色底画布 Surface0（移除渐变化妆光晕）
        var bg = definition.SolidBackground ?? (isDark ? DarkSolid : LightSolid);
        app.Resources["WindowBackground"] = new SolidColorBrush(bg);

        // 图表配色取决于背景与主题色，换主题后要重新解析
        ChartColorHelper.NotifyThemeChanged();
    }

    private static void ApplyAccentResources(
        Avalonia.Controls.IResourceDictionary resources,
        ThemeVariant variant,
        Color accentColor,
        bool isDark)
    {
        var isAzure = variant == Azure;
        var accentBrush = new SolidColorBrush(accentColor);
        var tintAlpha = isDark ? (byte)0x26 : (byte)0x24;
        var tintBrush = new SolidColorBrush(
            Color.FromArgb(tintAlpha, accentColor.R, accentColor.G, accentColor.B));
        var pillColor = ResolvePillColor(accentColor, isDark, isAzure);
        var pillBrush = new SolidColorBrush(pillColor);
        var listHoverBrush = new SolidColorBrush(
            ResolveListHoverColor(accentColor, isDark, isAzure));
        var badgeAlpha = isDark ? (byte)0x33 : (byte)0x24;
        var badgeBgBrush = new SolidColorBrush(
            Color.FromArgb(badgeAlpha, accentColor.R, accentColor.G, accentColor.B));
        var webFgColor = ResolveWebBadgeForeground(accentColor, isDark, isAzure);

        resources["ThemeColor"] = accentColor;
        resources["ThemeBrush"] = accentBrush;
        resources["ThemeAccentBrush"] = accentBrush;
        resources["ThemeTintBrush"] = tintBrush;
        foreach (var (stop, shadeColor) in ChartColorHelper.BuildRamp(accentColor))
            resources[$"ThemeRamp{stop}Brush"] = new SolidColorBrush(shadeColor);
        resources["SelectRingBrush"] = new SolidColorBrush(
            Color.FromArgb(0x26, accentColor.R, accentColor.G, accentColor.B));

        resources["WebBadgeBgBrush"] = tintBrush;
        resources["WebBadgeFgBrush"] = new SolidColorBrush(webFgColor);
        resources["ThemeBadgeBgBrush"] = badgeBgBrush;
        resources["ThemeBadgeFgBrush"] = accentBrush;
        resources["ChartCardPillBgBrush"] = badgeBgBrush;
        resources["ChartCardPillTextBrush"] = isDark
            ? new SolidColorBrush(Color.Parse("#ECEFF4"))
            : accentBrush;

        resources["SelectionPillBrush"] = pillBrush;
        resources["RowSelectedBrush"] = pillBrush;
        resources["NavSelectedColor"] = pillColor;
        resources["NavSelectedBrush"] = pillBrush;
        resources["NavSelectedGlassBrush"] = pillBrush;
        resources["ListItemSelectedBrush"] = pillBrush;
        resources["ListItemSelectedHoverBrush"] = listHoverBrush;
        resources["OptionCheckedBackgroundBrush"] = pillBrush;
        resources["OptionCheckedBorderBrush"] = accentBrush;
        resources["TabOptionTextCheckedColor"] = accentColor;

        resources["SettingPanelAccentBorderBrush"] = new SolidColorBrush(
            Color.FromArgb(isDark || isAzure ? (byte)0x73 : (byte)0x59, accentColor.R, accentColor.G, accentColor.B));
        resources["ListItemAccentBrush"] = new SolidColorBrush(
            Color.FromArgb(isDark ? (byte)0xD9 : (byte)0xE6, accentColor.R, accentColor.G, accentColor.B));

        resources["TimelineNowBrush"] = accentBrush;
        resources["TimelineSelectBgBrush"] = new SolidColorBrush(
            Color.FromArgb(isDark ? (byte)0x33 : (byte)0x29, accentColor.R, accentColor.G, accentColor.B));
        resources["TimelineSelectBorderBrush"] = new SolidColorBrush(
            Color.FromArgb(0xCC, accentColor.R, accentColor.G, accentColor.B));
        resources["TimelineSelectionBorderBrush"] = accentBrush;
        resources["TimelineSelectionGlowBrush"] = new SolidColorBrush(
            Color.FromArgb(isDark ? (byte)0x1F : (byte)0x1A, accentColor.R, accentColor.G, accentColor.B));
        resources["TimelineSelectionHandleBrush"] = accentBrush;
    }

    private static Color ResolvePillColor(Color accentColor, bool isDark, bool isAzure)
    {
        if (isAzure) return Color.Parse("#CDE2F5");
        if (!isDark && accentColor == Color.Parse("#527CE6")) return Color.Parse("#D2DEF5");
        if (isDark && accentColor == Color.Parse("#78A0F0")) return Color.Parse("#2A3856");
        var surface = isDark ? Color.Parse("#21252E") : Avalonia.Media.Colors.White;
        return BlendColor(surface, accentColor, isDark ? 0.24 : 0.16);
    }

    private static Color ResolveListHoverColor(Color accentColor, bool isDark, bool isAzure)
    {
        if (isAzure) return Color.Parse("#BFD8EE");
        if (!isDark && accentColor == Color.Parse("#527CE6")) return Color.Parse("#C5D5F2");
        if (isDark && accentColor == Color.Parse("#78A0F0")) return Color.Parse("#324367");
        var surface = isDark ? Color.Parse("#21252E") : Avalonia.Media.Colors.White;
        return BlendColor(surface, accentColor, isDark ? 0.30 : 0.22);
    }

    private static Color ResolveWebBadgeForeground(Color accentColor, bool isDark, bool isAzure)
    {
        if (isAzure) return Color.Parse("#2C68A3");
        if (!isDark && accentColor == Color.Parse("#527CE6")) return Color.Parse("#3E67CE");
        if (isDark && accentColor == Color.Parse("#78A0F0")) return Color.Parse("#8DB1F6");
        var target = isDark ? Avalonia.Media.Colors.White : Avalonia.Media.Colors.Black;
        return BlendColor(accentColor, target, 0.22);
    }

    private static Color BlendColor(Color from, Color to, double weight)
    {
        byte Mix(byte a, byte b) => (byte)Math.Clamp((int)Math.Round(a + (b - a) * weight), 0, 255);
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    private static AppTheme GetTheme(int index) =>
        index >= 0 && index < Definitions.Length ? (AppTheme)index : AppTheme.System;
}
