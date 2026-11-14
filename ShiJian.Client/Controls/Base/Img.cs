using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using ShiJian.Client.Librarys.Image;
using ShiJian.Client.Logging;

namespace ShiJian.Client.Controls.Base;

public class Img : TemplatedControl
{
    public static readonly StyledProperty<CornerRadius> RadiusProperty =
        AvaloniaProperty.Register<Img, CornerRadius>(nameof(Radius));

    public static readonly StyledProperty<IImage> ResourceProperty =
        AvaloniaProperty.Register<Img, IImage>(nameof(Resource));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<Img, int>(nameof(DecodeWidth));

    public static readonly StyledProperty<int> DecodeHeightProperty =
        AvaloniaProperty.Register<Img, int>(nameof(DecodeHeight));

    public static readonly StyledProperty<bool> NoCacheProperty =
        AvaloniaProperty.Register<Img, bool>(nameof(NoCache));

    public static readonly DirectProperty<Img, string> URLProperty =
        AvaloniaProperty.RegisterDirect<Img, string>(
            nameof(URL),
            o => o.URL,
            (o, v) => o.URL = v);

    /// <summary>图标加载失败时用来当占位符的文字（域名 / 应用名），只取首字符</summary>
    public static readonly StyledProperty<string?> FallbackTextProperty =
        AvaloniaProperty.Register<Img, string?>(nameof(FallbackText));

    public static readonly DirectProperty<Img, string> FallbackGlyphProperty =
        AvaloniaProperty.RegisterDirect<Img, string>(nameof(FallbackGlyph), o => o.FallbackGlyph);

    public static readonly DirectProperty<Img, IBrush?> FallbackBrushProperty =
        AvaloniaProperty.RegisterDirect<Img, IBrush?>(nameof(FallbackBrush), o => o.FallbackBrush);

    public static readonly DirectProperty<Img, bool> IsFallbackVisibleProperty =
        AvaloniaProperty.RegisterDirect<Img, bool>(nameof(IsFallbackVisible), o => o.IsFallbackVisible);

    public static readonly DirectProperty<Img, Geometry?> SvgGeometryProperty =
        AvaloniaProperty.RegisterDirect<Img, Geometry?>(nameof(SvgGeometry), o => o.SvgGeometry);

    public static readonly DirectProperty<Img, bool> HasSvgProperty =
        AvaloniaProperty.RegisterDirect<Img, bool>(nameof(HasSvg), o => o.HasSvg);

    public static readonly DirectProperty<Img, IBrush> IconPlateBackgroundProperty =
        AvaloniaProperty.RegisterDirect<Img, IBrush>(nameof(IconPlateBackground), o => o.IconPlateBackground);

    public static readonly DirectProperty<Img, Thickness> IconPlatePaddingProperty =
        AvaloniaProperty.RegisterDirect<Img, Thickness>(nameof(IconPlatePadding), o => o.IconPlatePadding);

    private string _url = string.Empty;
    private CancellationTokenSource? _cts;
    private string? _loadingUrl;
    private string _fallbackGlyph = string.Empty;
    private IBrush? _fallbackBrush = Brushes.Transparent;
    private bool _isFallbackVisible;
    private bool _loadFailed;
    private Geometry? _svgGeometry;
    private bool _hasSvg;
    private IBrush _iconPlateBackground = Brushes.Transparent;
    private Thickness _iconPlatePadding;

    private static readonly IBrush DarkContrastPlateBrush = new SolidColorBrush(Color.Parse("#3B4252"));
    private static readonly IBrush LightContrastPlateBrush = new SolidColorBrush(Color.Parse("#D8DEE9"));

    /// <summary>占位色：同一域名每次都拿到同一个颜色</summary>
    private static readonly string[] FallbackPalette =
    [
        "#7C6BF0", "#3D8BE0", "#2FA98C", "#D08A2C", "#D45F80", "#6E7FA8", "#9A6BD0", "#4F9E6A"
    ];

    public Img()
    {
        Resource = Imager.GetDefaultBitmap();
    }

    public CornerRadius Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public IImage Resource
    {
        get => GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public int DecodeHeight
    {
        get => GetValue(DecodeHeightProperty);
        set => SetValue(DecodeHeightProperty, value);
    }

    public bool NoCache
    {
        get => GetValue(NoCacheProperty);
        set => SetValue(NoCacheProperty, value);
    }

    public string URL
    {
        get => _url;
        set => SetAndRaise(URLProperty, ref _url, value);
    }

    public string? FallbackText
    {
        get => GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    /// <summary>占位符上显示的首字符</summary>
    public string FallbackGlyph
    {
        get => _fallbackGlyph;
        private set => SetAndRaise(FallbackGlyphProperty, ref _fallbackGlyph, value);
    }

    public IBrush? FallbackBrush
    {
        get => _fallbackBrush;
        private set => SetAndRaise(FallbackBrushProperty, ref _fallbackBrush, value);
    }

    public bool IsFallbackVisible
    {
        get => _isFallbackVisible;
        private set => SetAndRaise(IsFallbackVisibleProperty, ref _isFallbackVisible, value);
    }

    public Geometry? SvgGeometry
    {
        get => _svgGeometry;
        private set => SetAndRaise(SvgGeometryProperty, ref _svgGeometry, value);
    }

    public bool HasSvg
    {
        get => _hasSvg;
        private set => SetAndRaise(HasSvgProperty, ref _hasSvg, value);
    }

    public IBrush IconPlateBackground
    {
        get => _iconPlateBackground;
        private set => SetAndRaise(IconPlateBackgroundProperty, ref _iconPlateBackground, value);
    }

    public Thickness IconPlatePadding
    {
        get => _iconPlatePadding;
        private set => SetAndRaise(IconPlatePaddingProperty, ref _iconPlatePadding, value);
    }

    protected override Type StyleKeyOverride => typeof(Img);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == FallbackTextProperty)
            UpdateFallback();

        if (e.Property == URLProperty && !Equals(e.OldValue, e.NewValue))
        {
            UpdateFallback();
            ReloadFromUrl();
        }
    }

    private void UpdateFallback()
    {
        var seed = StripWww(!string.IsNullOrWhiteSpace(FallbackText) ? FallbackText! : GuessSeedFromUrl(URL));
        FallbackGlyph = FirstGlyph(seed);
        FallbackBrush = FallbackGlyph.Length == 0
            ? Brushes.Transparent
            : new SolidColorBrush(Color.Parse(FallbackPalette[(int)(Hash(seed) % FallbackPalette.Length)]));
        UpdateFallbackVisibility();
    }

    /// <summary>域名带 www. 时首字母应该取主域名，否则一整排网站都是 W</summary>
    private static string StripWww(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? trimmed[4..] : trimmed;
    }

    /// <summary>
    /// 占位符只在“图标确实加载失败”时出现。URL / 占位文字的绑定先后不固定，
    /// 所以每次都重新算一遍可见性。
    /// </summary>
    private void UpdateFallbackVisibility()
    {
        IsFallbackVisible = _loadFailed && FallbackGlyph.Length > 0;
    }

    private static string GuessSeedFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var name = url.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];

        // 站点图标落地是 site_<id>.png，文件名本身没有可读信息，交给调用方传域名
        return name.StartsWith("site_", StringComparison.OrdinalIgnoreCase) ? string.Empty : name;
    }

    private static string FirstGlyph(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        }

        return string.Empty;
    }

    private static uint Hash(string text)
    {
        // FNV-1a：简单稳定，换机器 / 重启后颜色不变
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        return hash;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnAppThemeChanged;

        if (!string.IsNullOrWhiteSpace(URL) && !HasSvg && Resource == Imager.GetDefaultBitmap())
            ReloadFromUrl();
        else
            UpdateIconPlate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged -= OnAppThemeChanged;
        CancelLoading();
        // Popup 关闭时内容可能被 detach，但不重置 Resource
        // 如果使用了 ShouldUseOverlayLayer="True"，每次关闭都会触发 detach
        // 正常情况下 Popup 只是隐藏，不会触发 detach
    }

    private void OnAppThemeChanged(object? sender, EventArgs e) => UpdateIconPlate();

    private void UpdateIconPlate()
    {
        if (HasSvg || IsFallbackVisible || _loadFailed || string.IsNullOrWhiteSpace(URL))
        {
            IconPlateBackground = Brushes.Transparent;
            IconPlatePadding = default;
            return;
        }

        var tone = Imager.GetIconTone(URL);
        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        if (!isDark && tone == IconTone.LightForeground)
        {
            IconPlateBackground = DarkContrastPlateBrush;
            IconPlatePadding = new Thickness(2);
        }
        else if (isDark && tone == IconTone.DarkForeground)
        {
            IconPlateBackground = LightContrastPlateBrush;
            IconPlatePadding = new Thickness(2);
        }
        else
        {
            IconPlateBackground = Brushes.Transparent;
            IconPlatePadding = default;
        }
    }

    private double GetDpiScale()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.RenderScaling is > 0 and var scale) return scale;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.RenderScaling is > 0 and var mainScale)
            return mainScale;

        return 2.0;
    }

    private void ReloadFromUrl()
    {
        var path = URL;

        if (_loadingUrl == path && _cts != null && !_cts.IsCancellationRequested)
            return;

        CancelLoading();
        _loadFailed = false;

        if (string.IsNullOrWhiteSpace(path))
        {
            SvgGeometry = null;
            HasSvg = false;
            Resource = Imager.GetDefaultBitmap();
            UpdateFallbackVisibility();
            return;
        }

        if (CategoryIconCatalog.TryGetGeometry(path, out var svgGeometry))
        {
            SvgGeometry = svgGeometry;
            HasSvg = true;
            Resource = Imager.GetDefaultBitmap();
            UpdateFallbackVisibility();
            UpdateIconPlate();
            return;
        }

        SvgGeometry = null;
        HasSvg = false;

        var dpiScale = GetDpiScale();
        var decodeWidth = DecodeWidth > 0 ? (int)(DecodeWidth * dpiScale) : 0;
        var decodeHeight = DecodeHeight > 0 ? (int)(DecodeHeight * dpiScale) : 0;

        if (!NoCache && Imager.TryGetFromCache(path, out var cached, decodeWidth, decodeHeight))
        {
            Resource = cached ?? Imager.GetDefaultBitmap();
            UpdateFallbackVisibility();
            UpdateIconPlate();
            return;
        }

        if (!NoCache && Imager.IsFailed(path, decodeWidth, decodeHeight))
        {
            Resource = Imager.GetDefaultBitmap();
            _loadFailed = true;
            UpdateFallbackVisibility();
            UpdateIconPlate();
            return;
        }

        Resource = Imager.GetDefaultBitmap();
        UpdateFallbackVisibility();
        UpdateIconPlate();

        _cts = new CancellationTokenSource();
        _loadingUrl = path;
        _ = LoadAsync(path, _cts.Token, decodeWidth, decodeHeight);
    }

    private void CancelLoading()
    {
        _loadingUrl = null;
        if (_cts == null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }

    private async Task LoadAsync(string path, CancellationToken token, int decodeWidth, int decodeHeight)
    {
        try
        {
            var bitmap = await Imager.LoadAsync(
                path,
                decodeWidth: decodeWidth,
                decodeHeight: decodeHeight,
                noCache: NoCache,
                cancellationToken: token);

            token.ThrowIfCancellationRequested();

            if (URL != path)
                return;

            Resource = bitmap;
            _loadFailed = ReferenceEquals(bitmap, Imager.GetDefaultBitmap());
            UpdateFallbackVisibility();
            UpdateIconPlate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error($"[Img] 加载失败: {path}", ex);
        }
        finally
        {
            if (_loadingUrl == path)
                _loadingUrl = null;
        }
    }
}
