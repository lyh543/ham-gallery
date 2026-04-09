using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentGallery.Loaders;

/// <summary>
/// Image loader for WIC-natively supported formats (JPEG, PNG, WebP, BMP, TIFF, GIF).
/// <para>
/// Uses <see cref="BitmapImage"/> with <see cref="BitmapImage.UriSource"/> for all
/// standard formats. The XAML framework handles decoding on an internal background
/// thread — no user-code WIC access, no serialisation gate required, no
/// <see cref="System.IDisposable"/> lifecycle management.
/// </para>
/// <para>
/// <b>Preloading:</b> <see cref="PreloadAsync"/> creates a <see cref="BitmapImage"/>
/// immediately (synchronous), which triggers background decoding. When the same path
/// is later requested via <see cref="LoadAsync"/>, the cached (possibly already-decoded)
/// <see cref="BitmapImage"/> is returned.  If <see cref="BitmapImage.PixelWidth"/> &gt; 0
/// the image is ready and <see cref="ZoomableImage"/> shows it without a loading spinner.
/// </para>
/// <para>
/// <b>GIF:</b> returned as <see cref="BitmapImage"/> (URI-based, lazy) so animated GIFs
/// continue to work.
/// </para>
/// <para>
/// <b>Thread safety:</b> must be called from the UI thread.
/// </para>
/// </summary>
public sealed class WicImageLoader : IImageLoader
{
    // Explicit whitelist of formats that BitmapImage.UriSource handles reliably.
    // Unknown or exotic formats fall through to MagickImageLoader.
    private static readonly HashSet<string> _supportedExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".gif" };

    private readonly ILogger<WicImageLoader> _logger;

    // Preload cache: stores BitmapImage objects (not IDisposable — GC handles lifetime).
    // Ownership is transferred to the caller when LoadAsync pulls an entry out.
    private readonly Dictionary<string, BitmapImage> _preloadCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _insertionOrder = [];

    /// <inheritdoc/>
    public int MaxCacheSize { get; set; } = 11;

    public WicImageLoader(ILogger<WicImageLoader> logger)
    {
        _logger = logger;
    }

    // ── IImageLoader ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsSupported(string extension) => _supportedExts.Contains(extension);

    /// <inheritdoc/>
    /// Creates a <see cref="BitmapImage"/> with <see cref="BitmapImage.UriSource"/>,
    /// which triggers XAML-framework background decoding immediately.
    /// Must be called from the UI thread.
    public Task PreloadAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_preloadCache.ContainsKey(filePath)) return Task.CompletedTask;

        var bmp = new BitmapImage(new Uri(filePath));
        AddToPreloadCache(filePath, bmp);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<LoadedImage?> LoadAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        BitmapImage bmp;
        if (_preloadCache.TryGetValue(filePath, out var cached))
        {
            _preloadCache.Remove(filePath);
            _insertionOrder.Remove(filePath);
            bmp = cached;
        }
        else
        {
            bmp = new BitmapImage(new Uri(filePath));
        }

        // Return current pixel dimensions: > 0 means already decoded (show immediately),
        // 0 means still decoding (ZoomableImage will wait for ImageOpened).
        return Task.FromResult<LoadedImage?>(
            new LoadedImage(bmp, bmp.PixelWidth, bmp.PixelHeight));
    }

    /// <inheritdoc/>
    public void ClearCache()
    {
        // BitmapImage is not IDisposable — just drop the references and let GC handle it.
        _preloadCache.Clear();
        _insertionOrder.Clear();
        _logger.LogDebug("WicImageLoader: preload cache cleared");
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private void AddToPreloadCache(string path, BitmapImage bmp)
    {
        _preloadCache[path] = bmp;
        _insertionOrder.Remove(path);
        _insertionOrder.Add(path);

        // Evict oldest entries over the cache size limit.
        // No Dispose needed — BitmapImage is not IDisposable.
        while (_insertionOrder.Count > MaxCacheSize)
        {
            var oldest = _insertionOrder[0];
            _insertionOrder.RemoveAt(0);
            _preloadCache.Remove(oldest);
        }

        _logger.LogDebug(
            "WicCache [{Count}/{Max}] First={First} Last={Last} Added={Added}",
            _insertionOrder.Count,
            MaxCacheSize,
            Path.GetFileName(_insertionOrder[0]),
            Path.GetFileName(_insertionOrder[^1]),
            Path.GetFileName(path));
    }
}
