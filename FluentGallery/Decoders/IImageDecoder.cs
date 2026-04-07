namespace FluentGallery.Decoders;

/// <summary>
/// Decoded image output: BGRA8 pixel data with EXIF orientation already applied.
/// The pixel layout matches <c>Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8</c>.
/// </summary>
/// <param name="Pixels">Raw BGRA8 pixel data (un-premultiplied).
/// When creating a <c>SoftwareBitmapSource</c>, convert to Premultiplied alpha first
/// via <c>SoftwareBitmap.Convert</c>.</param>
/// <param name="Width">Image width in pixels (post-rotation).</param>
/// <param name="Height">Image height in pixels (post-rotation).</param>
/// <param name="DpiX">Horizontal DPI (96 if unknown).</param>
/// <param name="DpiY">Vertical DPI (96 if unknown).</param>
public sealed record DecodedImageData(
    byte[] Pixels,
    uint   Width,
    uint   Height,
    double DpiX,
    double DpiY
);

/// <summary>
/// Generic image decoder abstraction.
/// Implementations back specific formats or codec stacks (WIC, Magick.NET, libheif, …).
/// Register implementations in <see cref="ImageDecoderPipeline"/> to enable
/// format-based selection with automatic fallback support.
/// </summary>
public interface IImageDecoder
{
    /// <summary>
    /// File extensions handled by this decoder.
    /// Values must be lowercase and include the leading dot (e.g. <c>".heic"</c>).
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// <c>true</c> when this decoder is usable in the current environment.
    /// For example, <see cref="WicImageDecoder"/> for HEIC/HEIF returns <c>false</c>
    /// when no WIC HEIC codec (HEVC Video Extensions) is installed, allowing the
    /// pipeline to fall back to a built-in decoder.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// <c>true</c> when this decoder is safe to call concurrently from multiple threads.
    /// The WIC HEIC codec (<see cref="WicImageDecoder"/> for HEIC/HEIF) is <c>false</c>
    /// because its COM implementation crashes under concurrent MTA access.
    /// <see cref="MagickImageDecoder"/> (libheif) is always <c>true</c>.
    /// </summary>
    bool SupportsConcurrentDecode { get; }

    /// <summary>
    /// Decodes the image at <paramref name="filePath"/> and returns BGRA8 pixel data
    /// with EXIF orientation applied; the output is already correctly oriented.
    /// <para>
    /// When both <paramref name="maxWidth"/> and <paramref name="maxHeight"/> are
    /// non-zero the image is scaled to fit inside that bounding box while preserving
    /// aspect ratio.  Pass 0 / 0 to decode at the original (full) resolution.
    /// </para>
    /// </summary>
    Task<DecodedImageData> DecodeAsync(
        string            filePath,
        uint              maxWidth  = 0,
        uint              maxHeight = 0,
        CancellationToken ct        = default);
}
