using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentGallery.Loaders;

/// <summary>
/// Image loader for WIC-natively supported formats (JPEG, PNG, WebP, BMP, TIFF, GIF).
/// <para>
/// <b>Full-size images (non-GIF):</b> decoded to <see cref="SoftwareBitmapSource"/> so the
/// caller can call <see cref="IDisposable.Dispose"/> to release GPU memory immediately.
/// Neighbouring photos are preloaded into <see cref="_preloadCache"/>; <see cref="LoadAsync"/>
/// transfers ownership out of the cache to the caller.
/// </para>
/// <para>
/// <b>GIF:</b> returned as <see cref="BitmapImage"/> (URI-based, lazy) so animated GIFs
/// continue to work. <see cref="LoadedImage.PixelWidth"/> is 0 until <c>ImageOpened</c> fires.
/// </para>
/// <para>
/// <b>Thumbnails:</b> loaded on demand via <see cref="LoadAsync"/>; not preloaded.
/// The caller (<see cref="FluentGallery.ViewModels.PhotoItemViewModel"/>) owns and disposes
/// the returned <see cref="SoftwareBitmapSource"/> when the item is recycled.
/// </para>
/// <para>
/// <b>Thread safety:</b> must be called from the UI thread.
/// </para>
/// </summary>
public sealed class WicImageLoader : IImageLoader
{
    private static readonly HashSet<string> _heicExts =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    private static readonly HashSet<string> _gifExts =
        new(StringComparer.OrdinalIgnoreCase) { ".gif" };

    private readonly ILogger<WicImageLoader> _logger;

    // Preload cache: stores SoftwareBitmapSources for neighbouring photos.
    // Ownership is transferred to the caller when LoadAsync pulls an entry out.
    // Any entry that remains here when ClearCache() is called is safely disposed
    // (it has never been handed to a consumer).
    private readonly Dictionary<string, LoadedImage> _preloadCache =
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
    public bool IsSupported(string extension) => !_heicExts.Contains(extension);

    /// <inheritdoc/>
    public Task PreloadAsync(string filePath, CancellationToken ct)
    {
        if (_gifExts.Contains(Path.GetExtension(filePath))) return Task.CompletedTask;
        if (_preloadCache.ContainsKey(filePath))            return Task.CompletedTask;
        return PreloadInternalAsync(filePath, ct);
    }

    private async Task PreloadInternalAsync(string filePath, CancellationToken ct)
    {
        try
        {
#pragma warning disable CAC002
            var loaded = await DecodeToLoadedImageAsync(filePath, ct, WicPriority.Low).ConfigureAwait(true);
#pragma warning restore CAC002
            if (loaded is null) return;

            if (_preloadCache.ContainsKey(filePath))
            {
                // A concurrent LoadAsync already decoded it — discard our copy.
                (loaded.Source as IDisposable)?.Dispose();
                return;
            }

            AddToPreloadCache(filePath, loaded);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "WicImageLoader preload failed: {Path}", filePath); }
    }

    /// <inheritdoc/>
    public async Task<LoadedImage?> LoadAsync(string filePath, CancellationToken ct,
        WicPriority priority = WicPriority.High)
    {
        ct.ThrowIfCancellationRequested();

        // GIF: return BitmapImage so animations work (SoftwareBitmapSource doesn't animate).
        if (_gifExts.Contains(Path.GetExtension(filePath)))
            return new LoadedImage(new BitmapImage(new Uri(filePath)), 0, 0);

        // Transfer ownership from preload cache to caller.
        if (_preloadCache.TryGetValue(filePath, out var cached))
        {
            _preloadCache.Remove(filePath);
            _insertionOrder.Remove(filePath);
            return cached;
        }

#pragma warning disable CAC002
        return await DecodeToLoadedImageAsync(filePath, ct, priority).ConfigureAwait(true);
#pragma warning restore CAC002
    }

    /// <inheritdoc/>
    public void ClearCache()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        foreach (var entry in _preloadCache.Values)
        {
            if (entry.Source is IDisposable d)
                dq?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => { try { d.Dispose(); } catch { } });
        }

        _preloadCache.Clear();
        _insertionOrder.Clear();
        _logger.LogDebug("WicImageLoader: preload cache cleared and disposed");
    }

    // ── Decode helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a WIC-supported image file to a <see cref="SoftwareBitmapSource"/>.
    /// Must resume on the UI thread so <see cref="SoftwareBitmapSource.SetBitmapAsync"/> is safe.
    /// </summary>
#pragma warning disable CAC001
    private async Task<LoadedImage?> DecodeToLoadedImageAsync(string filePath, CancellationToken ct,
        WicPriority priority)
    {
        ct.ThrowIfCancellationRequested();

        // Decode on a background thread; Task.Run without ConfigureAwait(false) resumes
        // on the UI SynchronizationContext, making SetBitmapAsync safe.
        // WIC BitmapDecoder is serialised via WicGate to prevent native crashes from
        // concurrent WIC COM access on MTA threads.
        var (softwareBitmap, w, h) = await Task.Run(async () =>
#pragma warning restore CAC001
        {
            await WicGate.WaitAsync(priority, ct).ConfigureAwait(false);
            try
            {
                using var stream  = File.OpenRead(filePath).AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
                var sb = await decoder
                    .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                    .AsTask(ct)
                    .ConfigureAwait(false);
                return (sb, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
            }
            finally { WicGate.Release(); }
        }, ct);

        ct.ThrowIfCancellationRequested();

        // Now on the UI thread.
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(softwareBitmap);
        softwareBitmap.Dispose();

        return new LoadedImage(source, w, h);
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private void AddToPreloadCache(string path, LoadedImage loaded)
    {
        _preloadCache[path] = loaded;
        _insertionOrder.Remove(path);
        _insertionOrder.Add(path);

        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        while (_insertionOrder.Count > MaxCacheSize)
        {
            var oldest = _insertionOrder[0];
            _insertionOrder.RemoveAt(0);
            if (_preloadCache.Remove(oldest, out var evicted) && evicted.Source is IDisposable d)
                dq?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => { try { d.Dispose(); } catch { } });
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
