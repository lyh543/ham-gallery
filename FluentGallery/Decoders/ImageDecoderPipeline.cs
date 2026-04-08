namespace FluentGallery.Decoders;

/// <summary>
/// Manages a priority-ordered registry of <see cref="IImageDecoder"/> implementations
/// and selects the first available one for a given file extension.
/// <para>
/// Decoders are registered via <see cref="Register"/> in descending priority order
/// (first registered = highest priority).  <see cref="GetDecoder"/> returns the
/// first decoder whose <see cref="IImageDecoder.IsAvailable"/> is <c>true</c>.
/// </para>
/// <para>
/// When all extension-based decoders fail, <see cref="TryDecodeAsync"/> falls back to
/// magic-byte sniffing to handle files whose extension does not match their actual
/// format (e.g. a HEIC file saved with a <c>.jpg</c> extension).
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
    /// <param name="filePath">Source file path (extension is used for lookup).</param>
    /// <param name="concurrentSafe">
    /// When <c>true</c>, only decoders whose <see cref="IImageDecoder.SupportsConcurrentDecode"/>
    /// is <c>true</c> are considered. Use this when calling from multiple threads simultaneously
    /// to avoid COM crashes in non-thread-safe codecs (e.g. WIC HEIC).
    /// </param>
    public IImageDecoder? GetDecoder(string filePath, bool concurrentSafe = false)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        return _byExtension.TryGetValue(ext, out var list)
            ? list.FirstOrDefault(d => d.IsAvailable && (!concurrentSafe || d.SupportsConcurrentDecode))
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
    /// If all extension-based decoders fail, the method reads the file's magic bytes
    /// to detect the actual format and retries with decoders registered for that format.
    /// This handles files whose extension does not match their content (e.g. a HEIC file
    /// that was saved / transferred with a <c>.jpg</c> extension).
    /// </para>
    /// <para>
    /// <see cref="OperationCanceledException"/> is always re-thrown immediately.
    /// </para>
    /// </summary>
    /// <param name="concurrentSafe">
    /// When <c>true</c>, skips decoders where <see cref="IImageDecoder.SupportsConcurrentDecode"/>
    /// is <c>false</c>. Pass <c>true</c> when calling concurrently from multiple threads.
    /// </param>
    public async Task<DecodedImageData?> TryDecodeAsync(
        string            filePath,
        uint              maxWidth      = 0,
        uint              maxHeight     = 0,
        CancellationToken ct            = default,
        bool              concurrentSafe = false)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        if (_byExtension.TryGetValue(ext, out var decoders))
        {
            foreach (var decoder in decoders.Where(d => d.IsAvailable && (!concurrentSafe || d.SupportsConcurrentDecode)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await decoder.DecodeAsync(filePath, maxWidth, maxHeight, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* this decoder failed — try the next one */ }
            }
        }

        // All extension-based decoders failed (or none were registered).
        // Sniff the actual format from magic bytes and retry with decoders for that format.
        var sniffedExt = SniffExtension(filePath);
        if (sniffedExt is not null
            && !string.Equals(sniffedExt, ext, StringComparison.OrdinalIgnoreCase)
            && _byExtension.TryGetValue(sniffedExt, out var sniffedDecoders))
        {
            foreach (var decoder in sniffedDecoders.Where(d => d.IsAvailable && (!concurrentSafe || d.SupportsConcurrentDecode)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await decoder.DecodeAsync(filePath, maxWidth, maxHeight, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* this decoder failed — try the next one */ }
            }
        }

        return null;
    }

    // ── Magic-byte format sniffing ────────────────────────────────────────────

    /// <summary>
    /// Reads the first 16 bytes of <paramref name="filePath"/> and returns the most
    /// likely canonical extension (lower-case, with dot), or <c>null</c> when the
    /// format is unrecognised or the file cannot be read.
    /// </summary>
    private static string? SniffExtension(string filePath)
    {
        try
        {
            Span<byte> buf = stackalloc byte[16];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
            int read = fs.Read(buf);
            if (read < 4) return null;

            // JPEG: FF D8 FF
            if (buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF)
                return ".jpg";

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (read >= 8
                && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47
                && buf[4] == 0x0D && buf[5] == 0x0A && buf[6] == 0x1A && buf[7] == 0x0A)
                return ".png";

            // GIF: 47 49 46 38
            if (buf[0] == 0x47 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x38)
                return ".gif";

            // BMP: 42 4D
            if (buf[0] == 0x42 && buf[1] == 0x4D)
                return ".bmp";

            // RIFF/WebP: 52 49 46 46 ... 57 45 42 50
            if (read >= 12
                && buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46
                && buf[8] == 0x57 && buf[9] == 0x45 && buf[10] == 0x42 && buf[11] == 0x50)
                return ".webp";

            // HEIC/HEIF: ISO Base Media file format (ISOBMFF) — ftyp box at offset 4
            // Layout: [4-byte box size][ftyp][brand (4 bytes)]...
            // Well-known HEIC/HEIF brands: heic, heix, hevc, hevx, heim, heis, mif1, msf1
            if (read >= 12
                && buf[4] == 0x66 && buf[5] == 0x74 && buf[6] == 0x79 && buf[7] == 0x70)
            {
                // Read the brand from bytes 8-11
                var brand = System.Text.Encoding.ASCII.GetString(buf.Slice(8, 4));
                if (brand is "heic" or "heix" or "hevc" or "hevx"
                          or "heim" or "heis" or "hevm" or "hevs"
                          or "mif1" or "msf1")
                    return ".heic";
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
