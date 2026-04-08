namespace FluentGallery.Loaders;

/// <summary>
/// Global serialisation gate for all WIC (Windows Imaging Component) operations
/// that run on thread-pool threads.
/// <para>
/// WIC COM objects (<see cref="Windows.Graphics.Imaging.BitmapDecoder"/>,
/// <see cref="Windows.Graphics.Imaging.BitmapEncoder"/>, etc.) are not safe for
/// concurrent access from multiple MTA threads.  Concurrent calls cause native
/// crashes (<c>STATUS_STOWED_EXCEPTION 0xC000027B</c>) that bypass all managed
/// exception handlers.
/// </para>
/// </summary>
internal static class WicGate
{
    internal static readonly SemaphoreSlim Semaphore = new(1, 1);
}
