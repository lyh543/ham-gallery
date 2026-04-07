using FluentGallery.Decoders;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentGallery.Loaders;

/// <summary>
/// Image loader for HEIC/HEIF files.
///
/// <para>
/// <b>Preload strategy:</b> decodes HEIC → raw BGRA8 pixels via
/// <see cref="ImageDecoderPipeline"/> with <c>concurrentSafe: true</c> (which selects
/// <see cref="MagickImageDecoder"/>, skipping the thread-unsafe WIC HEIC codec), then
/// encodes the pixels to PNG bytes in memory. PNG bytes are stored in an internal cache.
/// </para>
/// <para>
/// <b>Display:</b> PNG bytes → <see cref="BitmapImage"/> via
/// <see cref="BitmapImage.SetSourceAsync"/> on the UI thread. The WIC PNG codec is
/// thread-safe and fast, so this display step is safe and imperceptible to the user.
/// </para>
/// <para>
/// <b>Crash prevention:</b> a <see cref="SemaphoreSlim"/>(1,1) shared between
/// <see cref="PreloadAsync"/> and <see cref="LoadAsync"/> ensures all decode calls
/// are serialised, eliminating the concurrent-WIC crash that occurs during rapid
/// photo navigation.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="LoadAsync"/> must be called from the UI thread
/// because <see cref="BitmapImage.SetSourceAsync"/> requires it.
/// <see cref="PreloadAsync"/> can be fire-and-forgotten from any thread.
/// </para>
/// </summary>
public sealed class HeicImageLoader : IImageLoader
{
    private static readonly HashSet<string> _heicExts =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    private readonly ImageDecoderPipeline _pipeline;
    private readonly ILogger<HeicImageLoader> _logger;

    // Serialises all decode operations (preload + direct load) to prevent
    // concurrent WIC HEIC codec calls, which crash via COM access violation.
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // PNG bytes cache (FIFO, bounded).
    private readonly Dictionary<string, byte[]> _pngCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _insertionOrder = [];

    /// <inheritdoc/>
    public int MaxCacheSize { get; set; } = 11;

    public HeicImageLoader(ImageDecoderPipeline pipeline, ILogger<HeicImageLoader> logger)
    {
        _pipeline = pipeline;
        _logger   = logger;
    }

    // ── IImageLoader ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsSupported(string extension) => _heicExts.Contains(extension);

    /// <inheritdoc/>
    public Task PreloadAsync(string filePath, CancellationToken ct)
    {
        if (_pngCache.ContainsKey(filePath)) return Task.CompletedTask;
        // Fire-and-forget the background work; caller does not await.
        return PreloadInternalAsync(filePath, ct);
    }

    private async Task PreloadInternalAsync(string filePath, CancellationToken ct)
    {
        try
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_pngCache.ContainsKey(filePath)) return;
                var decoded = await _pipeline
                    .TryDecodeAsync(filePath, 0, 0, ct, concurrentSafe: true)
                    .ConfigureAwait(false);
                if (decoded is null) return;
                var bytes = await EncodeToPngBytesAsync(decoded, ct).ConfigureAwait(false);
                AddToCache(filePath, bytes);
            }
            finally { _semaphore.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "HeicImageLoader preload failed: {Path}", filePath); }
    }

    /// <inheritdoc/>
    /// Must be called from the UI thread.
    public async Task<BitmapImage?> LoadAsync(string filePath, CancellationToken ct)
    {
        // Run decode + encode on a background thread so we don't block the UI.
        // Intentionally NO ConfigureAwait(false) on Task.Run: we need the continuation
        // to resume on the UI SynchronizationContext for BitmapImage + SetSourceAsync.
#pragma warning disable CAC001
        var pngBytes = await Task.Run(async () =>
#pragma warning restore CAC001
        {
            if (_pngCache.TryGetValue(filePath, out var cached)) return cached;

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_pngCache.TryGetValue(filePath, out var cached2)) return cached2;
                var decoded = await _pipeline
                    .TryDecodeAsync(filePath, 0, 0, ct, concurrentSafe: true)
                    .ConfigureAwait(false);
                if (decoded is null) return null;
                var bytes = await EncodeToPngBytesAsync(decoded, ct).ConfigureAwait(false);
                AddToCache(filePath, bytes);
                return bytes;
            }
            finally { _semaphore.Release(); }
        }, ct);

        if (pngBytes is null) return null;
        ct.ThrowIfCancellationRequested();

        // Now on the UI thread — BitmapImage and SetSourceAsync are safe.
        using var stream = new MemoryStream(pngBytes).AsRandomAccessStream();
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        return bmp;
    }

    /// <inheritdoc/>
    public void ClearCache()
    {
        _pngCache.Clear();
        _insertionOrder.Clear();
    }

    // ── PNG encoding ──────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="decoded"/> (BGRA8) to PNG bytes using WIC's in-memory encoder.
    /// Testable without a UI dispatcher — does not create any WinUI objects.
    /// </summary>
    internal static async Task<byte[]> EncodeToPngBytesAsync(
        DecodedImageData decoded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var memRas  = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder
            .CreateAsync(BitmapEncoder.PngEncoderId, memRas)
            .AsTask(ct)
            .ConfigureAwait(false);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            decoded.Width,
            decoded.Height,
            decoded.DpiX,
            decoded.DpiY,
            decoded.Pixels);

        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

        memRas.Seek(0);
        var bytes = new byte[memRas.Size];
        using var reader = new DataReader(memRas);
        await reader.LoadAsync((uint)bytes.Length).AsTask(ct).ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private void AddToCache(string path, byte[] bytes)
    {
        _pngCache[path] = bytes;
        _insertionOrder.Remove(path);
        _insertionOrder.Add(path);

        while (_insertionOrder.Count > MaxCacheSize)
        {
            var oldest = _insertionOrder[0];
            _insertionOrder.RemoveAt(0);
            _pngCache.Remove(oldest);
        }

        long totalBytes = _pngCache.Values.Sum(b => (long)b.Length);
        _logger.LogDebug(
            "HeicCache [{Count}/{Max}] First={First} Last={Last} Added={Added} TotalMB={TotalMB:F1}",
            _insertionOrder.Count,
            MaxCacheSize,
            Path.GetFileName(_insertionOrder[0]),
            Path.GetFileName(_insertionOrder[^1]),
            Path.GetFileName(path),
            totalBytes / (1024.0 * 1024.0));
    }
}
