using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentGallery.Loaders;

/// <summary>
/// Abstraction for format-specific image loading with an internal preload cache.
/// Both <see cref="WicImageLoader"/> and <see cref="HeicImageLoader"/> implement this
/// interface and are decoupled from any UI component — they return <see cref="BitmapImage"/>
/// which the caller assigns to the appropriate UI element.
/// </summary>
public interface IImageLoader
{
    /// <summary>Returns <c>true</c> when this loader handles the given file extension.</summary>
    bool IsSupported(string extension);

    /// <summary>
    /// Starts a background preload for <paramref name="filePath"/> and stores the result
    /// in the internal cache. Safe to fire-and-forget (<c>_ = loader.PreloadAsync(...)</c>).
    /// Cancelling <paramref name="ct"/> aborts the preload without affecting the cache.
    /// </summary>
    Task PreloadAsync(string filePath, CancellationToken ct);

    /// <summary>
    /// Returns a <see cref="BitmapImage"/> for <paramref name="filePath"/>, using the
    /// internal cache if available. Must be called from the UI thread because
    /// <see cref="BitmapImage"/> creation and <c>SetSourceAsync</c> require it.
    /// Returns <c>null</c> when the file cannot be decoded.
    /// </summary>
    Task<BitmapImage?> LoadAsync(string filePath, CancellationToken ct);

    /// <summary>Clears the internal preload cache.</summary>
    void ClearCache();

    /// <summary>
    /// Maximum number of entries kept in the internal cache.
    /// Set to <c>PreloadCountBack + PreloadCountForward + 1</c> so the cache covers the current photo
    /// plus all preloaded neighbours in both directions.
    /// </summary>
    int MaxCacheSize { get; set; }
}
