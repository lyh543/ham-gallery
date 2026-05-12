using ImageMagick;
using Windows.Storage;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace FluentGallery.Helpers;

/// <summary>
/// Writes an updated EXIF orientation tag into JPEG or HEIC/HEIF image files in-place.
/// Writes are atomic: content is written to a temp file, then renamed over the original.
/// </summary>
public static class ExifOrientationWriter
{
    // EXIF orientation transition tables (valid indices are 1–8; index 0 is unused).
    private static readonly int[] RotCwTable  = { 0, 6, 7, 8, 5, 2, 3, 4, 1 };
    private static readonly int[] RotCcwTable = { 0, 8, 5, 6, 7, 4, 1, 2, 3 };

    private static readonly HashSet<string> RotatableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".heic", ".heif" };

    private static readonly HashSet<string> HeicExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    /// <summary>Returns <c>true</c> for file formats that support EXIF orientation write-back.</summary>
    public static bool IsRotatableFormat(string filePath) =>
        RotatableExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>Returns the new EXIF orientation value after a clockwise 90° rotation.</summary>
    public static int RotateCw(int orientation)
    {
        var idx = Math.Clamp(orientation, 1, 8);
        return RotCwTable[idx];
    }

    /// <summary>Returns the new EXIF orientation value after a counter-clockwise 90° rotation.</summary>
    public static int RotateCcw(int orientation)
    {
        var idx = Math.Clamp(orientation, 1, 8);
        return RotCcwTable[idx];
    }

    /// <summary>
    /// Writes <paramref name="newOrientation"/> into the EXIF orientation tag of
    /// <paramref name="filePath"/>, dispatching to the correct strategy per format.
    /// </summary>
    public static Task WriteAsync(string filePath, int newOrientation, CancellationToken ct = default) =>
        HeicExtensions.Contains(Path.GetExtension(filePath))
            ? WriteHeicAsync(filePath, newOrientation, ct)
            : WriteJpegAsync(filePath, newOrientation, ct);

    /// <summary>
    /// Updates the EXIF orientation tag in a JPEG file via WIC transcoding.
    /// The source file is first copied into an in-memory stream so the file handle
    /// is fully released before the atomic temp-file rename.
    /// </summary>
    public static async Task WriteJpegAsync(string filePath, int newOrientation, CancellationToken ct)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N") + Path.GetExtension(filePath));

        try
        {
            using var srcRas = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            {
                using var srcStream = File.OpenRead(filePath);
                await srcStream.CopyToAsync(srcRas.AsStreamForWrite(), ct).ConfigureAwait(false);
            }
            srcRas.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(srcRas).AsTask(ct).ConfigureAwait(false);

            using var memRas = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(memRas, decoder).AsTask(ct).ConfigureAwait(false);

            var props = new BitmapPropertySet
            {
                {
                    "System.Photo.Orientation",
                    new BitmapTypedValue(
                        (ushort)newOrientation,
                        Windows.Foundation.PropertyType.UInt16)
                }
            };
            await encoder.BitmapProperties.SetPropertiesAsync(props).AsTask(ct).ConfigureAwait(false);
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

            memRas.Seek(0);
            using (var dstStream = File.Create(tempPath))
                await memRas.AsStreamForRead().CopyToAsync(dstStream, ct).ConfigureAwait(false);

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath)) FileGuard.DeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Updates the EXIF orientation tag in a HEIC/HEIF file via Magick.NET.
    /// WIC has no built-in HEIC encoder, so Magick.NET (libheif) is used instead.
    /// </summary>
    public static async Task WriteHeicAsync(string filePath, int newOrientation, CancellationToken ct)
    {
        try
        {
            await WriteHeicViaShellPropertiesAsync(filePath, newOrientation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid().ToString("N") + Path.GetExtension(filePath));

            try
            {
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    using var image = new MagickImage(filePath);
                    var exif = image.GetExifProfile() ?? new ExifProfile();
                    exif.SetValue(ExifTag.Orientation, (ushort)newOrientation);
                    image.SetProfile(exif);
                    image.Orientation = (OrientationType)newOrientation;
                    image.Write(tempPath);
                }, ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tempPath)) FileGuard.DeleteTempFile(tempPath);
                throw;
            }
        }
    }

    private static async Task WriteHeicViaShellPropertiesAsync(
        string filePath,
        int newOrientation,
        CancellationToken ct)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(ct).ConfigureAwait(false);
        var properties = new Dictionary<string, object>
        {
            ["System.Photo.Orientation"] = (ushort)newOrientation,
        };
        await storageFile.Properties.SavePropertiesAsync(properties).AsTask(ct).ConfigureAwait(false);
    }
}
