using FluentGallery.Decoders;
using Xunit;

namespace FluentGallery.Tests;

/// <summary>
/// Tests for the image decoder pipeline and individual decoder implementations.
///
/// Test fixture: <c>TestData/regression_heic_512x512.heic</c>
///   Physical size  : 512 × 512 (square, EXIF orientation = 1 / normal)
///   Format         : HEIC (H.265 / HEVC via libheif)
///   Source         : top-left 512 × 512 crop of regression_exif_orient6_4032x3024.jpg
///                    after EXIF auto-orientation, generated once with Magick.NET.
/// </summary>
public sealed class ImageDecoderTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static readonly string HeicFixture = Path.Combine(
        AppContext.BaseDirectory, "TestData", "regression_heic_512x512.heic");

    private const uint FixtureWidth  = 512u;
    private const uint FixtureHeight = 512u;

    // ── Fixture guard ────────────────────────────────────────────────────────

    [Fact]
    public void HeicFixture_FileExists()
    {
        Assert.True(File.Exists(HeicFixture),
            $"HEIC test fixture not found: {HeicFixture}\n" +
            "Re-generate it by running the Magick.NET crop script in the repo.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // MagickImageDecoder — direct tests
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MagickDecoder_IsAvailable_AlwaysTrue()
    {
        Assert.True(new MagickImageDecoder().IsAvailable,
            "MagickImageDecoder.IsAvailable must always be true: " +
            "Magick.NET bundles its own HEIC codec via libheif.");
    }

    [Fact]
    public void MagickDecoder_SupportedExtensions_ContainsHeicAndHeif()
    {
        var exts = new MagickImageDecoder().SupportedExtensions;
        Assert.Contains(".heic", exts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".heif", exts, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Full-resolution decode returns the correct physical size.</summary>
    [Fact]
    public async Task MagickDecoder_FullResolution_CorrectDimensions()
    {
        var result = await new MagickImageDecoder().DecodeAsync(HeicFixture, 0, 0, CancellationToken.None);

        Assert.Equal(FixtureWidth,  result.Width);
        Assert.Equal(FixtureHeight, result.Height);
    }

    /// <summary>Full-resolution pixel buffer must be Width × Height × 4 bytes (BGRA8).</summary>
    [Fact]
    public async Task MagickDecoder_FullResolution_PixelBufferSizeMatchesDimensions()
    {
        var result   = await new MagickImageDecoder().DecodeAsync(HeicFixture, 0, 0, CancellationToken.None);
        int expected = (int)(result.Width * result.Height * 4);

        Assert.Equal(expected, result.Pixels.Length);
    }

    /// <summary>DPI must be positive (96 if absent in the source).</summary>
    [Fact]
    public async Task MagickDecoder_FullResolution_DpiIsPositive()
    {
        var result = await new MagickImageDecoder().DecodeAsync(HeicFixture, 0, 0, CancellationToken.None);

        Assert.True(result.DpiX > 0, $"DpiX={result.DpiX} must be positive");
        Assert.True(result.DpiY > 0, $"DpiY={result.DpiY} must be positive");
    }

    /// <summary>Scaled output must fit inside the requested bounding box.</summary>
    [Fact]
    public async Task MagickDecoder_Scaled_FitsInsideBox()
    {
        const uint box  = 128u;
        var result      = await new MagickImageDecoder().DecodeAsync(HeicFixture, box, box, CancellationToken.None);

        Assert.True(result.Width  <= box, $"Width  {result.Width}  exceeds {box}");
        Assert.True(result.Height <= box, $"Height {result.Height} exceeds {box}");
    }

    /// <summary>
    /// 512 × 512 source into a 128 × 128 box must produce exactly 128 × 128
    /// because the source is square (aspect ratio 1:1).
    /// </summary>
    [Fact]
    public async Task MagickDecoder_Scaled_SquareSourceProducesExactBoxSize()
    {
        const uint box  = 128u;
        var result      = await new MagickImageDecoder().DecodeAsync(HeicFixture, box, box, CancellationToken.None);

        Assert.Equal(box, result.Width);
        Assert.Equal(box, result.Height);
    }

    /// <summary>Pixel buffer size after scaling is consistent with reported dimensions.</summary>
    [Fact]
    public async Task MagickDecoder_Scaled_PixelBufferMatchesScaledDimensions()
    {
        var result   = await new MagickImageDecoder().DecodeAsync(HeicFixture, 256, 256, CancellationToken.None);
        int expected = (int)(result.Width * result.Height * 4);

        Assert.Equal(expected, result.Pixels.Length);
    }

    // ════════════════════════════════════════════════════════════════════════
    // ImageDecoderPipeline — HEIC with WIC-first / Magick fallback
    // ════════════════════════════════════════════════════════════════════════

    private static ImageDecoderPipeline BuildHeicPipeline()
    {
        var p = new ImageDecoderPipeline();
        p.Register(WicImageDecoder.CreateForHeic());  // priority 1: system WIC (may be unavailable)
        p.Register(new MagickImageDecoder());          // priority 2: built-in Magick.NET fallback
        return p;
    }

    [Fact]
    public void Pipeline_CanDecode_HeicFile_ReturnsTrue()
    {
        Assert.True(BuildHeicPipeline().CanDecode(HeicFixture),
            "At least one decoder (WIC or Magick.NET) must be available for .heic.");
    }

    /// <summary>
    /// Full-resolution decode via the pipeline must succeed and return the correct size.
    /// Passes whether WIC (with HEVC Extensions) or the Magick.NET fallback is used.
    /// </summary>
    [Fact]
    public async Task Pipeline_FullResolution_ReturnsCorrectData()
    {
        var result = await BuildHeicPipeline().TryDecodeAsync(HeicFixture, 0, 0);

        Assert.NotNull(result);
        Assert.Equal(FixtureWidth,  result.Width);
        Assert.Equal(FixtureHeight, result.Height);
        Assert.Equal((int)(FixtureWidth * FixtureHeight * 4), result.Pixels.Length);
    }

    /// <summary>
    /// Thumbnail-size decode (256 × 256) must produce valid, size-consistent pixels.
    /// </summary>
    [Fact]
    public async Task Pipeline_ThumbnailScale_ProducesValidPixels()
    {
        const uint box   = 256u;
        var        result = await BuildHeicPipeline().TryDecodeAsync(HeicFixture, box, box);

        Assert.NotNull(result);
        Assert.True(result.Width  <= box);
        Assert.True(result.Height <= box);
        Assert.Equal((int)(result.Width * result.Height * 4), result.Pixels.Length);
    }

    /// <summary>
    /// Non-existent file: every decoder in the chain will fail and their exceptions
    /// are swallowed by the pipeline, so it returns null rather than propagating.
    /// </summary>
    [Fact]
    public async Task Pipeline_NonExistentFile_ReturnsNull()
    {
        var pipeline = BuildHeicPipeline();
        var result   = await pipeline.TryDecodeAsync(
            Path.Combine(Path.GetTempPath(), "does_not_exist.heic"));
        Assert.Null(result);
    }

    /// <summary>
    /// Extension with no registered decoder must return null immediately.
    /// </summary>
    [Fact]
    public async Task Pipeline_UnsupportedExtension_ReturnsNull()
    {
        var result = await BuildHeicPipeline().TryDecodeAsync("image.xyz");
        Assert.Null(result);
    }

    // ════════════════════════════════════════════════════════════════════════
    // WicImageDecoder HEIC — availability check (machine-dependent)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Smoke-test: <see cref="WicImageDecoder.IsAvailable"/> for HEIC must not
    /// throw, and its value must reflect whether the HEVC Video Extensions are
    /// installed on the current machine (no fixed expectation here).
    /// </summary>
    [Fact]
    public void WicDecoder_ForHeic_IsAvailable_DoesNotThrow()
    {
        var decoder = WicImageDecoder.CreateForHeic();
        var available = decoder.IsAvailable; // should not throw
        // value is machine-dependent; just ensure the property is accessible
        _ = available;
    }

    /// <summary>
    /// If the WIC HEIC codec IS present on this machine, verify it can decode
    /// the fixture at full resolution.
    /// </summary>
    [Fact]
    public async Task WicDecoder_ForHeic_WhenAvailable_DecodesFixtureCorrectly()
    {
        var decoder = WicImageDecoder.CreateForHeic();
        if (!decoder.IsAvailable)
        {
            // HEVC Video Extensions not installed on this machine — skip gracefully.
            return;
        }

        var result = await decoder.DecodeAsync(HeicFixture, 0, 0, CancellationToken.None);

        Assert.Equal(FixtureWidth,  result.Width);
        Assert.Equal(FixtureHeight, result.Height);
        Assert.Equal((int)(FixtureWidth * FixtureHeight * 4), result.Pixels.Length);
    }
}
