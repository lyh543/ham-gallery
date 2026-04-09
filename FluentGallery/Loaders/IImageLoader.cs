using Microsoft.UI.Xaml.Media;

namespace FluentGallery.Loaders;

/// <summary>
/// A decoded image ready for display. <see cref="Source"/> is either a
/// <see cref="Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource"/> (ready immediately,
/// <see cref="PixelWidth"/> &gt; 0) or a
/// <see cref="Microsoft.UI.Xaml.Media.Imaging.BitmapImage"/> (decoded lazily by the XAML
/// framework; <see cref="PixelWidth"/> == 0 until <c>ImageOpened</c> fires, or &gt; 0 if
/// the preloaded bitmap is already decoded).
/// Sources backed by <see cref="Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource"/>
/// implement <see cref="IDisposable"/> and must be disposed by the caller when done.
/// <see cref="Microsoft.UI.Xaml.Media.Imaging.BitmapImage"/> is not disposable; the XAML
/// framework and GC manage its lifetime.
/// </summary>
public sealed class LoadedImage(ImageSource source, int pixelWidth, int pixelHeight)
{
    public ImageSource Source      { get; } = source;
    public int         PixelWidth  { get; } = pixelWidth;
    public int         PixelHeight { get; } = pixelHeight;
}

/// <summary>
/// Abstraction for format-specific image loading with an internal preload cache.
/// Both <see cref="WicImageLoader"/> and <see cref="MagickImageLoader"/> implement this
/// interface and are decoupled from any UI component.
/// </summary>
public interface IImageLoader
{
    /// <summary>Returns <c>true</c> when this loader handles the given file extension.</summary>
    bool IsSupported(string extension);

    /// <summary>
    /// Starts a background preload for <paramref name="filePath"/> and stores the result
    /// in the internal preload cache. Safe to fire-and-forget.
    /// Cancelling <paramref name="ct"/> aborts the preload without affecting the cache.
    /// </summary>
    Task PreloadAsync(string filePath, CancellationToken ct);

    /// <summary>
    /// Returns a <see cref="LoadedImage"/> for <paramref name="filePath"/>, using the
    /// internal preload cache if available. Must be called from the UI thread.
    /// Returns <c>null</c> when the file cannot be decoded.
    /// The caller owns the returned <see cref="LoadedImage.Source"/> and must dispose it
    /// when done if it implements <see cref="IDisposable"/>.
    /// </summary>
    Task<LoadedImage?> LoadAsync(string filePath, CancellationToken ct);

    /// <summary>Clears and disposes the internal preload cache.</summary>
    void ClearCache();

    /// <summary>
    /// Maximum number of entries kept in the internal preload cache.
    /// Set to <c>PreloadCountBack + PreloadCountForward + 1</c>.
    /// </summary>
    int MaxCacheSize { get; set; }
}
