using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Simple;
using System;

[assembly: AvaloniaTestApplication(typeof(ShiJian.Tests.TestAppBuilder))]

namespace ShiJian.Tests;

/// <summary>
/// 测试宿主：只加载产品自带的资源字典，不跑真正的 <c>App</c>（那会去连服务端）。
/// 主题里有嵌套选择器之类的错误时，这里会直接炸——正是我们想要的回归保护。
/// </summary>
public class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());

        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://ShiJian/"))
        {
            Source = new Uri("avares://ShiJian/Resources/Themes/Theme.axaml")
        });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://ShiJian/"))
        {
            Source = new Uri("avares://ShiJian/Resources/Themes/Common.axaml")
        });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://ShiJian/"))
        {
            Source = new Uri("avares://ShiJian/Themes/Generic.axaml")
        });

        // 文案用的是 DynamicResource，测试里直接挂中文资源（正式运行由 SystemLanguage 挂）
        Resources.MergedDictionaries.Add(new ShiJian.Client.Resources.Localization.StringResourcesCn());

        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // 启用 Skia 位图与真实渲染后端，避免 HeadlessBitmapStub 吞掉像素读写测试
                UseHeadlessDrawing = false
            });
}
