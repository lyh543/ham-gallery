namespace FluentGallery.Decoders;

/// <summary>
/// Manages a priority-ordered registry of <see cref="IImageDecoder"/> implementations
/// and selects the first available one for a given file extension.
/// <para>
/// Decoders are registered via <see cref="Register"/> in descending priority order
/// (first registered = highest priority).  <see cref="GetDecoder"/> returns the
/// first decoder whose <see cref="IImageDecoder.IsAvailable"/> is <c>true</c>.
/// </para>
/// <example>
/// Typical registration for HEIC/HEIF with WIC-first and Magick.NET fallback:
/// <code>
/// var pipeline = new ImageDecoderPipeline();
/// pipeline.Register(WicImageDecoder.CreateForStandardFormats()); // always available
/// pipeline.Register(WicImageDecoder.CreateForHeic());            // priority 1: uses system WIC
/// pipeline.Register(new MagickImageDecoder());                   // priority 2: built-in fallback
/// </code>
/// </example>
/// </summary>
public sealed class ImageDecoderPipeline
{
    private readonly Dictionary<string, List<IImageDecoder>> _byExtension =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a decoder.  Decoders registered first have higher priority.
    /// </summary>
    public void Register(IImageDecoder decoder)
    {
        foreach (var ext in decoder.SupportedExtensions)
        {
            if (!_byExtension.TryGetValue(ext, out var list))
                _byExtension[ext] = list = [];
            list.Add(decoder);
        }
    }

    /// <summary>
    /// Returns the highest-priority available decoder for the given file path,
    /// or <c>null</c> when no decoder is registered or available for that extension.
    /// </summary>
    public IImageDecoder? GetDecoder(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        return _byExtension.TryGetValue(ext, out var list)
            ? list.FirstOrDefault(d => d.IsAvailable)
            : null;
    }

    /// <summary>
    /// <c>true</c> when at least one available decoder is registered for
    /// <paramref name="filePath"/>'s extension.
    /// </summary>
    public bool CanDecode(string filePath) => GetDecoder(filePath) is not null;

    /// <summary>
    /// Tries each available decoder for <paramref name="filePath"/>'s extension in
    /// priority order, advancing to the next one if a decoder throws (e.g. a WIC
    /// codec that is registered but fails at runtime due to a partial/broken install).
    /// Returns <c>null</c> when all available decoders are exhausted without success.
    /// <para>
    /// <see cref="OperationCanceledException"/> is always re-thrown immediately.
    /// </para>
    /// </summary>
    public async Task<DecodedImageData?> TryDecodeAsync(
        string            filePath,
        uint              maxWidth  = 0,
        uint              maxHeight = 0,
        CancellationToken ct        = default)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        if (!_byExtension.TryGetValue(ext, out var decoders)) return null;

        foreach (var decoder in decoders.Where(d => d.IsAvailable))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await decoder.DecodeAsync(filePath, maxWidth, maxHeight, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* this decoder failed — try the next one */ }
        }

        return null;
    }
}
