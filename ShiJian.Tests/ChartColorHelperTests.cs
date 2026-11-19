using Avalonia.Media;
using ShiJian.Client.Librarys;
using Xunit;

namespace ShiJian.Tests;

public class ChartColorHelperTests
{
    private static readonly Color LightBackground = Color.Parse("#ECEFF4");
    private static readonly Color DarkBackground = Color.Parse("#181A20");

    [Fact]
    public void RelativeLuminance_MatchesKnownValues()
    {
        Assert.Equal(1d, ChartColorHelper.RelativeLuminance(Colors.White), 3);
        Assert.Equal(0d, ChartColorHelper.RelativeLuminance(Colors.Black), 3);
    }

    [Fact]
    public void ContrastRatio_BlackOnWhite_Is21()
    {
        Assert.Equal(21d, ChartColorHelper.ContrastRatio(Colors.Black, Colors.White), 2);
    }

    [Fact]
    public void ContrastRatio_IsSymmetric()
    {
        var a = Color.Parse("#87CEEB");
        var b = Color.Parse("#131315");
        Assert.Equal(ChartColorHelper.ContrastRatio(a, b), ChartColorHelper.ContrastRatio(b, a), 6);
    }

    [Theory]
    [InlineData("#E4E3DF")] // 曾经几乎等于浅色背景的「未分类」颜色
    [InlineData("#FFFFFF")]
    [InlineData("#F5F5F5")]
    [InlineData("#87CEEB")]
    public void ResolveFor_LightBackground_ReachesTargetContrast(string color)
    {
        var resolved = Color.Parse(ChartColorHelper.ResolveFor(color, false, LightBackground));
        var contrast = ChartColorHelper.ContrastRatio(resolved, LightBackground);

        Assert.True(contrast >= ChartColorHelper.TargetContrast,
            $"{color} 在浅色背景下对比度只有 {contrast:F2}");
    }

    [Theory]
    [InlineData("#1C1C20")]
    [InlineData("#2A2A31")]
    [InlineData("#003366")]
    [InlineData("#000000")]
    public void ResolveFor_DarkBackground_ReachesTargetContrast(string color)
    {
        var resolved = Color.Parse(ChartColorHelper.ResolveFor(color, false, DarkBackground));
        var contrast = ChartColorHelper.ContrastRatio(resolved, DarkBackground);

        Assert.True(contrast >= ChartColorHelper.TargetContrast,
            $"{color} 在深色背景下对比度只有 {contrast:F2}");
    }

    [Fact]
    public void ResolveFor_AlreadyCompliantColor_IsNotChanged()
    {
        // 深蓝在浅色背景上本来就够对比，不该被压暗
        const string color = "#1B3A8C";
        Assert.True(ChartColorHelper.ContrastRatio(Color.Parse(color), LightBackground) > ChartColorHelper.TargetContrast);

        Assert.Equal(color.ToUpperInvariant(), ChartColorHelper.ResolveFor(color, false, LightBackground));
    }

    [Fact]
    public void ResolveFor_InvalidColor_FallsBackInsteadOfThrowing()
    {
        var resolved = ChartColorHelper.ResolveFor("not-a-color", false, LightBackground);

        Assert.StartsWith("#", resolved);
        Assert.Equal(7, resolved.Length);
    }

    [Fact]
    public void ResolveFor_SystemCategory_UsesAccentColor()
    {
        // 系统分类（未分类）不看原色，跟随主题色
        var fromSystem = ChartColorHelper.ResolveFor(null, true, LightBackground);
        var fromAccent = ChartColorHelper.ResolveFor(ChartColorHelper.AccentHex, false, LightBackground);

        Assert.Equal(fromAccent, fromSystem);
    }

    [Theory]
    [InlineData("#FF1BBC", (byte)0x2E, "#2EFF1BBC")]
    [InlineData("#00FF88", (byte)0xFF, "#FF00FF88")]
    public void WithAlpha_PrependsAlphaChannel(string color, byte alpha, string expected)
    {
        Assert.Equal(expected, ChartColorHelper.WithAlpha(color, alpha));
    }

    [Fact]
    public void WithAlpha_InvalidInput_ReturnsInputUnchanged()
    {
        Assert.Equal(string.Empty, ChartColorHelper.WithAlpha(null, 0x10));
        Assert.Equal("oops", ChartColorHelper.WithAlpha("oops", 0x10));
    }

    [Fact]
    public void BuildRamp_Produces10StopsWithMonotonicLuminance()
    {
        var baseColor = Color.Parse("#527CE6");
        var ramp = ChartColorHelper.BuildRamp(baseColor);

        int[] expectedStops = [50, 100, 200, 300, 400, 500, 600, 700, 800, 900];
        Assert.Equal(expectedStops.Length, ramp.Count);
        foreach (var stop in expectedStops)
            Assert.True(ramp.ContainsKey(stop));

        Assert.Equal(baseColor, ramp[500]);

        for (var i = 1; i < expectedStops.Length; i++)
        {
            var prevLum = ChartColorHelper.RelativeLuminance(ramp[expectedStops[i - 1]]);
            var currLum = ChartColorHelper.RelativeLuminance(ramp[expectedStops[i]]);
            Assert.True(prevLum > currLum,
                $"Stop {expectedStops[i - 1]} luminance ({prevLum:F4}) should exceed stop {expectedStops[i]} ({currLum:F4})");
        }
    }

    [Fact]
    public void GetShadeFor_ReturnsNearestRampStop()
    {
        var baseColor = Color.Parse("#1B3A8C");
        var expectedRamp = ChartColorHelper.BuildRamp(baseColor);

        Assert.Equal(expectedRamp[200], ChartColorHelper.GetShadeFor("#1B3A8C", 210, false, LightBackground));
        Assert.Equal(expectedRamp[800], ChartColorHelper.GetShadeFor("#1B3A8C", 800, false, LightBackground));
    }

    [Fact]
    public void GetHeatColor_LightDarkensAndDarkBrightensMonotonically()
    {
        var accent = Color.Parse("#527CE6");
        double[] levels = [0.05, 0.25, 0.55, 0.85, 1.0];

        var lightLums = levels
            .Select(l => ChartColorHelper.RelativeLuminance(ChartColorHelper.GetHeatColor(accent, l, isDark: false)))
            .ToArray();
        var darkLums = levels
            .Select(l => ChartColorHelper.RelativeLuminance(ChartColorHelper.GetHeatColor(accent, l, isDark: true)))
            .ToArray();

        for (var i = 1; i < levels.Length; i++)
        {
            Assert.True(lightLums[i] <= lightLums[i - 1] + 1e-6);
            Assert.True(darkLums[i] >= darkLums[i - 1] - 1e-6);
        }

        Assert.True(lightLums[0] > lightLums[^1]);
        Assert.True(darkLums[^1] > darkLums[0]);
    }
}
