using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentGallery.Loaders;

/// <summary>
/// Image loader for WIC-natively supported formats (JPEG, PNG, GIF, WebP, BMP, TIFF).
/// Uses <see cref="BitmapImage.UriSource"/> for background decode — no pipeline involvement.
/// <para>
/// Also used for thumbnail display on list pages: the thumbnail is always a JPEG on disk,
/// so <see cref="BitmapImage.UriSource"/> is the correct and sufficient approach.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="PreloadAsync"/> and <see cref="LoadAsync"/> must be
/// called from the UI thread because <see cref="BitmapImage"/> requires the WinUI
/// dispatcher for creation.
/// </para>
/// </summary>
public sealed class WicImageLoader : IImageLoader
{
    private static readonly HashSet<string> _heicExts =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    // FIFO cache bounded by MaxCacheSize.
    private readonly Dictionary<string, BitmapImage> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _insertionOrder = [];
    private const int MaxCacheSize = 11;

    // ── IImageLoader ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsSupported(string extension) => !_heicExts.Contains(extension);

    /// <inheritdoc/>
    public Task PreloadAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_cache.ContainsKey(filePath))
            AddToCache(filePath, new BitmapImage(new Uri(filePath)));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<BitmapImage?> LoadAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(filePath, out var cached))
            return Task.FromResult<BitmapImage?>(cached);

        var bmp = new BitmapImage(new Uri(filePath));
        AddToCache(filePath, bmp);
        return Task.FromResult<BitmapImage?>(bmp);
    }

    /// <inheritdoc/>
    public void ClearCache()
    {
        _cache.Clear();
        _insertionOrder.Clear();
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private void AddToCache(string path, BitmapImage bmp)
    {
        _cache[path] = bmp;
        _insertionOrder.Remove(path); // remove if re-inserted
        _insertionOrder.Add(path);

        while (_insertionOrder.Count > MaxCacheSize)
        {
            var oldest = _insertionOrder[0];
            _insertionOrder.RemoveAt(0);
            _cache.Remove(oldest);
        }
    }
}
