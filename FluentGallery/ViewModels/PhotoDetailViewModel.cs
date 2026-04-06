using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace FluentGallery.ViewModels;

// ── Navigation parameter ────────────────────────────────────────────────────

/// <summary>
/// Passed from PhotoListPage / AllPhotosPage when navigating to PhotoDetailPage.
/// </summary>
public sealed record PhotoDetailArgs(IReadOnlyList<Photo> Photos, int InitialIndex);

// ── Filmstrip item ──────────────────────────────────────────────────────────

/// <summary>Represents a single entry in the bottom filmstrip strip.</summary>
public sealed partial class PhotoThumbItem : ObservableObject
{
    public Photo Photo { get; }

    [ObservableProperty] public partial string? ThumbPath  { get; set; }
    [ObservableProperty] public partial bool    IsSelected { get; set; }

    public PhotoThumbItem(Photo photo, string? thumbPath)
    {
        Photo     = photo;
        ThumbPath = thumbPath;
    }
}

// ── Undo stack entry ────────────────────────────────────────────────────────

/// <summary>
/// Lightweight in-memory undo entry. Full restoration data is persisted in the
/// <c>DeletedPhotos</c> database table (row Id stored here).
/// </summary>
public sealed record UndoEntry(long DeletedPhotoDbId, string FilePath, int IndexWas);

// ── ViewModel ───────────────────────────────────────────────────────────────

/// <summary>
/// ViewModel for <c>PhotoDetailPage</c>.
/// Manages the current photo, navigation, info-panel data,
/// filmstrip thumbnails, rotation, deletion and undo.
/// </summary>
public sealed partial class PhotoDetailViewModel : ObservableObject
{
    private readonly DatabaseService               _db;
    private readonly ThumbnailService              _thumbnail;
    private readonly ExifService                   _exif;
    private readonly ILogger<PhotoDetailViewModel> _logger;

    private IReadOnlyList<Photo>     _photos = Array.Empty<Photo>();
    private CancellationTokenSource? _preloadCts;
    private AppSettings              _settings = new();

    // ── Undo stack ──────────────────────────────────────────────────────────

    private const int MaxUndoHistory = 100;
    private readonly Stack<UndoEntry> _undoStack = new();

    public bool CanUndo => _undoStack.Count > 0;

    // ── Core photo state ────────────────────────────────────────────────────

    [ObservableProperty] public partial Photo?  CurrentPhoto     { get; set; }
    [ObservableProperty] public partial int     CurrentIndex     { get; set; }
    [ObservableProperty] public partial string? CurrentImagePath { get; set; }

    // ── Navigation state ────────────────────────────────────────────────────

    [ObservableProperty] public partial bool CanGoPrevious { get; set; }
    [ObservableProperty] public partial bool CanGoNext     { get; set; }

    // ── UI panel states ─────────────────────────────────────────────────────

    [ObservableProperty] public partial bool IsInfoPanelOpen { get; set; }

    // ── Info panel display properties ───────────────────────────────────────

    [ObservableProperty] public partial string? InfoFileName    { get; set; }
    [ObservableProperty] public partial string? InfoFilePath    { get; set; }
    [ObservableProperty] public partial string? InfoFileSize    { get; set; }
    [ObservableProperty] public partial string? InfoResolution  { get; set; }
    [ObservableProperty] public partial string? InfoCreatedAt   { get; set; }
    [ObservableProperty] public partial string? InfoModifiedAt  { get; set; }
    [ObservableProperty] public partial string? InfoTakenAt     { get; set; }
    [ObservableProperty] public partial string? InfoCamera      { get; set; }
    [ObservableProperty] public partial string? InfoLens        { get; set; }
    [ObservableProperty] public partial string? InfoAperture    { get; set; }
    [ObservableProperty] public partial string? InfoShutter     { get; set; }
    [ObservableProperty] public partial string? InfoIso         { get; set; }
    [ObservableProperty] public partial string? InfoFocalLength { get; set; }
    [ObservableProperty] public partial string? InfoGps         { get; set; }
    [ObservableProperty] public partial string? InfoOrientation { get; set; }
    [ObservableProperty] public partial string? InfoColorSpace  { get; set; }
    [ObservableProperty] public partial string? InfoBitDepth    { get; set; }

    // GIF-specific (null / hidden for non-GIF photos)
    [ObservableProperty] public partial string? InfoGifDuration  { get; set; }
    [ObservableProperty] public partial string? InfoGifFrames    { get; set; }
    [ObservableProperty] public partial string? InfoGifFrameRate { get; set; }

    // ── Settings ─────────────────────────────────────────────────────────────

    /// <summary>Whether to show a confirmation dialog before deleting.</summary>
    public bool ConfirmBeforeDelete => _settings.ConfirmBeforeDelete;

    /// <summary>Number of adjacent photos to preload (from settings, default 5).</summary>
    public int PreloadCount => _settings.PreloadCount;

    // ── Filmstrip ───────────────────────────────────────────────────────────

    public ObservableCollection<PhotoThumbItem> FilmStripItems { get; } = new();

    // ── Constructor ─────────────────────────────────────────────────────────

    public PhotoDetailViewModel(
        DatabaseService              db,
        ThumbnailService             thumbnail,
        ExifService                  exif,
        ILogger<PhotoDetailViewModel> logger)
    {
        _db        = db;
        _thumbnail = thumbnail;
        _exif      = exif;
        _logger    = logger;
    }

    // ── Initialise ──────────────────────────────────────────────────────────

    /// <summary>
    /// Populates the ViewModel from a photo list and an initial index.
    /// Called by the page's <c>OnNavigatedTo</c>.
    /// </summary>
    public async Task InitializeAsync(
        IReadOnlyList<Photo> photos,
        int                  initialIndex,
        DispatcherQueue      dispatcher,
        CancellationToken    ct = default)
    {
        _photos = photos;

        _settings = await _db.LoadSettingsAsync(ct);

        // Reset so that PropertyChanged fires unconditionally when NavigateToIndexAsync
        // sets the real path — guards against re-entering with the same photo path.
        CurrentImagePath = null;

        // Build filmstrip skeleton (thumbnail loaded lazily)
        FilmStripItems.Clear();
        for (int i = 0; i < photos.Count; i++)
            FilmStripItems.Add(new PhotoThumbItem(photos[i], null));

        await NavigateToIndexAsync(initialIndex, ct);

        // Kick off background filmstrip thumbnail loading
        _preloadCts?.Cancel();
        _preloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = LoadFilmStripThumbsAsync(dispatcher, _preloadCts.Token);
    }

    /// <summary>
    /// Returns the file paths of adjacent photos to preload.
    /// Preloads <see cref="PreloadCount"/> photos in each direction (N±1 … N±PreloadCount),
    /// totalling up to <c>PreloadCount * 2</c> photos, next photos weighted first.
    /// </summary>
    public IReadOnlyList<string> GetPreloadPaths(int currentIndex)
    {
        var result = new List<string>();
        int count  = PreloadCount;

        // Build alternating deltas: +1,-1,+2,-2,...,+count,-count
        for (int step = 1; step <= count; step++)
        {
            foreach (int sign in new[] { 1, -1 })
            {
                int i = currentIndex + sign * step;
                if (i >= 0 && i < _photos.Count)
                    result.Add(_photos[i].FilePath);
            }
        }
        return result;
    }

    // ── Settings helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Disables the delete-confirmation dialog and persists the setting.
    /// </summary>
    public async Task DisableDeleteConfirmAsync(CancellationToken ct = default)
    {
        _settings.ConfirmBeforeDelete = false;
        await _db.SaveSettingsAsync(_settings, ct);
    }

    // ── Navigation commands ─────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousPhotoAsync() =>
        await NavigateToIndexAsync(CurrentIndex - 1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextPhotoAsync() =>
        await NavigateToIndexAsync(CurrentIndex + 1);

    partial void OnCanGoPreviousChanged(bool value) => PreviousPhotoCommand.NotifyCanExecuteChanged();
    partial void OnCanGoNextChanged(bool value)     => NextPhotoCommand.NotifyCanExecuteChanged();

    // ── Navigation core ─────────────────────────────────────────────────────

    public async Task NavigateToIndexAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _photos.Count) return;

        CurrentIndex     = index;
        CurrentPhoto     = _photos[index];
        CurrentImagePath = CurrentPhoto.FilePath;
        CanGoPrevious    = index > 0;
        CanGoNext        = index < _photos.Count - 1;

        // Update filmstrip selection
        for (int i = 0; i < FilmStripItems.Count; i++)
            FilmStripItems[i].IsSelected = (i == index);

        UpdateInfoPanelFast(CurrentPhoto);
        var filePath = CurrentPhoto.FilePath;
        _ = LoadExtendedExifAsync(filePath, ct);
    }

    // ── Info panel ──────────────────────────────────────────────────────────

    private void UpdateInfoPanelFast(Photo photo)
    {
        InfoFileName   = photo.FileName;
        InfoFilePath   = photo.FilePath;
        InfoFileSize   = FormatFileSize(photo.FileSize);
        InfoResolution = (photo.Width.HasValue && photo.Height.HasValue)
            ? $"{photo.Width} × {photo.Height}"
            : null;
        InfoCreatedAt  = FormatIsoDate(photo.CreatedAt);
        InfoModifiedAt = FormatIsoDate(photo.ModifiedAt);
        InfoTakenAt    = FormatIsoDate(photo.TakenAt);
        InfoCamera      = JoinNonEmpty(photo.CameraMake, photo.CameraModel);
        InfoLens        = null;
        InfoAperture    = null;
        InfoShutter     = null;
        InfoIso         = null;
        InfoFocalLength = null;
        InfoColorSpace   = null;
        InfoBitDepth     = null;
        InfoGifDuration  = null;
        InfoGifFrames    = null;
        InfoGifFrameRate = null;
        InfoGps = (photo.Latitude.HasValue && photo.Longitude.HasValue)
            ? $"{photo.Latitude:F6}, {photo.Longitude:F6}"
            : null;
        InfoOrientation = FormatOrientation(photo.Orientation);
    }

    private async Task LoadExtendedExifAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var exif = await Task.Run(() => _exif.ReadExif(filePath), ct);
            InfoLens        = exif.LensModel;
            InfoAperture    = exif.Aperture.HasValue    ? $"f/{exif.Aperture:F1}"       : null;
            InfoShutter     = FormatShutter(exif.ShutterSpeed);
            InfoIso         = exif.Iso.HasValue          ? $"ISO {exif.Iso}"            : null;
            InfoFocalLength = exif.FocalLength.HasValue  ? $"{exif.FocalLength:F0} mm"  : null;
            InfoColorSpace  = exif.ColorSpace;
            InfoBitDepth    = exif.BitDepth.HasValue     ? $"{exif.BitDepth} bit"       : null;

            if (string.Equals(
                    Path.GetExtension(filePath), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                await LoadGifInfoAsync(filePath, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Extended EXIF load failed for {Path}", filePath);
        }
    }

    /// <summary>
    /// Reads frame count, total duration and frame rate from a GIF file via WIC.
    /// Each frame's delay is stored in the GCE (/grctlext/Delay) in centiseconds.
    /// </summary>
    private async Task LoadGifInfoAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(ct);
            using var ras = await storageFile.OpenAsync(Windows.Storage.FileAccessMode.Read).AsTask(ct);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras).AsTask(ct);

            uint frameCount = decoder.FrameCount;
            if (frameCount == 0) return;

            // Sum per-frame delays (in centiseconds; 0 means "use default ~10 cs").
            double totalCs = 0;
            for (uint i = 0; i < frameCount; i++)
            {
                var frame = await decoder.GetFrameAsync(i).AsTask(ct);
                try
                {
                    var props = await frame.BitmapProperties
                        .GetPropertiesAsync(["/grctlext/Delay"]).AsTask(ct);

                    if (props.TryGetValue("/grctlext/Delay", out var v) && v.Value is ushort delay)
                        totalCs += delay > 0 ? delay : 10; // treat 0 as 10 cs (browser default)
                    else
                        totalCs += 10;
                }
                catch
                {
                    totalCs += 10;
                }
            }

            double totalMs  = totalCs * 10.0;
            double totalSec = totalMs / 1000.0;
            double fps      = totalSec > 0 ? frameCount / totalSec : 0;

            string durationStr = totalSec >= 1
                ? $"{totalSec:F2} s"
                : $"{totalMs:F0} ms";

            InfoGifFrames    = $"{frameCount} 帧";
            InfoGifDuration  = durationStr;
            InfoGifFrameRate = fps > 0 ? $"{fps:F2} fps" : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GIF info load failed for {Path}", filePath);
        }
    }

    // ── Rotation ────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task RotateAsync(bool clockwise, CancellationToken ct = default)
    {
        if (CurrentPhoto is null) return;

        try
        {
            int oldOrientation = CurrentPhoto.Orientation ?? 1;
            int newOrientation = clockwise
                ? RotateCw(oldOrientation)
                : RotateCcw(oldOrientation);

            await WriteExifOrientationAsync(CurrentPhoto.FilePath, newOrientation, ct);

            CurrentPhoto.Orientation = newOrientation;
            await _db.UpdatePhotoAsync(CurrentPhoto, ct);

            var path = CurrentImagePath;
            CurrentImagePath = null;
            CurrentImagePath = path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rotate failed for {Path}", CurrentPhoto?.FilePath);
        }
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the current photo to the Windows Recycle Bin, saves a full
    /// snapshot (including thumbnail info) to the <c>DeletedPhotos</c> table,
    /// removes it from the DB and filmstrip, and pushes an undo entry.
    /// </summary>
    /// <returns>The deleted photo's file name on success; <c>null</c> on failure.</returns>
    public async Task<string?> DeleteAsync(CancellationToken ct = default)
    {
        if (CurrentPhoto is null) return null;

        var photo     = CurrentPhoto;
        int indexWas  = CurrentIndex;
        int nextIndex = CurrentIndex < _photos.Count - 1 ? CurrentIndex : CurrentIndex - 1;

        // Capture thumbnail info before deletion (cascade will drop the Thumbnails row)
        var thumb = await _db.GetThumbnailAsync(photo.Id, ct);

        // Move file to the Windows Recycle Bin
        bool moved = await RecycleBinHelper.MoveToRecycleBinAsync(photo.FilePath);
        if (!moved)
        {
            _logger.LogWarning("MoveToRecycleBin failed for {Path}", photo.FilePath);
            return null;
        }

        // Save restoration snapshot (photo JSON + thumbnail path) in DB
        long deletedId = await _db.InsertDeletedPhotoAsync(
            photo,
            thumb?.ThumbPath,
            thumb?.SourceModifiedAt,
            ct);

        // Delete from Photos table (cascade removes Thumbnails row)
        await _db.DeletePhotoAsync(photo.Id, ct);

        // Push undo entry — trim oldest when at capacity
        if (_undoStack.Count >= MaxUndoHistory)
        {
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            foreach (var item in items.Take(MaxUndoHistory - 1).Reverse())
                _undoStack.Push(item);
        }
        _undoStack.Push(new UndoEntry(deletedId, photo.FilePath, indexWas));

        // Rebuild photo list
        var newList = _photos.Where(p => p.Id != photo.Id).ToList();
        _photos = newList;

        var filmItem = FilmStripItems.FirstOrDefault(f => f.Photo.Id == photo.Id);
        if (filmItem is not null) FilmStripItems.Remove(filmItem);

        if (newList.Count == 0)
        {
            CurrentPhoto     = null;
            CurrentImagePath = null;
            CanGoPrevious    = false;
            CanGoNext        = false;
        }
        else
        {
            await NavigateToIndexAsync(Math.Clamp(nextIndex, 0, newList.Count - 1), ct);
        }

        return photo.FileName;
    }

    // ── Undo delete ─────────────────────────────────────────────────────────

    /// <summary>
    /// Restores the most recently deleted photo from the Windows Recycle Bin,
    /// re-inserts it into the database with all original metadata, and
    /// re-creates the thumbnail DB record so the filmstrip shows correctly.
    /// </summary>
    /// <returns>The restored photo's file name on success; <c>null</c> on failure.</returns>
    public async Task<string?> UndoDeleteAsync(CancellationToken ct = default)
    {
        if (_undoStack.Count == 0) return null;

        var entry = _undoStack.Pop();

        // Load full snapshot from DB
        var record = await _db.GetDeletedPhotoAsync(entry.DeletedPhotoDbId, ct);
        if (record is null)
        {
            _logger.LogWarning("DeletedPhoto record {Id} not found in DB", entry.DeletedPhotoDbId);
            return null;
        }

        // Restore the file from the Windows Recycle Bin
        bool restored = await RecycleBinHelper.RestoreFromRecycleBinAsync(record.FilePath);
        if (!restored)
        {
            _logger.LogWarning("RestoreFromRecycleBin failed for {Path}", record.FilePath);
            // Put the entry back so the user sees the accurate error (don't lose it)
            _undoStack.Push(entry);
            return null;
        }

        // Deserialise the original Photo object
        var photo = System.Text.Json.JsonSerializer.Deserialize<Photo>(record.PhotoJson);
        if (photo is null) return null;

        // Re-insert into Photos table with a fresh ID
        photo.Id  = 0;
        var newId = await _db.InsertPhotoAsync(photo, ct);
        photo.Id  = newId;

        // Re-create the Thumbnails row so the filmstrip shows the cached thumbnail
        if (record.ThumbPath is not null &&
            record.ThumbSourceModifiedAt is not null &&
            File.Exists(record.ThumbPath))
        {
            await _db.UpsertThumbnailAsync(new Thumbnail
            {
                PhotoId          = newId,
                ThumbPath        = record.ThumbPath,
                SourceModifiedAt = record.ThumbSourceModifiedAt,
            }, ct);
        }

        // Clean up the undo snapshot from DB
        await _db.DeleteDeletedPhotoAsync(entry.DeletedPhotoDbId, ct);

        // Insert back into the in-memory photo list at the original position
        int insertAt = Math.Clamp(entry.IndexWas, 0, _photos.Count);
        var newList  = _photos.ToList();
        newList.Insert(insertAt, photo);
        _photos = newList;

        // Insert filmstrip item with the known thumbnail path (no re-generation needed)
        var thumbPath  = record.ThumbPath is not null && File.Exists(record.ThumbPath)
            ? record.ThumbPath
            : null;
        var thumbItem = new PhotoThumbItem(photo, thumbPath);
        FilmStripItems.Insert(insertAt, thumbItem);

        await NavigateToIndexAsync(insertAt, ct);
        return photo.FileName;
    }

    // ── Toggle info panel ───────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleInfoPanel() => IsInfoPanelOpen = !IsInfoPanelOpen;

    // ── Background filmstrip thumbnail loader ───────────────────────────────

    private Task LoadFilmStripThumbsAsync(DispatcherQueue dispatcher, CancellationToken ct)
        => Task.Run(() => LoadFilmStripThumbsCoreAsync(dispatcher, ct), ct);

    private async Task LoadFilmStripThumbsCoreAsync(DispatcherQueue dispatcher, CancellationToken ct)
    {
        for (int i = 0; i < _photos.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var path = await _thumbnail.GetOrCreateThumbnailAsync(_photos[i], ct)
                    .ConfigureAwait(false);
                if (path is null || i >= FilmStripItems.Count) continue;

                int    captured     = i;
                string capturedPath = path;
                dispatcher.TryEnqueue(() =>
                {
                    if (captured < FilmStripItems.Count)
                        FilmStripItems[captured].ThumbPath = capturedPath;
                });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Filmstrip thumb failed for index {I}", i);
            }
        }
    }

    // ── EXIF orientation write-back ─────────────────────────────────────────

    private static async Task WriteExifOrientationAsync(
        string filePath, int newOrientation, CancellationToken ct)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N") + Path.GetExtension(filePath));

        try
        {
            await using var srcStream = File.OpenRead(filePath);
            var srcRas  = srcStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(srcRas).AsTask(ct);

            using var memRas = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder
                .CreateForTranscodingAsync(memRas, decoder).AsTask(ct);

            var props = new BitmapPropertySet
            {
                {
                    "System.Photo.Orientation",
                    new BitmapTypedValue(
                        (ushort)newOrientation,
                        Windows.Foundation.PropertyType.UInt16)
                }
            };
            await encoder.BitmapProperties.SetPropertiesAsync(props).AsTask(ct);
            await encoder.FlushAsync().AsTask(ct);

            // Write to a temp file first, then atomically rename over the original.
            // This ensures the original is never left in a truncated/corrupt state
            // if the process crashes or is cancelled mid-write.
            memRas.Seek(0);
            await using (var dstStream = File.Create(tempPath))
                await memRas.AsStreamForRead().CopyToAsync(dstStream, ct);

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath)) FileGuard.DeleteTempFile(tempPath);
            throw;
        }
    }

    // ── Orientation rotation helpers ────────────────────────────────────────

    private static readonly int[] RotCwTable  = { 0, 6, 7, 8, 5, 2, 3, 4, 1 };
    private static readonly int[] RotCcwTable = { 0, 8, 5, 6, 7, 4, 1, 2, 3 };

    private static int RotateCw(int orientation)
    {
        var idx = Math.Clamp(orientation, 1, 8);
        return RotCwTable[idx];
    }

    private static int RotateCcw(int orientation)
    {
        var idx = Math.Clamp(orientation, 1, 8);
        return RotCcwTable[idx];
    }

    // ── Formatting helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Parses an ISO 8601 date string and returns it formatted as local time.
    /// Returns <c>null</c> for null / empty input.
    /// </summary>
    private static string? FormatIsoDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        return DateTime.TryParse(iso, null,
                   System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : iso;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private static string? FormatOrientation(int? orientation) => orientation switch
    {
        1 => "正常",
        2 => "水平翻转",
        3 => "旋转 180°",
        4 => "垂直翻转",
        5 => "旋转 90° CCW + 水平翻转",
        6 => "旋转 90° CW",
        7 => "旋转 90° CW + 水平翻转",
        8 => "旋转 90° CCW",
        _ => null
    };

    private static string? FormatShutter(double? seconds)
    {
        if (!seconds.HasValue) return null;
        if (seconds >= 1) return $"{seconds:F1} s";
        var denom = (int)Math.Round(1.0 / seconds.Value);
        return $"1/{denom} s";
    }

    private static string? JoinNonEmpty(params string?[] parts)
    {
        var joined = string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _preloadCts?.Cancel();
        _preloadCts?.Dispose();
        _preloadCts = null;
    }
}
