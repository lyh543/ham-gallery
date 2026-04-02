using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using Microsoft.Extensions.Logging;

namespace FluentGallery.Data;

/// <summary>
/// Reads image metadata (EXIF, GPS, dimensions) from photo files using MetadataExtractor.
/// All operations are synchronous and CPU-bound; callers should wrap in Task.Run if needed.
/// </summary>
public sealed class ExifService
{
    private readonly ILogger<ExifService> _logger;

    public ExifService(ILogger<ExifService> logger) => _logger = logger;

    /// <summary>
    /// Extracts all available metadata from <paramref name="filePath"/>.
    /// Never throws — errors are logged and a partial result is returned.
    /// </summary>
    public ExifData ReadExif(string filePath)
    {
        var result = new ExifData();
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);

            ExtractDimensions(directories, result);
            ExtractExifSubIfd(directories, result);
            ExtractExifIfd0(directories, result);
            ExtractGps(directories, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EXIF read failed for {Path}", filePath);
        }
        return result;
    }

    // ── Dimensions ──────────────────────────────────────────────────────────

    private static void ExtractDimensions(IReadOnlyList<MetadataExtractor.Directory> dirs, ExifData result)
    {
        // JPEG
        var jpeg = dirs.OfType<JpegDirectory>().FirstOrDefault();
        if (jpeg is not null)
        {
            if (jpeg.ContainsTag(JpegDirectory.TagImageWidth))
                result.Width = jpeg.GetInt32(JpegDirectory.TagImageWidth);
            if (jpeg.ContainsTag(JpegDirectory.TagImageHeight))
                result.Height = jpeg.GetInt32(JpegDirectory.TagImageHeight);
            return;
        }

        // PNG
        var png = dirs.OfType<PngDirectory>().FirstOrDefault(d =>
            d.ContainsTag(PngDirectory.TagImageWidth) && d.ContainsTag(PngDirectory.TagImageHeight));
        if (png is not null)
        {
            result.Width  = png.GetInt32(PngDirectory.TagImageWidth);
            result.Height = png.GetInt32(PngDirectory.TagImageHeight);
        }
    }

    // ── EXIF SubIFD (date/time, lens, pixel dimensions) ─────────────────────

    private static void ExtractExifSubIfd(IReadOnlyList<MetadataExtractor.Directory> dirs, ExifData result)
    {
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (sub is null) return;

        // Preferred date: DateTimeOriginal
        if (sub.TryGetDateTime(ExifSubIfdDirectory.TagDateTimeOriginal, out var dt))
            result.TakenAt = dt.ToUniversalTime().ToString("O");

        // Pixel dimensions (may supplement JPEG header values)
        if (!result.Width.HasValue && sub.ContainsTag(ExifSubIfdDirectory.TagExifImageWidth))
            result.Width = sub.GetInt32(ExifSubIfdDirectory.TagExifImageWidth);
        if (!result.Height.HasValue && sub.ContainsTag(ExifSubIfdDirectory.TagExifImageHeight))
            result.Height = sub.GetInt32(ExifSubIfdDirectory.TagExifImageHeight);
    }

    // ── EXIF IFD0 (camera, orientation, fallback date) ──────────────────────

    private static void ExtractExifIfd0(IReadOnlyList<MetadataExtractor.Directory> dirs, ExifData result)
    {
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 is null) return;

        result.CameraMake  = TrimOrNull(ifd0.GetDescription(ExifIfd0Directory.TagMake));
        result.CameraModel = TrimOrNull(ifd0.GetDescription(ExifIfd0Directory.TagModel));

        if (ifd0.ContainsTag(ExifIfd0Directory.TagOrientation))
            result.Orientation = ifd0.GetInt32(ExifIfd0Directory.TagOrientation);

        // Fallback date when SubIFD did not provide one
        if (result.TakenAt is null && ifd0.TryGetDateTime(ExifIfd0Directory.TagDateTime, out var dt))
            result.TakenAt = dt.ToUniversalTime().ToString("O");
    }

    // ── GPS ─────────────────────────────────────────────────────────────────

    private static void ExtractGps(IReadOnlyList<MetadataExtractor.Directory> dirs, ExifData result)
    {
        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
        if (gps is null) return;

        var loc = gps.GetGeoLocation();
        if (loc is not null && !loc.IsZero)
        {
            result.Latitude  = loc.Latitude;
            result.Longitude = loc.Longitude;
        }
    }

    // ────────────────────────────────────────────────────────────────────────

    private static string? TrimOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Value object that holds metadata extracted from a single image file.
/// </summary>
public sealed class ExifData
{
    public int?    Width       { get; set; }
    public int?    Height      { get; set; }

    /// <summary>ISO 8601 UTC string (nullable — many files have no EXIF date).</summary>
    public string? TakenAt     { get; set; }

    public string? CameraMake  { get; set; }
    public string? CameraModel { get; set; }

    /// <summary>EXIF Orientation tag value (1–8), or null if absent.</summary>
    public int?    Orientation { get; set; }

    public double? Latitude    { get; set; }
    public double? Longitude   { get; set; }
}
