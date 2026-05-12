using FluentGallery.Helpers;
using ImageMagick;
using Xunit;

namespace FluentGallery.Tests;

/// <summary>
/// End-to-end tests for EXIF orientation write-back (JPG and HEIC).
///
/// Each test copies a fixture to an isolated temp directory — the originals in
/// TestData/ are never modified.  The temp directory is deleted in Dispose().
///
/// Orientation is read back via Magick.NET, which supports both JPEG and HEIC.
/// </summary>
public sealed class ExifOrientationWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "HamGalleryRotationTest_" + Guid.NewGuid().ToString("N"));

    public ExifOrientationWriterTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    // regression_exif_orient6_4032x3024.jpg  — EXIF orientation = 6 (90° CW)
    private static string JpegFixture => Path.Combine(
        AppContext.BaseDirectory, "TestData", "regression_exif_orient6_4032x3024.jpg");

    // regression_heic_512x512.heic  — EXIF orientation = 1 (normal)
    private static string HeicFixture => Path.Combine(
        AppContext.BaseDirectory, "TestData", "regression_heic_512x512.heic");

    private string CopyToTemp(string source)
    {
        var dest = Path.Combine(_tempDir, Path.GetFileName(source));
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    /// <summary>Reads the EXIF orientation tag using Magick.NET (works for both JPEG and HEIC).</summary>
    private static int ReadOrientation(string filePath)
    {
        using var image = new MagickImage(filePath);
        return (int?)image.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value ?? 1;
    }

    // ── Format detection ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.JPEG")]
    [InlineData("photo.heic")]
    [InlineData("photo.HEIF")]
    public void IsRotatableFormat_SupportedFormats_True(string filename)
        => Assert.True(ExifOrientationWriter.IsRotatableFormat(filename));

    [Theory]
    [InlineData("video.mp4")]
    [InlineData("video.mov")]
    [InlineData("photo.png")]
    [InlineData("photo.gif")]
    public void IsRotatableFormat_UnsupportedFormats_False(string filename)
        => Assert.False(ExifOrientationWriter.IsRotatableFormat(filename));

    // ── Rotation table ────────────────────────────────────────────────────────

    [Fact]
    public void RotateCw_Orientation1_Returns6()
        // Normal → 90°CW = orientation 6
        => Assert.Equal(6, ExifOrientationWriter.RotateCw(1));

    [Fact]
    public void RotateCcw_Orientation6_Returns1()
        // 90°CW rotated → CCW = back to Normal
        => Assert.Equal(1, ExifOrientationWriter.RotateCcw(6));

    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    [InlineData(5)][InlineData(6)][InlineData(7)][InlineData(8)]
    public void RotateCw_FourTimes_ReturnToOriginal(int start)
    {
        int o = start;
        for (int i = 0; i < 4; i++) o = ExifOrientationWriter.RotateCw(o);
        Assert.Equal(start, o);
    }

    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    [InlineData(5)][InlineData(6)][InlineData(7)][InlineData(8)]
    public void RotateCcw_FourTimes_ReturnToOriginal(int start)
    {
        int o = start;
        for (int i = 0; i < 4; i++) o = ExifOrientationWriter.RotateCcw(o);
        Assert.Equal(start, o);
    }

    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    [InlineData(5)][InlineData(6)][InlineData(7)][InlineData(8)]
    public void RotateCw_ThenCcw_ReturnToOriginal(int start)
        => Assert.Equal(start,
            ExifOrientationWriter.RotateCcw(ExifOrientationWriter.RotateCw(start)));

    // ── JPEG write-back ───────────────────────────────────────────────────────

    [Fact]
    public async Task Jpeg_RotateCw_OrientationUpdatedOnDisk()
    {
        var path     = CopyToTemp(JpegFixture);
        int initial  = ReadOrientation(path);
        int expected = ExifOrientationWriter.RotateCw(initial);

        await ExifOrientationWriter.WriteAsync(path, expected);

        Assert.Equal(expected, ReadOrientation(path));
    }

    [Fact]
    public async Task Jpeg_RotateCcw_OrientationUpdatedOnDisk()
    {
        var path     = CopyToTemp(JpegFixture);
        int initial  = ReadOrientation(path);
        int expected = ExifOrientationWriter.RotateCcw(initial);

        await ExifOrientationWriter.WriteAsync(path, expected);

        Assert.Equal(expected, ReadOrientation(path));
    }

    [Fact]
    public async Task Jpeg_FourCwRotations_OrientationRestoredToOriginal()
    {
        var path        = CopyToTemp(JpegFixture);
        int initial     = ReadOrientation(path);
        int orientation = initial;

        for (int i = 0; i < 4; i++)
        {
            orientation = ExifOrientationWriter.RotateCw(orientation);
            await ExifOrientationWriter.WriteAsync(path, orientation);
        }

        Assert.Equal(initial, ReadOrientation(path));
    }

    [Fact]
    public async Task Jpeg_AfterRotate_FileRemainsValidImage()
    {
        var path = CopyToTemp(JpegFixture);
        await ExifOrientationWriter.WriteAsync(
            path, ExifOrientationWriter.RotateCw(ReadOrientation(path)));

        using var image = new MagickImage(path);
        Assert.True(image.Width > 0 && image.Height > 0);
    }

    // ── HEIC write-back ───────────────────────────────────────────────────────

    [Fact]
    public async Task Heic_RotateCw_OrientationUpdatedOnDisk()
    {
        var path     = CopyToTemp(HeicFixture);
        int initial  = ReadOrientation(path);
        int expected = ExifOrientationWriter.RotateCw(initial);

        await ExifOrientationWriter.WriteAsync(path, expected);

        Assert.Equal(expected, ReadOrientation(path));
    }

    [Fact]
    public async Task Heic_RotateCcw_OrientationUpdatedOnDisk()
    {
        var path     = CopyToTemp(HeicFixture);
        int initial  = ReadOrientation(path);
        int expected = ExifOrientationWriter.RotateCcw(initial);

        await ExifOrientationWriter.WriteAsync(path, expected);

        Assert.Equal(expected, ReadOrientation(path));
    }

    [Fact]
    public async Task Heic_FourCwRotations_OrientationRestoredToOriginal()
    {
        var path        = CopyToTemp(HeicFixture);
        int initial     = ReadOrientation(path);
        int orientation = initial;

        for (int i = 0; i < 4; i++)
        {
            orientation = ExifOrientationWriter.RotateCw(orientation);
            await ExifOrientationWriter.WriteAsync(path, orientation);
        }

        Assert.Equal(initial, ReadOrientation(path));
    }

    [Fact]
    public async Task Heic_AfterRotate_FileRemainsValidImage()
    {
        var path = CopyToTemp(HeicFixture);
        await ExifOrientationWriter.WriteAsync(
            path, ExifOrientationWriter.RotateCw(ReadOrientation(path)));

        using var image = new MagickImage(path);
        Assert.True(image.Width > 0 && image.Height > 0);
    }
}
