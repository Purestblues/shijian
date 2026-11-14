using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ShiJian.Client.Logging;
using ShiJian.Client.Shared.Librarys;

namespace ShiJian.Client.Librarys.Image;

public enum IconTone
{
    Neutral,
    LightForeground,
    DarkForeground
}

public class Imager
{
    private static volatile Bitmap? _defaultBitmap;
    private static readonly object DefaultBitmapLock = new();
    private const string DefaultAssetPath = "avares://ShiJian/Resources/Icons/defaultIcon.png";

    private static readonly SimpleLruCache Cache = new(capacity: 256);
    private static readonly ConcurrentDictionary<string, IconTone> ToneCache = new();
    private static readonly SemaphoreSlim DecodeSemaphore = new(4, 4);

    public static bool TryGetFromCache(string filePath, out Bitmap? bitmap, int decodeWidth = 0, int decodeHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            bitmap = GetDefaultBitmap();
            return true;
        }
        var failureKey = NormalizeFailureKey(filePath);
        if (Cache.IsFailed(failureKey))
        {
            bitmap = null;
            return false;
        }
        return Cache.TryGet(BuildCacheKey(filePath, decodeWidth, decodeHeight), out bitmap);
    }

    /// <summary>
    /// 位图由缓存统一持有、交给 GC 回收，控件不参与释放。
    /// </summary>
    public static void Release(Bitmap? bitmap) { }

    public static bool IsFailed(string filePath, int decodeWidth = 0, int decodeHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return Cache.IsFailed(NormalizeFailureKey(filePath))
            || Cache.IsFailed(BuildCacheKey(filePath, decodeWidth, decodeHeight));
    }

    public static async Task<Bitmap> LoadAsync(string filePath, string? defaultPath = null, int decodeWidth = 0, int decodeHeight = 0, bool noCache = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return GetDefaultBitmap(defaultPath);

        var cacheKey = BuildCacheKey(filePath, decodeWidth, decodeHeight);
        var failureKey = NormalizeFailureKey(filePath);

        if (!noCache && (Cache.IsFailed(failureKey) || Cache.IsFailed(cacheKey)))
            return GetDefaultBitmap(defaultPath);

        if (!noCache && Cache.TryGet(cacheKey, out var cachedBitmap))
            return cachedBitmap;

        var sw = Stopwatch.StartNew();
        Logger.Debug($"[Imager] 开始异步加载: {filePath}, decode=({decodeWidth}x{decodeHeight})");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = await Task.Run(() => TryReadFileBytes(filePath), cancellationToken);
            if (bytes == null)
            {
                Logger.Debug($"[Imager] 文件不存在或无法读取: {filePath}");
                Cache.MarkFailed(failureKey);
                return GetDefaultBitmap(defaultPath);
            }

            // WebFavicons 里有一批 0 字节或 HTML/404 文本的下载失败产物，直接按文件维度标记失败并降级，
            // 避免在多尺寸 DecodeWidth 下反复送入解码器触发 NullReferenceException 并刷日志
            if (bytes.Length == 0 || !IsSupportedImageHeader(bytes))
            {
                Logger.Debug($"[Imager] 跳过无效或非图片文件: {filePath}, size={bytes.Length} bytes");
                if (!noCache)
                    Cache.MarkFailed(failureKey);
                return GetDefaultBitmap(defaultPath);
            }

            Logger.Debug($"[Imager] 文件读取完成 [{sw.ElapsedMilliseconds}ms]: {filePath}, size={bytes.Length} bytes");

            Bitmap bitmap;
            await DecodeSemaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bitmap = await Task.Run(() => BytesToBitmap(bytes, decodeWidth, decodeHeight, cancellationToken), cancellationToken);
                Logger.Debug($"[Imager] 解码完成 [{sw.ElapsedMilliseconds}ms]: {filePath}, pixel={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}");
            }
            finally
            {
                DecodeSemaphore.Release();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!ToneCache.ContainsKey(failureKey))
                ToneCache.TryAdd(failureKey, ClassifyIconTone(bitmap));

            if (noCache)
            {
                Logger.Debug($"[Imager] 加载完成(无缓存) [{sw.ElapsedMilliseconds}ms]: {filePath}");
                return bitmap;
            }
            var result = Cache.Add(cacheKey, bitmap, failureKey);
            Logger.Debug($"[Imager] 加载完成 [{sw.ElapsedMilliseconds}ms]: {filePath}");
            return result;
        }
        catch (OperationCanceledException)
        {
            Logger.Debug($"[Imager] 加载取消 [{sw.ElapsedMilliseconds}ms]: {filePath}");
            throw;
        }
        catch (Exception ex)
        {
            // 损坏的 ico/png 会让解码器抛异常，属于可预期的数据问题，按文件维度记住失败状态
            Logger.Debug($"[Imager] 图片无法解码，已降级: {filePath} ({ex.GetType().Name})");
            if (!noCache)
                Cache.MarkFailed(failureKey);
            return GetDefaultBitmap(defaultPath);
        }
    }

    public static IconTone GetIconTone(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return IconTone.Neutral;
        return ToneCache.TryGetValue(NormalizeFailureKey(filePath), out var tone)
            ? tone
            : IconTone.Neutral;
    }

    internal static IconTone ClassifyIconTone(Bitmap bitmap)
    {
        try
        {
            if (bitmap is WriteableBitmap wb)
            {
                using var fb = wb.Lock();
                var fbFormat = fb.Format;
                if (fbFormat != PixelFormat.Bgra8888 && fbFormat != PixelFormat.Rgba8888)
                    return IconTone.Neutral;

                var fbW = fb.Size.Width;
                var fbH = fb.Size.Height;
                var fbStride = fb.RowBytes;
                if (fbW <= 0 || fbH <= 0 || fbStride <= 0) return IconTone.Neutral;

                var fbBytes = fbStride * fbH;
                if (fbBytes <= 0 || fbBytes > 1024 * 1024)
                    return IconTone.Neutral;

                var fbBuffer = new byte[fbBytes];
                Marshal.Copy(fb.Address, fbBuffer, 0, fbBytes);
                return ClassifyPixelBuffer(
                    fbBuffer,
                    fbW,
                    fbH,
                    fbStride,
                    fbFormat == PixelFormat.Bgra8888,
                    wb.AlphaFormat == AlphaFormat.Premul);
            }

            var format = bitmap.Format;
            if (format != PixelFormat.Bgra8888 && format != PixelFormat.Rgba8888)
                return IconTone.Neutral;

            var w = bitmap.PixelSize.Width;
            var h = bitmap.PixelSize.Height;
            if (w <= 0 || h <= 0) return IconTone.Neutral;

            var stride = w * 4;
            var totalBytes = stride * h;
            if (totalBytes <= 0 || totalBytes > 1024 * 1024)
                return IconTone.Neutral;

            var buffer = new byte[totalBytes];
            var ptr = Marshal.AllocHGlobal(totalBytes);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, w, h), ptr, totalBytes, stride);
                Marshal.Copy(ptr, buffer, 0, totalBytes);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return ClassifyPixelBuffer(
                buffer,
                w,
                h,
                stride,
                format == PixelFormat.Bgra8888,
                bitmap.AlphaFormat == AlphaFormat.Premul);
        }
        catch
        {
            return IconTone.Neutral;
        }
    }

    private static IconTone ClassifyPixelBuffer(
        byte[] buffer,
        int w,
        int h,
        int stride,
        bool isBgra,
        bool premul)
    {
        var stepX = Math.Max(1, w / 16);
        var stepY = Math.Max(1, h / 16);
        var opaqueCount = 0;
        var lightNeutral = 0;
        var darkNeutral = 0;

        for (var y = 0; y < h; y += stepY)
        {
            var rowStart = y * stride;
            for (var x = 0; x < w; x += stepX)
            {
                var idx = rowStart + x * 4;
                var a = buffer[idx + 3];
                if (a < 64) continue;

                double r = isBgra ? buffer[idx + 2] : buffer[idx];
                double g = buffer[idx + 1];
                double b = isBgra ? buffer[idx] : buffer[idx + 2];

                if (premul && a < 255)
                {
                    var inv = 255.0 / a;
                    r = Math.Min(255.0, r * inv);
                    g = Math.Min(255.0, g * inv);
                    b = Math.Min(255.0, b * inv);
                }

                opaqueCount++;
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max - min > 55) continue;

                var lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                if (lum >= 0.78) lightNeutral++;
                else if (lum <= 0.14) darkNeutral++;
            }
        }

        if (opaqueCount < 4) return IconTone.Neutral;
        if ((double)lightNeutral / opaqueCount >= 0.75) return IconTone.LightForeground;
        if ((double)darkNeutral / opaqueCount >= 0.80) return IconTone.DarkForeground;
        return IconTone.Neutral;
    }

    internal static string NormalizeFailureKey(string filePath)
        => filePath.Trim().Replace('\\', '/');

    internal static bool IsSupportedImageHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return false;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return true;

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;

        // GIF: GIF8
        if (bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8')
            return true;

        // BMP: BM
        if (bytes.Length >= 14 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return true;

        // WEBP: RIFF....WEBP
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return true;

        // ICO (00 00 01 00) / CUR (00 00 02 00), count > 0
        if (bytes.Length >= 6 &&
            bytes[0] == 0x00 && bytes[1] == 0x00 &&
            (bytes[2] == 0x01 || bytes[2] == 0x02) && bytes[3] == 0x00 &&
            (bytes[4] != 0 || bytes[5] != 0))
            return true;

        // TIFF: II*\0 / MM\0*
        if ((bytes[0] == (byte)'I' && bytes[1] == (byte)'I' && bytes[2] == 0x2A && bytes[3] == 0x00) ||
            (bytes[0] == (byte)'M' && bytes[1] == (byte)'M' && bytes[2] == 0x00 && bytes[3] == 0x2A))
            return true;

        // AVIF / HEIF / HEIC: ....ftyp
        if (bytes.Length >= 12 &&
            bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
            return true;

        return false;
    }

    public static Bitmap GetDefaultBitmap(string? defaultPath = null)
    {
        if (!string.IsNullOrWhiteSpace(defaultPath))
        {
            try
            {
                return LoadDefaultBitmapFromPath(defaultPath);
            }
            catch
            {
                // ignored
            }
        }

        if (_defaultBitmap != null)
            return _defaultBitmap;

        lock (DefaultBitmapLock)
        {
            _defaultBitmap ??= LoadDefaultBitmap();
            return _defaultBitmap;
        }
    }

    private static Bitmap LoadDefaultBitmap()
    {
        try
        {
            var uri = new Uri(DefaultAssetPath);
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Logger.Error($"默认图片加载失败：{DefaultAssetPath}", ex);
            throw;
        }
    }

    private static Bitmap LoadDefaultBitmapFromPath(string path)
    {
        if (path.StartsWith("avares://", StringComparison.Ordinal))
        {
            var uri = new Uri(path);
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }

        var fullPath = TryResolveFullPath(path);
        if (fullPath == null)
            throw new FileNotFoundException("默认图片文件不存在", path);

        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new Bitmap(fs);
    }

    private static string? TryResolveFullPath(string filePath)
    {
        var normalizedPath = filePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedPath))
            return File.Exists(normalizedPath) ? normalizedPath : null;

        var root = FileHelper.GetIconsDirectory();

        var fullPath = Path.Combine(root, normalizedPath);
        if (File.Exists(fullPath)) return fullPath;

        // Windows 下服务端的数据目录是 <exe>\Data（应用图标落在 <exe>\Data\AppIcons），
        // 而网页图标落在 <exe>\WebFavicons；同一份相对路径必须两个根目录都试
        var dataFullPath = Path.Combine(root, "Data", normalizedPath);
        return File.Exists(dataFullPath) ? dataFullPath : null;
    }

    private static byte[]? TryReadFileBytes(string filePath)
    {
        if (filePath.StartsWith("avares://", StringComparison.Ordinal))
        {
            try
            {
                var uri = new Uri(filePath);
                using var stream = AssetLoader.Open(uri);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        var fullPath = TryResolveFullPath(filePath);
        if (fullPath == null)
            return null;

        try
        {
            return File.ReadAllBytes(fullPath);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap BytesToBitmap(byte[] bytes, int decodeWidth = 0, int decodeHeight = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var ms = new MemoryStream(bytes);
        return DecodeFromStream(ms, decodeWidth, decodeHeight);
    }

    private static Bitmap DecodeFromStream(Stream stream, int decodeWidth, int decodeHeight)
    {
        if (decodeWidth > 0)
            return Bitmap.DecodeToWidth(stream, decodeWidth);
        if (decodeHeight > 0)
            return Bitmap.DecodeToHeight(stream, decodeHeight);
        return new Bitmap(stream);
    }

    private static string BuildCacheKey(string filePath, int decodeWidth, int decodeHeight)
    {
        if (decodeWidth > 0 && decodeHeight > 0)
            return $"{filePath}#w{decodeWidth}h{decodeHeight}";
        if (decodeWidth > 0)
            return $"{filePath}#w{decodeWidth}";
        if (decodeHeight > 0)
            return $"{filePath}#h{decodeHeight}";
        return filePath;
    }

    internal class SimpleLruCache
    {
        private readonly int _capacity;
        private readonly Dictionary<string, Bitmap> _cache = new();
        private readonly LinkedList<string> _lru = new();
        private readonly ConcurrentDictionary<string, byte> _failed = new();
        private readonly object _lock = new();

        public SimpleLruCache(int capacity)
        {
            _capacity = capacity;
        }

        public bool TryGet(string key, out Bitmap? bitmap)
        {
            bitmap = null;

            if (_failed.ContainsKey(key))
                return false;

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out bitmap))
                {
                    _lru.Remove(key);
                    _lru.AddFirst(key);
                    return true;
                }
            }

            return false;
        }

        public bool IsFailed(string key) => _failed.ContainsKey(key);

        public Bitmap Add(string key, Bitmap bitmap, string? failureKey = null)
        {
            _failed.TryRemove(key, out _);
            if (!string.IsNullOrEmpty(failureKey))
                _failed.TryRemove(failureKey, out _);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var existing))
                {
                    _lru.Remove(key);
                    _lru.AddFirst(key);
                    bitmap.Dispose();
                    return existing;
                }

                // 淘汰只丢引用，绝不 Dispose：同一张位图可能还挂在若干个 Img 控件上，
                // 一旦被释放，下一次渲染会在 Avalonia.Controls.Image.Render 里抛
                // ObjectDisposedException（Ref<IBitmapImpl>），直接崩掉整个进程。
                // 位图没有其他引用时由 GC 回收，代价远小于一次闪退。
                while (_cache.Count >= _capacity)
                {
                    var last = _lru.Last;
                    if (last == null) break;

                    var oldKey = last.Value;
                    _lru.RemoveLast();
                    _cache.Remove(oldKey);
                }

                _cache[key] = bitmap;
                _lru.AddFirst(key);
                return bitmap;
            }
        }

        public void MarkFailed(string key)
        {
            _failed[key] = 0;
        }

        /// <summary>
        /// 只清空引用，不 Dispose——原因同淘汰逻辑：位图可能正在被控件渲染。
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _lru.Clear();
            }
            _failed.Clear();
        }
    }
}
