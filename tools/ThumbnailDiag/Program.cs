/// <summary>
/// ThumbnailDiag — diagnoses and compares three thumbnail generation strategies.
///
/// Strategy A (BUG):  RespectExif + physical dims for FitInside  (original broken code)
/// Strategy B (FIX1): RespectExif + logical dims for FitInside   (first fix attempt — still broken)
/// Strategy C (FIX2): IgnoreExif  + manual BitmapTransform.Rotation  (new fix)
/// </summary>

using System.Security.Cryptography;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

const uint  ThumbSize    = 256;
const float JpegQuality  = 0.80f;

string inputPath = args.Length > 0
    ? args[0]
    : @"C:\Users\lyh54\git\github\ham-gallery\data\手机照片\Camera_archived\2018-2022.11.4\1f930110a7c4bb45.jpg";

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"[ERROR] File not found: {inputPath}");
    return 1;
}

var outputDir = Path.Combine(Path.GetTempPath(), "ThumbDiag");
Directory.CreateDirectory(outputDir);

Console.WriteLine($"Source  : {inputPath}");
Console.WriteLine($"Output  : {outputDir}");
Console.WriteLine();

// ── Source info ──────────────────────────────────────────────────────────────
await using var srcFs   = File.OpenRead(inputPath);
var srcRas              = srcFs.AsRandomAccessStream();
var srcDecoder          = await BitmapDecoder.CreateAsync(srcRas);
ushort exifOrient       = await ReadExifOrientAsync(srcDecoder);
uint physW = srcDecoder.PixelWidth, physH = srcDecoder.PixelHeight;
bool swaps = exifOrient is 5 or 6 or 7 or 8;
(uint logW, uint logH) = swaps ? (physH, physW) : (physW, physH);

Console.WriteLine($"Physical  : {physW} × {physH}");
Console.WriteLine($"EXIF      : {exifOrient} ({DescribeOrientation(exifOrient)})");
Console.WriteLine($"Logical   : {logW} × {logH}" + (swaps ? "  (swapped)" : ""));
Console.WriteLine();

await srcFs.DisposeAsync();

// ── Strategy A: BUG — RespectExif + physical dims ────────────────────────────
{
    string path = Path.Combine(outputDir, "A_BUG_RespectExif_PhysicalDims.jpg");
    (uint dstW, uint dstH) = FitInside(physW, physH, ThumbSize);
    Console.WriteLine($"── A) BUG: RespectExif + FitInside({physW},{physH}) → {dstW}×{dstH}");
    try
    {
        var (aw, ah, bufLen) = await GenerateRespectExifAsync(inputPath, path, dstW, dstH);
        PrintResult(path, aw, ah, bufLen, logW, logH);
    }
    catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }
    Console.WriteLine();
}

// ── Strategy B: FIX1 — RespectExif + logical dims ───────────────────────────
{
    string path = Path.Combine(outputDir, "B_FIX1_RespectExif_LogicalDims.jpg");
    (uint dstW, uint dstH) = FitInside(logW, logH, ThumbSize);
    Console.WriteLine($"── B) FIX1: RespectExif + FitInside({logW},{logH}) → {dstW}×{dstH}");
    try
    {
        var (aw, ah, bufLen) = await GenerateRespectExifAsync(inputPath, path, dstW, dstH);
        PrintResult(path, aw, ah, bufLen, logW, logH);
    }
    catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }
    Console.WriteLine();
}

// ── Strategy C: FIX2 — IgnoreExif + manual rotation ─────────────────────────
{
    string path = Path.Combine(outputDir, "C_FIX2_IgnoreExif_ManualRotation.jpg");
    // Scale in physical space, then rotate
    (uint scaleW, uint scaleH, uint finalW, uint finalH) = ComputeManualRotationDims(
        physW, physH, exifOrient, ThumbSize);
    var rotation = ExifToRotation(exifOrient);
    var flip     = ExifToFlip(exifOrient);
    Console.WriteLine($"── C) FIX2: IgnoreExif + Rotation={rotation}, Flip={flip}");
    Console.WriteLine($"     Scale to {scaleW}×{scaleH} → rotate → final {finalW}×{finalH}");
    try
    {
        var (aw, ah, bufLen) = await GenerateManualRotationAsync(
            inputPath, path, scaleW, scaleH, finalW, finalH, rotation, flip);
        PrintResult(path, aw, ah, bufLen, logW, logH);
    }
    catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }
    Console.WriteLine();
}

// ── Strategy D: IgnoreExif, no rotation (baseline) ──────────────────────────
{
    string path = Path.Combine(outputDir, "D_Baseline_IgnoreExif_NoRotation.jpg");
    (uint dstW, uint dstH) = FitInside(physW, physH, ThumbSize);
    Console.WriteLine($"── D) Baseline: IgnoreExif, no rotation, {dstW}×{dstH}");
    try
    {
        var (aw, ah, bufLen) = await GenerateIgnoreExifAsync(inputPath, path, dstW, dstH);
        PrintResult(path, aw, ah, bufLen, physW, physH); // compare against physical since no rotation
    }
    catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }
    Console.WriteLine();
}

Console.WriteLine("══════════════════════════════════════════════════════════════════════");
Console.WriteLine("Please visually compare the output files.");
Console.WriteLine("  A = original bug       (likely distorted for rotated images)");
Console.WriteLine("  B = fix attempt 1      (may still be distorted)");
Console.WriteLine("  C = fix attempt 2      (should be correct — manual rotation)");
Console.WriteLine("  D = baseline           (correct content, wrong orientation)");
Console.WriteLine();
System.Diagnostics.Process.Start("explorer.exe", outputDir);
return 0;


// ════════════════════════════════════════════════════════════════════════════
// Generation strategies
// ════════════════════════════════════════════════════════════════════════════

static async Task<(uint outW, uint outH, int bufLen)> GenerateRespectExifAsync(
    string src, string dst, uint dstW, uint dstH)
{
    await using var fs = File.OpenRead(src);
    var ras     = fs.AsRandomAccessStream();
    var decoder = await BitmapDecoder.CreateAsync(ras);

    var transform = new BitmapTransform
    {
        ScaledWidth       = dstW,
        ScaledHeight      = dstH,
        InterpolationMode = BitmapInterpolationMode.Fant,
    };

    var pd = await decoder.GetPixelDataAsync(
        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
        transform,
        ExifOrientationMode.RespectExifOrientation,
        ColorManagementMode.ColorManageToSRgb);

    var pixels = pd.DetachPixelData();

    using var mem = new InMemoryRandomAccessStream();
    var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, mem);
    enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
        dstW, dstH, decoder.DpiX, decoder.DpiY, pixels);
    await enc.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        { { "ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single) } });
    await enc.FlushAsync();

    mem.Seek(0);
    await using var outFs = File.Create(dst);
    await mem.AsStreamForRead().CopyToAsync(outFs);

    return (dstW, dstH, pixels.Length);
}

static async Task<(uint outW, uint outH, int bufLen)> GenerateIgnoreExifAsync(
    string src, string dst, uint dstW, uint dstH)
{
    await using var fs = File.OpenRead(src);
    var ras     = fs.AsRandomAccessStream();
    var decoder = await BitmapDecoder.CreateAsync(ras);

    var transform = new BitmapTransform
    {
        ScaledWidth       = dstW,
        ScaledHeight      = dstH,
        InterpolationMode = BitmapInterpolationMode.Fant,
    };

    var pd = await decoder.GetPixelDataAsync(
        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
        transform,
        ExifOrientationMode.IgnoreExifOrientation,
        ColorManagementMode.ColorManageToSRgb);

    var pixels = pd.DetachPixelData();

    using var mem = new InMemoryRandomAccessStream();
    var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, mem);
    enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
        dstW, dstH, decoder.DpiX, decoder.DpiY, pixels);
    await enc.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        { { "ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single) } });
    await enc.FlushAsync();

    mem.Seek(0);
    await using var outFs = File.Create(dst);
    await mem.AsStreamForRead().CopyToAsync(outFs);

    return (dstW, dstH, pixels.Length);
}

static async Task<(uint outW, uint outH, int bufLen)> GenerateManualRotationAsync(
    string src, string dst,
    uint scaleW, uint scaleH,
    uint finalW, uint finalH,
    BitmapRotation rotation, BitmapFlip flip)
{
    await using var fs = File.OpenRead(src);
    var ras     = fs.AsRandomAccessStream();
    var decoder = await BitmapDecoder.CreateAsync(ras);

    var transform = new BitmapTransform
    {
        ScaledWidth       = scaleW,
        ScaledHeight      = scaleH,
        InterpolationMode = BitmapInterpolationMode.Fant,
        Rotation          = rotation,
        Flip              = flip,
    };

    var pd = await decoder.GetPixelDataAsync(
        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
        transform,
        ExifOrientationMode.IgnoreExifOrientation,
        ColorManagementMode.ColorManageToSRgb);

    var pixels = pd.DetachPixelData();
    int expected = (int)(finalW * finalH * 4);

    Console.WriteLine($"     pixel buf: {pixels.Length} bytes  (expected {expected})" +
                      (pixels.Length == expected ? "  ✓" : "  ✗ MISMATCH"));

    using var mem = new InMemoryRandomAccessStream();
    var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, mem);
    enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
        finalW, finalH, decoder.DpiX, decoder.DpiY, pixels);
    await enc.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        { { "ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single) } });
    await enc.FlushAsync();

    mem.Seek(0);
    await using var outFs = File.Create(dst);
    await mem.AsStreamForRead().CopyToAsync(outFs);

    return (finalW, finalH, pixels.Length);
}

// ════════════════════════════════════════════════════════════════════════════
// Print helpers
// ════════════════════════════════════════════════════════════════════════════

static void PrintResult(string path, uint outW, uint outH, int bufLen,
                        uint refW, uint refH)
{
    double refAspect  = (double)refW / refH;
    double outAspect  = (double)outW / outH;
    string aspectOk   = Math.Abs(refAspect - outAspect) < 0.05
        ? "✓ aspect OK" : $"✗ aspect MISMATCH (ref={refAspect:F3}, out={outAspect:F3})";

    int expected = (int)(outW * outH * 4);
    string bufOk = bufLen == expected ? "✓ buf OK" : $"✗ buf MISMATCH ({bufLen} vs {expected})";

    Console.WriteLine($"  → {outW}×{outH}  |  {aspectOk}  |  {bufOk}  |  {path}");
}

// ════════════════════════════════════════════════════════════════════════════
// EXIF → BitmapTransform mapping
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Computes scale and final dimensions for the manual-rotation strategy.
/// We scale in physical (pre-rotation) space, then rotate.
/// </summary>
static (uint scaleW, uint scaleH, uint finalW, uint finalH) ComputeManualRotationDims(
    uint physW, uint physH, ushort exifOrient, uint box)
{
    bool rotSwaps = exifOrient is 5 or 6 or 7 or 8;

    // Logical (post-rotation) dimensions
    (uint logW, uint logH) = rotSwaps ? (physH, physW) : (physW, physH);

    // FitInside targets the final (post-rotation) output
    (uint finalW, uint finalH) = FitInside(logW, logH, box);

    // The BitmapTransform scales BEFORE rotating, so we need pre-rotation scale dims
    (uint scaleW, uint scaleH) = rotSwaps ? (finalH, finalW) : (finalW, finalH);

    return (scaleW, scaleH, finalW, finalH);
}

static BitmapRotation ExifToRotation(ushort orient) => orient switch
{
    3 or 4 => BitmapRotation.Clockwise180Degrees,
    5 or 6 => BitmapRotation.Clockwise90Degrees,
    7 or 8 => BitmapRotation.Clockwise270Degrees,
    _      => BitmapRotation.None,
};

static BitmapFlip ExifToFlip(ushort orient) => orient switch
{
    2 or 7 => BitmapFlip.Horizontal,
    4 or 5 => BitmapFlip.Vertical,
    _      => BitmapFlip.None,
};

// ════════════════════════════════════════════════════════════════════════════
// EXIF read helper
// ════════════════════════════════════════════════════════════════════════════

static async Task<ushort> ReadExifOrientAsync(BitmapDecoder decoder)
{
    try
    {
        var props = await decoder.BitmapProperties
            .GetPropertiesAsync(new[] { "System.Photo.Orientation" });
        if (props.TryGetValue("System.Photo.Orientation", out var v) && v.Value is ushort u)
            return u;
    }
    catch { }
    return 0;
}

static string DescribeOrientation(ushort o) => o switch
{
    0 => "absent", 1 => "Normal", 2 => "Flip H", 3 => "180°",
    4 => "Flip V", 5 => "Transpose", 6 => "90° CW", 7 => "Transverse",
    8 => "90° CCW", _ => "unknown",
};

// ════════════════════════════════════════════════════════════════════════════

static (uint w, uint h) FitInside(uint srcW, uint srcH, uint box)
{
    if (srcW == 0 || srcH == 0) return (box, box);
    if (srcW >= srcH)
        return (box, (uint)Math.Max(1, Math.Round(box * (double)srcH / srcW)));
    else
        return ((uint)Math.Max(1, Math.Round(box * (double)srcW / srcH)), box);
}
