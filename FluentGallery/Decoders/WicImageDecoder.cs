using FluentGallery.Helpers;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FluentGallery.Decoders;

/// <summary>
/// Image decoder backed by Windows Imaging Component (WIC) via
/// <see cref="BitmapDecoder"/> / <see cref="BitmapEncoder"/>.
/// <para>
/// <see cref="IsAvailable"/> checks (once, lazily) whether every extension in
/// <see cref="SupportedExtensions"/> has a registered WIC codec.  This lets a
/// HEIC-only instance return <c>false</c> when the HEVC Video Extensions are
/// absent, allowing <see cref="ImageDecoderPipeline"/> to fall back to a
/// built-in decoder such as <see cref="MagickImageDecoder"/>.
/// </para>
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    // ── Pre-built factory methods ─────────────────────────────────────────────

    /// <summary>
    /// Returns a WIC decoder for common formats that are always present on Windows.
    /// </summary>
    public static WicImageDecoder CreateForStandardFormats() =>
        new([".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"]);

    /// <summary>
    /// Returns a WIC decoder scoped to HEIC/HEIF.
    /// <see cref="IsAvailable"/> will be <c>false</c> when the HEVC codec is absent,
    /// allowing <see cref="ImageDecoderPipeline"/> to try a built-in fallback next.
    /// </summary>
    public static WicImageDecoder CreateForHeic() =>
        new([".heic", ".heif"]);

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly IReadOnlyList<string> _extensions;
    private readonly Lazy<bool>            _isAvailable;

    public WicImageDecoder(string[] extensions)
    {
        _extensions  = extensions;
        _isAvailable = new Lazy<bool>(() => CheckCodecAvailability(extensions));
    }

    // ── IImageDecoder ─────────────────────────────────────────────────────────

    public IReadOnlyList<string> SupportedExtensions => _extensions;

    /// <inheritdoc/>
    public bool IsAvailable => _isAvailable.Value;

    /// <inheritdoc/>
    /// WIC HEIC/HEIF codecs are NOT concurrent-safe (COM crash under parallel MTA calls).
    /// Standard-format WIC codecs (JPEG, PNG, etc.) are safe.
    public bool SupportsConcurrentDecode =>
        _extensions.All(e => !string.Equals(e, ".heic", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(e, ".heif", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public async Task<DecodedImageData> DecodeAsync(
        string filePath, uint maxWidth, uint maxHeight, CancellationToken ct)
    {
        ThreadGuard.EnsureBackground();
        ct.ThrowIfCancellationRequested();

        // Use StorageFile.OpenAsync to get a native WinRT IRandomAccessStream.
        // File.OpenRead().AsRandomAccessStream() produces an STA-bound wrapper;
        // when WIC (MTA) reads it, every buffer read must marshal back through
        // the calling thread's STA, flooding the UI message pump.
        var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(ct).ConfigureAwait(false);
        using var srcRas = await storageFile.OpenAsync(FileAccessMode.Read).AsTask(ct).ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(srcRas).AsTask(ct).ConfigureAwait(false);

        uint physW = decoder.PixelWidth;
        uint physH = decoder.PixelHeight;

        ushort exifOrient = await ReadExifOrientationAsync(decoder, ct).ConfigureAwait(false);
        bool   rotSwaps   = exifOrient is 5 or 6 or 7 or 8;

        // Post-rotation logical dimensions
        (uint logW, uint logH) = rotSwaps ? (physH, physW) : (physW, physH);

        uint finalW, finalH, scaleW, scaleH;
        if (maxWidth > 0 && maxHeight > 0)
        {
            (finalW, finalH) = FitInside(logW, logH, maxWidth, maxHeight);
            // BitmapTransform scales BEFORE rotating, so swap back for 90°/270°
            (scaleW, scaleH) = rotSwaps ? (finalH, finalW) : (finalW, finalH);
        }
        else
        {
            // Full resolution
            finalW = logW;  finalH = logH;
            scaleW = physW; scaleH = physH;
        }

        var transform = new BitmapTransform
        {
            ScaledWidth       = scaleW,
            ScaledHeight      = scaleH,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Rotation          = ExifToRotation(exifOrient),
            Flip              = ExifToFlip(exifOrient),
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);

        return new DecodedImageData(
            pixelData.DetachPixelData(),
            finalW, finalH,
            decoder.DpiX, decoder.DpiY);
    }

    // ── Codec availability ────────────────────────────────────────────────────

    private static bool CheckCodecAvailability(string[] extensions)
    {
        try
        {
            var registered = BitmapDecoder.GetDecoderInformationEnumerator()
                .SelectMany(info => info.FileExtensions)
                .Select(e => e.ToLowerInvariant())
                .ToHashSet();

            return extensions.All(ext => registered.Contains(ext));
        }
        catch
        {
            return false;
        }
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the largest (w, h) that fits inside maxW×maxH while preserving aspect ratio.
    /// </summary>
    internal static (uint w, uint h) FitInside(uint srcW, uint srcH, uint maxW, uint maxH)
    {
        if (srcW == 0 || srcH == 0) return (Math.Max(1u, maxW), Math.Max(1u, maxH));

        double scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
        return ((uint)Math.Max(1, Math.Round(srcW * scale)),
                (uint)Math.Max(1, Math.Round(srcH * scale)));
    }

    // ── EXIF helpers ──────────────────────────────────────────────────────────

    private static async Task<ushort> ReadExifOrientationAsync(
        BitmapDecoder decoder, CancellationToken ct)
    {
        try
        {
            var props = await decoder.BitmapProperties
                .GetPropertiesAsync(["System.Photo.Orientation"]).AsTask(ct).ConfigureAwait(false);

            if (props.TryGetValue("System.Photo.Orientation", out var v)
                && v.Value is ushort orient)
                return orient;
        }
        catch { /* no EXIF tag or unreadable */ }

        return 1;
    }

    internal static BitmapRotation ExifToRotation(ushort orient) => orient switch
    {
        3 or 4 => BitmapRotation.Clockwise180Degrees,
        5 or 6 => BitmapRotation.Clockwise90Degrees,
        7 or 8 => BitmapRotation.Clockwise270Degrees,
        _      => BitmapRotation.None,
    };

    internal static BitmapFlip ExifToFlip(ushort orient) => orient switch
    {
        2 or 7 => BitmapFlip.Horizontal,
        4 or 5 => BitmapFlip.Vertical,
        _      => BitmapFlip.None,
    };
}
