using FluentGallery.Data;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace FluentGallery.Tests;

/// <summary>
/// Regression tests for <see cref="ThumbnailService.GenerateAsync"/>.
///
/// Test fixture: <c>TestData/regression_exif_orient6_4032x3024.jpg</c>
///   Physical size  : 4032 × 3024 (landscape)
///   EXIF orientation: 6 = 90° CW  → logical display size is portrait 3024 × 4032
///
/// Historical bugs caught by these tests:
///   1. WIC's RespectExifOrientation + scaling produced garbled / striped output.
///   2. FitInside used physical (pre-EXIF) dimensions, producing a landscape
///      thumbnail for a portrait photo (wrong aspect ratio).
/// </summary>
public sealed class ThumbnailServiceTests : IAsyncLifetime
{
    // ── Test image ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 4032×3024 phone photo with EXIF orientation 6 (90° CW).
    /// After EXIF correction the logical display size is 3024×4032 (portrait).
    /// FitInside(3024, 4032, 512) → expected thumbnail size 384×512.
    /// </summary>
    private static readonly string ExifOrient6Image = Path.Combine(
        AppContext.BaseDirectory, "TestData", "regression_exif_orient6_4032x3024.jpg");

    // ── Per-test temp output management ───────────────────────────────────────

    private string _outDir = string.Empty;

    public Task InitializeAsync()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"ThumbnailTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_outDir))
            Directory.Delete(_outDir, recursive: true);
        return Task.CompletedTask;
    }

    private string TempOutput(string name) => Path.Combine(_outDir, name);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<(uint width, uint height)> ReadJpegDimensionsAsync(string path)
    {
        await using var fs  = File.OpenRead(path);
        var ras             = fs.AsRandomAccessStream();
        var decoder         = await BitmapDecoder.CreateAsync(ras);
        return (decoder.PixelWidth, decoder.PixelHeight);
    }

    /// <summary>
    /// Computes the Alternating-Row-Residual-Correlation (ARRC) score for the JPEG at
    /// <paramref name="path"/>.  A score above 0.5 indicates a periodic stripe artifact
    /// (every other row is visually wrong), as produced by the WIC RespectExifOrientation
    /// bug.  Clean thumbnails score well below 0.3.
    /// </summary>
    private static async Task<double> ComputeStripeScoreAsync(string path)
    {
        await using var fs  = File.OpenRead(path);
        var ras             = fs.AsRandomAccessStream();
        var decoder         = await BitmapDecoder.CreateAsync(ras);

        uint w = decoder.PixelWidth;
        uint h = decoder.PixelHeight;

        var pd = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var px = pd.DetachPixelData();

        // Per-row mean luminance (BT.709)
        double[] L = new double[h];
        for (uint row = 0; row < h; row++)
        {
            double sum = 0;
            for (uint col = 0; col < w; col++)
            {
                uint i = (row * w + col) * 4;
                sum   += 0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i];
            }
            L[row] = sum / w;
        }

        // ARRC: Pearson correlation of residual with alternating ±1 signal
        int n = (int)h - 2;
        if (n < 4) return 0;

        double[] residual = new double[n];
        double[] alt      = new double[n];
        for (int i = 0; i < n; i++)
        {
            residual[i] = Math.Abs(L[i + 1] - (L[i] + L[i + 2]) / 2.0);
            alt[i]      = (i % 2 == 0) ? 1.0 : -1.0;
        }

        double mx = residual.Average(), my = alt.Average();
        double num = 0, dx2 = 0, dy2 = 0;
        for (int i = 0; i < n; i++)
        {
            double dxi = residual[i] - mx, dyi = alt[i] - my;
            num += dxi * dyi;
            dx2 += dxi * dxi;
            dy2 += dyi * dyi;
        }
        double denom = Math.Sqrt(dx2 * dy2);
        return denom < 1e-12 ? 0.0 : Math.Abs(num / denom);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Tests
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TestImage_Exists()
    {
        Assert.True(File.Exists(ExifOrient6Image),
            $"Test fixture not found: {ExifOrient6Image}\n" +
            "Run the project once or check that TestData/ was included in the build output.");
    }

    /// <summary>
    /// Regression guard for bug #1:
    /// WIC's RespectExifOrientation combined with scaling produced garbled (striped) pixel output.
    /// The ARRC stripe score of a correct thumbnail must stay below 0.3.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ExifOrientation6_ContentIsNotGarbled()
    {
        var dest = TempOutput("no_garble.jpg");
        await ThumbnailService.GenerateAsync(ExifOrient6Image, dest, 512, CancellationToken.None);

        double score = await ComputeStripeScoreAsync(dest);
        Assert.True(score < 0.3,
            $"Stripe score {score:F4} ≥ 0.3 — thumbnail content appears garbled. " +
            "Check that GenerateAsync uses IgnoreExifOrientation + manual BitmapTransform.Rotation.");
    }

    /// <summary>
    /// Regression guard for bug #2:
    /// FitInside used physical (pre-EXIF) dimensions (4032×3024) instead of logical
    /// (3024×4032), producing a landscape 512×384 thumbnail for a portrait photo.
    /// The correct output for this image is portrait 384×512.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ExifOrientation6_ProducesPortraitThumbnail()
    {
        var dest = TempOutput("portrait_dims.jpg");
        await ThumbnailService.GenerateAsync(ExifOrient6Image, dest, 512, CancellationToken.None);

        var (w, h) = await ReadJpegDimensionsAsync(dest);

        Assert.Equal(384u, w);
        Assert.Equal(512u, h);
    }

    /// <summary>
    /// Both output dimensions must fit within the 512-pixel box.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ExifOrientation6_FitsWithinBox()
    {
        var dest = TempOutput("fit_box.jpg");
        await ThumbnailService.GenerateAsync(ExifOrient6Image, dest, 512, CancellationToken.None);

        var (w, h) = await ReadJpegDimensionsAsync(dest);

        Assert.True(w <= 512, $"Width {w} exceeds the 512-pixel box");
        Assert.True(h <= 512, $"Height {h} exceeds the 512-pixel box");
    }

    /// <summary>
    /// The thumbnail's aspect ratio must match the logical (post-EXIF) source aspect ratio
    /// within a 5 % tolerance.  For this image: logical 3024×4032 → aspect ≈ 0.750.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ExifOrientation6_AspectRatioMatchesLogicalSource()
    {
        const double logicalAspect = 3024.0 / 4032.0; // ≈ 0.750

        var dest = TempOutput("aspect_ratio.jpg");
        await ThumbnailService.GenerateAsync(ExifOrient6Image, dest, 512, CancellationToken.None);

        var (w, h) = await ReadJpegDimensionsAsync(dest);
        double thumbAspect = (double)w / h;

        Assert.True(Math.Abs(thumbAspect - logicalAspect) < 0.05,
            $"Aspect ratio mismatch: expected ≈{logicalAspect:F3}, got {thumbAspect:F3} ({w}×{h}). " +
            "FitInside may be using physical instead of logical (EXIF-corrected) dimensions.");
    }
}
