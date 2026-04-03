using FluentGallery.Decoders;
using FluentGallery.Helpers;
using FluentGallery.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentGallery.Data;

/// <summary>Progress snapshot emitted during <see cref="ThumbnailService.GenerateMissingAsync"/>.</summary>
/// <param name="Done">Number of thumbnails successfully processed so far.</param>
/// <param name="Total">Total number of photos in this batch.</param>
/// <param name="SpeedPerSec">Throughput in thumbnails per second (smoothed over elapsed time).</param>
/// <param name="Eta">Estimated time to completion, or <c>null</c> if speed is not yet known.</param>
public record ThumbnailBatchProgress(int Done, int Total, double SpeedPerSec, TimeSpan? Eta);

/// <summary>
/// Generates and caches JPEG thumbnails using WIC (Windows.Graphics.Imaging).
/// The fit-inside box size is configurable via <see cref="ThumbSize"/> (default 512 px).
/// Thumbnail files are stored under <see cref="AppDataPaths.ThumbnailsDirectory"/>;
/// their paths and source modification times are persisted in the Thumbnails table.
///
/// Concurrency is bounded by <see cref="MaxConcurrent"/> to avoid saturating disk I/O
/// during large batch scans.
/// </summary>
public sealed class ThumbnailService
{
    private const float JpegQuality = 0.80f;

    /// <summary>
    /// Fit-inside box size in pixels used when generating new thumbnails.
    /// Changing this at runtime does not invalidate the existing file cache;
    /// call <c>ClearThumbnailCacheAsync</c> in <see cref="FluentGallery.Data.DatabaseService"/>
    /// after updating this value so thumbnails are regenerated at the new size.
    /// </summary>
    public uint ThumbSize { get; set; } = 512;

    /// <summary>
    /// Maximum number of thumbnails generated simultaneously.
    /// Kept low (2) so bulk scans don't saturate disk I/O and cause UI jank.
    /// </summary>
    private const int MaxConcurrent = 2;

    private readonly SemaphoreSlim              _semaphore = new(MaxConcurrent, MaxConcurrent);
    private readonly DatabaseService            _db;
    private readonly ILogger<ThumbnailService>  _logger;
    private readonly ImageDecoderPipeline       _pipeline;

    public ThumbnailService(
        DatabaseService           db,
        ILogger<ThumbnailService> logger,
        ImageDecoderPipeline      pipeline)
    {
        _db       = db;
        _logger   = logger;
        _pipeline = pipeline;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the thumbnail file path for <paramref name="photo"/>, generating it on demand.
    /// Returns <c>null</c> when generation fails (e.g. unsupported format, locked file).
    /// </summary>
    public async Task<string?> GetOrCreateThumbnailAsync(Photo photo, CancellationToken ct = default)
    {
        // Skip formats with no registered decoder (avoids attempting WIC on unsupported types)
        if (!_pipeline.CanDecode(photo.FilePath))
            return null;

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

            await GenerateViaDecoderAsync(photo.FilePath, thumbPath, ThumbSize, ct);

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

    /// <summary>
    /// Generates thumbnails for all <paramref name="photos"/> that don't already have a
    /// valid cached thumbnail, reporting progress after each photo completes.
    /// Parallelism is bounded by <see cref="MaxConcurrent"/> — the same limit that
    /// governs on-demand generation, so the two workloads don't compete.
    /// </summary>
    public async Task GenerateMissingAsync(
        IReadOnlyList<Photo>              photos,
        IProgress<ThumbnailBatchProgress> progress,
        CancellationToken                 ct)
    {
        int total    = photos.Count;
        int doneCount = 0;
        var sw       = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            photos,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrent, CancellationToken = ct },
            async (photo, innerCt) =>
            {
                await GetOrCreateThumbnailAsync(photo, innerCt);

                int    done      = Interlocked.Increment(ref doneCount);
                double elapsed   = sw.Elapsed.TotalSeconds;
                double speed     = elapsed > 0.1 ? done / elapsed : 0;
                int    remaining = total - done;
                TimeSpan? eta    = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : null;

                progress.Report(new ThumbnailBatchProgress(done, total, speed, eta));
            });
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

    // ── Decoder-pipeline thumbnail generation ────────────────────────────────

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> via <see cref="ImageDecoderPipeline"/>
    /// (WIC for standard formats; built-in Magick.NET fallback for HEIC/HEIF),
    /// then encodes the scaled pixels as JPEG to <paramref name="destPath"/>.
    /// </summary>
    private async Task GenerateViaDecoderAsync(
        string sourcePath, string destPath, uint thumbSize, CancellationToken ct)
    {
        var decoded = await _pipeline.TryDecodeAsync(sourcePath, thumbSize, thumbSize, ct)
            ?? throw new NotSupportedException(
                $"No decoder available for '{Path.GetExtension(sourcePath)}'");

        await EncodeToJpegAsync(decoded, destPath, ct);
    }

    // ── WIC thumbnail generation (kept as internal static for unit tests) ────

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> with WIC, scales it to fit inside
    /// <paramref name="thumbSize"/>×<paramref name="thumbSize"/> (preserving aspect ratio),
    /// applies EXIF orientation, and writes a JPEG to <paramref name="destPath"/>.
    ///
    /// EXIF rotation is handled manually via <see cref="BitmapTransform.Rotation"/>
    /// and <see cref="BitmapTransform.Flip"/> rather than
    /// <see cref="ExifOrientationMode.RespectExifOrientation"/>, because WIC's
    /// built-in EXIF handling produces garbled output on certain images when
    /// combined with scaling.
    /// </summary>
    internal static async Task GenerateAsync(string sourcePath, string destPath, uint thumbSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // ── Decode ───────────────────────────────────────────────────────────
        await using var srcStream = File.OpenRead(sourcePath);
        var srcRas  = srcStream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(srcRas).AsTask(ct);

        uint physW = decoder.PixelWidth;
        uint physH = decoder.PixelHeight;

        ushort exifOrient = await ReadExifOrientationAsync(decoder, ct);
        bool   rotSwaps   = exifOrient is 5 or 6 or 7 or 8;

        // FitInside targets the final (post-rotation) logical dimensions.
        (uint logW, uint logH) = rotSwaps ? (physH, physW) : (physW, physH);
        (uint finalW, uint finalH) = FitInside(logW, logH, thumbSize);

        // BitmapTransform scales BEFORE rotating, so pre-rotation scale dims
        // must be swapped back when a 90°/270° rotation is involved.
        (uint scaleW, uint scaleH) = rotSwaps ? (finalH, finalW) : (finalW, finalH);

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
            ColorManagementMode.ColorManageToSRgb).AsTask(ct);

        var pixels = pixelData.DetachPixelData();

        ct.ThrowIfCancellationRequested();

        var decoded = new Decoders.DecodedImageData(pixels, finalW, finalH, decoder.DpiX, decoder.DpiY);
        await EncodeToJpegAsync(decoded, destPath, ct);
    }

    // ── JPEG encoding helper ─────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="decoded"/> pixels (BGRA8) as a JPEG file at
    /// <paramref name="destPath"/> using an in-memory WIC encoder.
    /// </summary>
    private static async Task EncodeToJpegAsync(
        Decoders.DecodedImageData decoded, string destPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var memRas  = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, memRas).AsTask(ct);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            decoded.Width, decoded.Height,
            decoded.DpiX, decoded.DpiY,
            decoded.Pixels);

        await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single) },
        }).AsTask(ct);

        await encoder.FlushAsync().AsTask(ct);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        memRas.Seek(0);

        await using var dstStream = File.Create(destPath);
        await memRas.AsStreamForRead().CopyToAsync(dstStream, ct);
    }

    // ── Geometry helpers ─────────────────────────────────────────────────────

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

    // ── EXIF helpers ─────────────────────────────────────────────────────────

    private static async Task<ushort> ReadExifOrientationAsync(
        BitmapDecoder decoder, CancellationToken ct)
    {
        try
        {
            var props = await decoder.BitmapProperties
                .GetPropertiesAsync(new[] { "System.Photo.Orientation" }).AsTask(ct);

            if (props.TryGetValue("System.Photo.Orientation", out var v) && v.Value is ushort orient)
                return orient;
        }
        catch { /* no EXIF tag or unreadable — treat as normal orientation */ }

        return 1; // Normal / absent
    }

    private static BitmapRotation ExifToRotation(ushort orient) => orient switch
    {
        3 or 4 => BitmapRotation.Clockwise180Degrees,
        5 or 6 => BitmapRotation.Clockwise90Degrees,
        7 or 8 => BitmapRotation.Clockwise270Degrees,
        _      => BitmapRotation.None,
    };

    private static BitmapFlip ExifToFlip(ushort orient) => orient switch
    {
        2 or 7 => BitmapFlip.Horizontal,
        4 or 5 => BitmapFlip.Vertical,
        _      => BitmapFlip.None,
    };
}
