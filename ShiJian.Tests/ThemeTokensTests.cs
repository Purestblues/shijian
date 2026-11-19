using System;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using ShiJian.Client.Servicers;
using Xunit;

namespace ShiJian.Tests;

public class ThemeTokensTests
{
    private static readonly string[] RequiredColorBrushes =
    [
        "DividerDefaultBrush",
        "ChartRefLineBrush",
        "ThemeAccentBrush",
        "ErrorBrush",
        "DividerSubtleBrush",
        "DividerStrongBrush",
        "Surface0Brush",
        "Surface1Brush",
        "Surface2Brush",
        "Surface3Brush",
        "Surface4Brush",
        "PopupBackgroundBrush",
        "PopupBorderBrush",
        "DefaultTextBrush",
        "SubTextBrush",
        "OnBrandBrush"
    ];

    private static readonly string[] RequiredElevations =
    [
        "ElevationCard",
        "Elevation1",
        "Elevation2",
        "Elevation3"
    ];

    private static readonly string[] RequiredRadii =
    [
        "RadiusXs",
        "RadiusSmall",
        "RadiusMedium",
        "RadiusLarge",
        "RadiusXLarge",
        "RadiusPill"
    ];

    private static readonly string[] RequiredSpaces =
    [
        "Space1",
        "Space2",
        "Space3",
        "Space4",
        "Space5",
        "Space6",
        "Space8",
        "Space10",
        "Space18"
    ];

    private static readonly string[] RequiredMotions =
    [
        "MotionXs",
        "MotionS",
        "MotionM",
        "MotionL"
    ];

    [AvaloniaFact]
    public void TokenDictionaries_ResolveAcrossDarkLightAndAzureVariants()
    {
        var app = Application.Current!;
        var originalVariant = app.RequestedThemeVariant;

        try
        {
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeServicer.Azure })
            {
                app.RequestedThemeVariant = variant;

                foreach (var key in RequiredColorBrushes)
                {
                    Assert.True(app.TryGetResource(key, variant, out var res) && res is IBrush,
                        $"Resource '{key}' failed to resolve as IBrush in variant '{variant.Key}'.");
                }

                foreach (var key in RequiredElevations)
                {
                    Assert.True(app.TryGetResource(key, variant, out var res) && res is BoxShadows,
                        $"Resource '{key}' failed to resolve as BoxShadows in variant '{variant.Key}'.");
                }
            }

            foreach (var key in RequiredRadii)
            {
                Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var res) && res is CornerRadius,
                    $"Radius token '{key}' missing.");
            }

            Assert.True(app.TryGetResource("RadiusXLarge", ThemeVariant.Dark, out var xlarge) &&
                        xlarge is CornerRadius { TopLeft: 16 });

            foreach (var key in RequiredSpaces)
            {
                Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var res) && res is double,
                    $"Space token '{key}' missing.");
            }

            foreach (var key in RequiredMotions)
            {
                Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var res) && res is TimeSpan,
                    $"Motion token '{key}' missing.");
            }

            Assert.True(app.TryGetResource("FontSizeSmall", ThemeVariant.Dark, out var fontSizeSmall) &&
                        fontSizeSmall is double and 12);
            Assert.True(app.TryGetResource("ThemeDisabledOpacity", ThemeVariant.Dark, out var disabledOpacity) &&
                        disabledOpacity is double and > 0);
        }
        finally
        {
            app.RequestedThemeVariant = originalVariant;
        }
    }
}
