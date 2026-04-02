using FluentGallery.Helpers;
using FluentGallery.Models;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentGallery.Data;

/// <summary>
/// Generates and caches 256×256 JPEG thumbnails using WIC (Windows.Graphics.Imaging).
/// Thumbnail files are stored under <see cref="AppDataPaths.ThumbnailsDirectory"/>;
/// their paths and source modification times are persisted in the Thumbnails table.
///
/// Concurrency is bounded by <see cref="MaxConcurrent"/> to avoid saturating disk I/O
/// during large batch scans.
/// </summary>
public sealed class ThumbnailService
{
    private const uint   ThumbSize    = 256;
    private const float  JpegQuality  = 0.80f;

    /// <summary>
    /// Maximum number of thumbnails generated simultaneously.
    /// Kept low (2) so bulk scans don't saturate disk I/O and cause UI jank.
    /// </summary>
    private const int MaxConcurrent = 2;

    private readonly SemaphoreSlim              _semaphore = new(MaxConcurrent, MaxConcurrent);
    private readonly DatabaseService            _db;
    private readonly ILogger<ThumbnailService>  _logger;

    public ThumbnailService(DatabaseService db, ILogger<ThumbnailService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the thumbnail file path for <paramref name="photo"/>, generating it on demand.
    /// Returns <c>null</c> when generation fails (e.g. unsupported format, locked file).
    /// </summary>
    public async Task<string?> GetOrCreateThumbnailAsync(Photo photo, CancellationToken ct = default)
    {
        var thumbPath        = GetThumbPath(photo.FilePath);
        var sourceModifiedAt = photo.ModifiedAt;

        // Fast-path: valid cached thumbnail
        var cached = await _db.GetThumbnailAsync(photo.Id, ct);
        if (cached is not null
            && cached.SourceModifiedAt == sourceModifiedAt
            && File.Exists(cached.ThumbPath))
        {
            return cached.ThumbPath;
        }

        // Generate — bounded by semaphore to avoid disk I/O saturation
        await _semaphore.WaitAsync(ct);
        try
        {
            // Re-check cache in case another worker already generated it while we waited
            cached = await _db.GetThumbnailAsync(photo.Id, ct);
            if (cached is not null
                && cached.SourceModifiedAt == sourceModifiedAt
                && File.Exists(cached.ThumbPath))
            {
                return cached.ThumbPath;
            }

            await GenerateAsync(photo.FilePath, thumbPath, ct);

            await _db.UpsertThumbnailAsync(new Thumbnail
            {
                PhotoId          = photo.Id,
                ThumbPath        = thumbPath,
                SourceModifiedAt = sourceModifiedAt,
            }, ct);

            return thumbPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail generation failed for {Path}", photo.FilePath);
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Path helper ──────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a stable thumbnail filename from the source path using MD5.
    /// This avoids illegal characters and path-length issues.
    /// </summary>
    private static string GetThumbPath(string filePath)
    {
        var hash = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(filePath))
        ).ToLowerInvariant();

        return Path.Combine(AppDataPaths.ThumbnailsDirectory, $"{hash}.jpg");
    }

    // ── WIC thumbnail generation ─────────────────────────────────────────────

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> with WIC, scales it to fit inside
    /// <see cref="ThumbSize"/>×<see cref="ThumbSize"/> (preserving aspect ratio),
    /// respects EXIF orientation, and writes a JPEG to <paramref name="destPath"/>.
    /// Runs fully on the calling (background) thread.
    /// </summary>
    private static async Task GenerateAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // ── Decode ───────────────────────────────────────────────────────────
        await using var srcStream = File.OpenRead(sourcePath);
        var srcRas  = srcStream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(srcRas).AsTask(ct);

        // Compute fit-inside dimensions
        (uint dstW, uint dstH) = FitInside(decoder.PixelWidth, decoder.PixelHeight, ThumbSize);

        var transform = new BitmapTransform
        {
            ScaledWidth        = dstW,
            ScaledHeight       = dstH,
            InterpolationMode  = BitmapInterpolationMode.Fant,
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(ct);

        var pixels = pixelData.DetachPixelData();

        ct.ThrowIfCancellationRequested();

        // ── Encode to in-memory stream ────────────────────────────────────────
        using var memRas  = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, memRas).AsTask(ct);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            dstW, dstH,
            decoder.DpiX, decoder.DpiY,
            pixels);

        await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single) },
        }).AsTask(ct);

        await encoder.FlushAsync().AsTask(ct);

        // ── Write to disk ─────────────────────────────────────────────────────
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        memRas.Seek(0);

        await using var dstStream = File.Create(destPath);
        await memRas.AsStreamForRead().CopyToAsync(dstStream, ct);
    }

    // ── Geometry helper ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the largest (w, h) pair that fits inside a <paramref name="box"/>×<paramref name="box"/>
    /// square while preserving the original aspect ratio.
    /// </summary>
    private static (uint w, uint h) FitInside(uint srcW, uint srcH, uint box)
    {
        if (srcW == 0 || srcH == 0) return (box, box);

        if (srcW >= srcH)
            return (box, (uint)Math.Max(1, Math.Round(box * (double)srcH / srcW)));
        else
            return ((uint)Math.Max(1, Math.Round(box * (double)srcW / srcH)), box);
    }
}
