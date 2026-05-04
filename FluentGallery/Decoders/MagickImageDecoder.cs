using ImageMagick;

namespace FluentGallery.Decoders;

/// <summary>
/// Image decoder backed by Magick.NET (ImageMagick).
/// Used as a built-in fallback for HEIC/HEIF when the system WIC HEIC codec
/// (HEVC Video Extensions) is not installed.
/// <para>
/// Magick.NET bundles its own codec pipeline via libheif (version 14+), so this
/// decoder is always available regardless of system-installed codecs.
/// </para>
/// <para>
/// <b>Extensibility:</b> to add support for additional formats (e.g. AVIF, JXL),
/// create another <see cref="MagickImageDecoder"/>-like class or extend this one
/// and register it in <see cref="ImageDecoderPipeline"/>.
/// </para>
/// </summary>
public sealed class MagickImageDecoder : IImageDecoder
{
    private static readonly string[] _extensions = [".heic", ".heif"];

    public IReadOnlyList<string> SupportedExtensions => _extensions;

    /// <summary>
    /// Always <c>true</c>: Magick.NET bundles its own HEIC codec via libheif
    /// and does not depend on any system-installed codec.
    /// </summary>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    /// Magick.NET (libheif) is thread-safe for concurrent decode calls.
    public bool SupportsConcurrentDecode => true;

    /// <inheritdoc/>
    public Task<DecodedImageData> DecodeAsync(
        string filePath, uint maxWidth, uint maxHeight, CancellationToken ct)
    {
        // MagickImage operations are CPU-bound and synchronous; run on the thread pool.
        return Task.Run(() => Decode(filePath, maxWidth, maxHeight, ct), ct);
    }

    private static DecodedImageData Decode(
        string filePath, uint maxWidth, uint maxHeight, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var image = new MagickImage(filePath);

        // Apply EXIF orientation so output pixels are already correctly oriented
        image.AutoOrient();

        ct.ThrowIfCancellationRequested();

        // Scale to fit inside maxWidth × maxHeight (aspect-ratio preserving)
        if (maxWidth > 0 && maxHeight > 0)
            image.Resize(new MagickGeometry(maxWidth, maxHeight));

        // Normalise to sRGB so colours match WIC output
        image.ColorSpace = ColorSpace.sRGB;

        // Ensure an alpha channel exists before exporting BGRA
        image.Alpha(AlphaOption.Set);

        double dpiX = image.Density.X > 0 ? image.Density.X : 96.0;
        double dpiY = image.Density.Y > 0 ? image.Density.Y : 96.0;

        // Export as BGRA8 — matches Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8
        byte[] pixels;
        using (var pc = image.GetPixels())
        {
            pixels = pc.ToByteArray("BGRA")
                ?? throw new InvalidOperationException(
                    $"Magick.NET: failed to extract BGRA pixel data from '{filePath}'");
        }

        return new DecodedImageData(
            pixels,
            (uint)image.Width,
            (uint)image.Height,
            dpiX,
            dpiY);
    }
}
