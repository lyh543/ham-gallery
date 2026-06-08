using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using FluentGallery.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace FluentGallery.ViewModels;

// ── Navigation parameters ────────────────────────────────────────────────────

/// <summary>
/// Passed from PhotoListPage / AllPhotosPage when navigating to PhotoDetailPage.
/// </summary>
public sealed record PhotoDetailArgs(IReadOnlyList<Photo> Photos, int InitialIndex);

/// <summary>
/// Passed when the user opens a single image file directly (e.g. file association,
/// drag-and-drop, or "Open with"). The detail page will search the database for
/// sibling photos in the same directory to populate the filmstrip.
/// </summary>
public sealed record PhotoDetailFileArgs(string FilePath);

public sealed record RotationPersistedEventArgs(long PhotoId, string FilePath, string? ThumbPath, string ModifiedAt);

public sealed record RotationPersistFailedEventArgs(string FilePath, Exception Exception);

// ── Preload state ────────────────────────────────────────────────────────────

public enum PreloadState
{
    /// <summary>Not yet queued for preloading.</summary>
    NotLoaded,
    /// <summary>Preload task has been dispatched but not completed.</summary>
    Loading,
    /// <summary>Image is ready in the preload cache.</summary>
    Loaded,
}

// ── Filmstrip item ──────────────────────────────────────────────────────────

/// <summary>Represents a single entry in the bottom filmstrip strip.</summary>
public sealed partial class PhotoThumbItem : ObservableObject
{
    public Photo Photo { get; }

    [ObservableProperty] public partial string?      ThumbPath         { get; set; }
    [ObservableProperty] public partial bool         IsSelected        { get; set; }
    [ObservableProperty] public partial PreloadState PreloadState      { get; set; } = PreloadState.NotLoaded;
    [ObservableProperty] public partial bool         ShowPreloadBadge  { get; set; }

    /// <summary>True when the loading spinner badge should be visible.</summary>
    public bool LoadingBadgeVisible => ShowPreloadBadge && PreloadState == PreloadState.Loading;

    /// <summary>True when the loaded checkmark badge should be visible.</summary>
    public bool LoadedBadgeVisible  => ShowPreloadBadge && PreloadState == PreloadState.Loaded;

    partial void OnPreloadStateChanged(PreloadState value)
    {
        OnPropertyChanged(nameof(LoadingBadgeVisible));
        OnPropertyChanged(nameof(LoadedBadgeVisible));
    }

    partial void OnShowPreloadBadgeChanged(bool value)
    {
        OnPropertyChanged(nameof(LoadingBadgeVisible));
        OnPropertyChanged(nameof(LoadedBadgeVisible));
    }

    public PhotoThumbItem(Photo photo, string? thumbPath)
    {
        Photo     = photo;
        ThumbPath = CreateDisplayThumbPath(thumbPath);
    }

    public static string? CreateDisplayThumbPath(string? thumbPath) => thumbPath;
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
public sealed partial class PhotoDetailViewModel : ObservableObject, IDisposable
{
    private sealed record RotationPersistRequest(Photo PhotoSnapshot, int Orientation, int Sequence);

    private readonly DatabaseService               _db;
    private readonly ThumbnailService              _thumbnail;
    private readonly ExifService                   _exif;
    private readonly ScanService                   _scan;
    private readonly ThumbnailRefreshService       _thumbnailRefresh;
    private readonly ILogger<PhotoDetailViewModel> _logger;

    private IReadOnlyList<Photo>     _photos = Array.Empty<Photo>();
    private CancellationTokenSource? _exifCts;
    private AppSettings              _settings = new();
    private readonly object          _rotationPersistGate = new();
    private readonly Queue<RotationPersistRequest> _rotationPersistQueue = new();
    private bool                     _rotationPersistWorkerRunning;
    private int                      _rotateSequence = 0;

    public event EventHandler<RotationPersistedEventArgs>? RotationPersisted;
    public event EventHandler<RotationPersistFailedEventArgs>? RotationPersistFailed;

    // ── Undo stack ──────────────────────────────────────────────────────────

    private const int MaxUndoHistory = 100;
    private readonly Stack<UndoEntry> _undoStack = new();

    public bool CanUndo => _undoStack.Count > 0;

    // ── Core photo state ────────────────────────────────────────────────────

    [ObservableProperty] public partial Photo?  CurrentPhoto     { get; set; }
    [ObservableProperty] public partial int     CurrentIndex     { get; set; }
    [ObservableProperty] public partial string? CurrentImagePath { get; set; }

    private int _previousSelectedIndex = -1;

    // ── Navigation state ────────────────────────────────────────────────────

    [ObservableProperty] public partial bool CanGoPrevious { get; set; }
    [ObservableProperty] public partial bool CanGoNext     { get; set; }

    // ── Rotation state ──────────────────────────────────────────────────────

    /// <summary>Whether the current file format supports EXIF-orientation write-back.</summary>
    [ObservableProperty] public partial bool CanRotate { get; private set; }

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

    // ── Filmstrip availability ───────────────────────────────────────────────

    /// <summary>
    /// Whether the filmstrip is available for the current session.
    /// False when the user opens a single file from a directory that has not been
    /// indexed in the database — in that case the filmstrip pin button is disabled.
    /// </summary>
    [ObservableProperty] public partial bool IsFilmStripAvailable { get; set; } = true;

    /// <summary>
    /// The album ID of the directory from which a file was opened via file association.
    /// Null if the directory is not indexed or the page was opened normally.
    /// </summary>
    public long? AlbumId { get; private set; }

    // ── Settings ─────────────────────────────────────────────────────────────

    /// <summary>Whether to show a confirmation dialog before deleting.</summary>
    public bool ConfirmBeforeDelete => _settings.ConfirmBeforeDelete;

    /// <summary>Number of photos before the current one to preload (from settings, default 2).</summary>
    public int PreloadCountBack => _settings.PreloadCountBack;

    /// <summary>Number of photos after the current one to preload (from settings, default 5).</summary>
    public int PreloadCountForward => _settings.PreloadCountForward;

    /// <summary>Whether the filmstrip is pinned (always visible). Persisted to DB.</summary>
    [ObservableProperty] public partial bool FilmStripPinned { get; set; }

    /// <summary>Whether to show preload-state badges on filmstrip thumbnails. Persisted to DB.</summary>
    [ObservableProperty] public partial bool ShowPreloadStatus { get; set; }

    /// <summary>Whether debug mode keeps photo-detail chrome always visible. Persisted to DB.</summary>
    [ObservableProperty] public partial bool DebugKeepPhotoDetailChromeVisible { get; set; }

    // ── Filmstrip ───────────────────────────────────────────────────────────

    public ObservableCollection<PhotoThumbItem> FilmStripItems { get; } = new();

    // ── Constructor ─────────────────────────────────────────────────────────

    public PhotoDetailViewModel(
        DatabaseService               db,
        ThumbnailService              thumbnail,
        ExifService                   exif,
        ScanService                   scan,
        ThumbnailRefreshService       thumbnailRefresh,
        ILogger<PhotoDetailViewModel> logger)
    {
        _db        = db;
        _thumbnail = thumbnail;
        _exif      = exif;
        _scan      = scan;
        _thumbnailRefresh = thumbnailRefresh;
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

        FilmStripPinned                  = _settings.FilmStripPinned;
        ShowPreloadStatus                = _settings.ShowPreloadStatus;
        DebugKeepPhotoDetailChromeVisible = _settings.DebugKeepPhotoDetailChromeVisible;

        // Reset so that PropertyChanged fires unconditionally when NavigateToIndexAsync
        // sets the real path — guards against re-entering with the same photo path.
        CurrentImagePath = null;

        // Build filmstrip skeleton (thumbnail loaded lazily)
        FilmStripItems.Clear();
        _previousSelectedIndex = -1;
        for (int i = 0; i < photos.Count; i++)
            FilmStripItems.Add(new PhotoThumbItem(photos[i], null));

        await NavigateToIndexAsync(initialIndex, ct);

        // Filmstrip thumbnails now load on demand via container virtualization.
    }

    // ── Initialise from single file ──────────────────────────────────────────

    /// <summary>
    /// Initialises the ViewModel when a single image file is opened directly.
    /// Queries the database for sibling photos in the same directory:
    /// <list type="bullet">
    ///   <item>Directory found → filmstrip available; current file is located in the
    ///         list or a synthetic <see cref="Photo"/> is inserted at the sorted position.</item>
    ///   <item>Directory not found → single-file mode; <see cref="IsFilmStripAvailable"/>
    ///         is set to <c>false</c> and the filmstrip pin button is disabled.</item>
    /// </list>
    /// </summary>
    public async Task InitializeFromFileAsync(
        string            filePath,
        DispatcherQueue   dispatcher,
        CancellationToken ct = default)
    {
        var dirPath = Path.GetDirectoryName(filePath) ?? string.Empty;

        // Query sibling photos from the database (returns empty when dir not indexed).
        var siblingPhotos = await _db.GetPhotosByDirectoryAsync(dirPath, ct);

        List<Photo> photos;
        int         initialIndex;
        Photo?      syntheticPhoto = null;

        if (siblingPhotos.Count > 0)
        {
            // Directory is known — filmstrip is available.
            IsFilmStripAvailable = true;
            AlbumId = siblingPhotos[0].AlbumId;

            // Try to find the current file in the existing list.
            int existingIndex = -1;
            for (int i = 0; i < siblingPhotos.Count; i++)
            {
                if (string.Equals(siblingPhotos[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = i;
                    break;
                }
            }

            photos = siblingPhotos.ToList();

            if (existingIndex >= 0)
            {
                initialIndex = existingIndex;
            }
            else
            {
                // File is not yet indexed — create a synthetic Photo and insert it
                // at the correct sort position (by FileName, matching the list order).
                syntheticPhoto = await BuildPhotoFromFileAsync(filePath, ct);
                initialIndex   = FindSortedInsertPosition(photos, syntheticPhoto);
                photos.Insert(initialIndex, syntheticPhoto);
            }
        }
        else
        {
            // Directory is not indexed — single-file mode, filmstrip unavailable.
            IsFilmStripAvailable = false;
            syntheticPhoto       = await BuildPhotoFromFileAsync(filePath, ct);
            photos               = new List<Photo> { syntheticPhoto };
            initialIndex         = 0;
        }

        await InitializeAsync(photos, initialIndex, dispatcher, ct);
    }

    /// <summary>
    /// Adds the current file's directory to scan settings if needed, persists the
    /// updated settings, and starts a fresh background scan so the directory can
    /// become an indexed album.
    /// </summary>
    public async Task<bool> EnsureDirectoryIndexedAsync(
        string            directoryPath,
        DispatcherQueue   dispatcher,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return false;

        var settings = await _db.LoadSettingsAsync(ct);
        bool added = false;
        if (!settings.ScanDirectories.Contains(directoryPath, StringComparer.OrdinalIgnoreCase))
        {
            settings.ScanDirectories.Add(directoryPath);
            added = true;
        }

        settings.RecursiveScan = true;

        if (added)
            await _db.SaveSettingsAsync(settings, ct);

        await _scan.StartAsync(settings, dispatcher);
        return true;
    }

    /// <summary>
    /// Returns true when the current photo was opened from a directory that has not
    /// yet been indexed into an album and therefore cannot populate the filmstrip.
    /// </summary>
    public bool ShouldPromptToIndexCurrentDirectory()
        => !IsFilmStripAvailable && !string.IsNullOrEmpty(CurrentImagePath);

    /// <summary>
    /// Reloads the current direct-open file against the latest database state.
    /// Used after a newly-added directory finishes scanning so the filmstrip can
    /// become available without closing the detail page.
    /// </summary>
    public Task ReloadCurrentFileContextAsync(
        DispatcherQueue   dispatcher,
        CancellationToken ct = default)
    {
        var filePath = CurrentImagePath;
        if (string.IsNullOrEmpty(filePath))
            return Task.CompletedTask;

        return InitializeFromFileAsync(filePath, dispatcher, ct);
    }

    /// <summary>
    /// Returns the directory that contains the current image, or null when none.
    /// </summary>
    public string? GetCurrentDirectoryPath()
        => string.IsNullOrEmpty(CurrentImagePath)
            ? null
            : Path.GetDirectoryName(CurrentImagePath);

    /// <summary>
    /// Returns true when the current image path belongs to the specified directory.
    /// </summary>
    public bool IsCurrentFileInDirectory(string directoryPath)
    {
        var currentDir = GetCurrentDirectoryPath();
        return !string.IsNullOrEmpty(currentDir)
            && string.Equals(currentDir, directoryPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a lightweight <see cref="Photo"/> object from a file path without
    /// touching the database. For files outside the scan index, EXIF and dimensions
    /// are read on demand so the detail page can still show complete metadata.
    /// The returned instance has <c>Id = 0</c> (synthetic).
    /// </summary>
    private async Task<Photo> BuildPhotoFromFileAsync(string filePath, CancellationToken ct)
    {
        var fi = new FileInfo(filePath);
        var photo = new Photo
        {
            Id         = 0,
            FilePath   = filePath,
            FileName   = Path.GetFileName(filePath),
            FileSize   = fi.Exists ? fi.Length : 0,
            CreatedAt  = fi.Exists ? fi.CreationTimeUtc.ToString("O") : DateTime.UtcNow.ToString("O"),
            ModifiedAt = fi.Exists ? fi.LastWriteTimeUtc.ToString("O") : DateTime.UtcNow.ToString("O"),
        };

        if (!fi.Exists)
            return photo;

        try
        {
            var exif = await Task.Run(() => _exif.ReadExif(filePath), ct);
            photo.Width       = exif.Width;
            photo.Height      = exif.Height;
            photo.TakenAt     = exif.TakenAt;
            photo.Latitude    = exif.Latitude;
            photo.Longitude   = exif.Longitude;
            photo.CameraModel = exif.CameraModel;
            photo.CameraMake  = exif.CameraMake;
            photo.Orientation = exif.Orientation;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metadata preload failed for direct-open file {Path}", filePath);
        }

        return photo;
    }

    /// <summary>
    /// Returns the index at which <paramref name="photo"/> should be inserted into
    /// <paramref name="sortedPhotos"/> to maintain TakenAt → FileName ascending order
    /// (matching the DB query used to load sibling photos).
    /// </summary>
    private static int FindSortedInsertPosition(List<Photo> sortedPhotos, Photo photo)
    {
        for (int i = 0; i < sortedPhotos.Count; i++)
        {
            if (ComparePhotoOrdering(sortedPhotos[i], photo) > 0)
                return i;
        }
        return sortedPhotos.Count;
    }

    private static int ComparePhotoOrdering(Photo left, Photo right)
    {
        int takenAtOrder = string.Compare(left.TakenAt, right.TakenAt, StringComparison.Ordinal);
        if (takenAtOrder != 0)
            return takenAtOrder;

        return string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the file paths of adjacent photos to preload.
    /// Preloads <see cref="PreloadCountForward"/> photos after and <see cref="PreloadCountBack"/>
    /// photos before the current one, forward photos weighted first.
    /// </summary>
    public IReadOnlyList<string> GetPreloadPaths(int currentIndex)
    {
        var result   = new List<string>();
        int forward  = PreloadCountForward;
        int back     = PreloadCountBack;
        int maxSteps = Math.Max(forward, back);

        // Interleave forward (+) and backward (-) steps, forward first each round
        for (int step = 1; step <= maxSteps; step++)
        {
            if (step <= forward)
            {
                int i = currentIndex + step;
                if (i >= 0 && i < _photos.Count)
                    result.Add(_photos[i].FilePath);
            }
            if (step <= back)
            {
                int i = currentIndex - step;
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

    /// <summary>
    /// Toggles the filmstrip pinned state and persists it to the database.
    /// </summary>
    public async Task ToggleFilmStripPinnedAsync(CancellationToken ct = default)
    {
        FilmStripPinned           = !FilmStripPinned;
        _settings.FilmStripPinned = FilmStripPinned;
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
        CanRotate        = ExifOrientationWriter.IsRotatableFormat(CurrentPhoto.FilePath);

        // Update filmstrip selection — only toggle the previous and new items
        if (_previousSelectedIndex >= 0 && _previousSelectedIndex < FilmStripItems.Count)
            FilmStripItems[_previousSelectedIndex].IsSelected = false;
        FilmStripItems[index].IsSelected = true;
        _previousSelectedIndex = index;

        UpdateInfoPanelFast(CurrentPhoto);
        var filePath = CurrentPhoto.FilePath;
        _exifCts?.Cancel();
        _exifCts?.Dispose();
        var exifCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _exifCts = exifCts;
        _ = LoadExtendedExifAsync(filePath, exifCts.Token);
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
    public Task RotateAsync(bool clockwise, CancellationToken ct = default)
    {
        if (CurrentPhoto is null || !CanRotate) return Task.CompletedTask;

        ct.ThrowIfCancellationRequested();

        int sequence = Interlocked.Increment(ref _rotateSequence);
        int oldOrientation = CurrentPhoto.Orientation ?? 1;
        int newOrientation = clockwise
            ? ExifOrientationWriter.RotateCw(oldOrientation)
            : ExifOrientationWriter.RotateCcw(oldOrientation);

        CurrentPhoto.Orientation = newOrientation;
        InfoOrientation = FormatOrientation(newOrientation);

        _logger.LogDebug(
            "RotateAsync queued: seq={Sequence}, path={Path}, clockwise={Clockwise}, oldOrientation={OldOrientation}, newOrientation={NewOrientation}",
            sequence,
            CurrentPhoto.FilePath,
            clockwise,
            oldOrientation,
            newOrientation);

        EnqueueRotationPersist(new RotationPersistRequest(ClonePhoto(CurrentPhoto), newOrientation, sequence));
        return Task.CompletedTask;
    }

    private void EnqueueRotationPersist(RotationPersistRequest request)
    {
        lock (_rotationPersistGate)
        {
            _rotationPersistQueue.Enqueue(request);
            if (_rotationPersistWorkerRunning)
                return;

            _rotationPersistWorkerRunning = true;
            _ = Task.Run(ProcessRotationPersistQueueAsync);
        }
    }

    private async Task ProcessRotationPersistQueueAsync()
    {
        while (true)
        {
            RotationPersistRequest request;
            lock (_rotationPersistGate)
            {
                if (_rotationPersistQueue.Count == 0)
                {
                    _rotationPersistWorkerRunning = false;
                    return;
                }

                request = _rotationPersistQueue.Dequeue();
            }

            await PersistRotationRequestAsync(request).ConfigureAwait(false);
        }
    }

    private async Task PersistRotationRequestAsync(RotationPersistRequest request)
    {
        try
        {
            _logger.LogDebug(
                "RotateAsync persist begin: seq={Sequence}, path={Path}, orientation={Orientation}",
                request.Sequence,
                request.PhotoSnapshot.FilePath,
                request.Orientation);

            await ExifOrientationWriter.WriteAsync(request.PhotoSnapshot.FilePath, request.Orientation).ConfigureAwait(false);

            request.PhotoSnapshot.Orientation = request.Orientation;
            request.PhotoSnapshot.ModifiedAt = File.GetLastWriteTimeUtc(request.PhotoSnapshot.FilePath).ToString("O");
            await _db.UpdatePhotoAsync(request.PhotoSnapshot).ConfigureAwait(false);

            var thumbPath = await _thumbnail.RegenerateThumbnailAsync(request.PhotoSnapshot).ConfigureAwait(false);

            _logger.LogDebug(
                "RotateAsync persist complete: seq={Sequence}, path={Path}, orientation={Orientation}, thumbPath={ThumbPath}",
                request.Sequence,
                request.PhotoSnapshot.FilePath,
                request.Orientation,
                thumbPath);

            if (CurrentPhoto?.Id == request.PhotoSnapshot.Id)
                CurrentPhoto.ModifiedAt = request.PhotoSnapshot.ModifiedAt;

            _thumbnailRefresh.NotifyThumbnailRefreshed(
                request.PhotoSnapshot.Id,
                request.PhotoSnapshot.FilePath,
                thumbPath,
                request.PhotoSnapshot.ModifiedAt);

            RotationPersisted?.Invoke(
                this,
                new RotationPersistedEventArgs(
                    request.PhotoSnapshot.Id,
                    request.PhotoSnapshot.FilePath,
                    thumbPath,
                    request.PhotoSnapshot.ModifiedAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rotate failed for {Path}", request.PhotoSnapshot.FilePath);
            RotationPersistFailed?.Invoke(this, new RotationPersistFailedEventArgs(request.PhotoSnapshot.FilePath, ex));
        }
    }

    private static Photo ClonePhoto(Photo photo) => new()
    {
        Id = photo.Id,
        FilePath = photo.FilePath,
        FileName = photo.FileName,
        FileSize = photo.FileSize,
        Width = photo.Width,
        Height = photo.Height,
        TakenAt = photo.TakenAt,
        CreatedAt = photo.CreatedAt,
        ModifiedAt = photo.ModifiedAt,
        Latitude = photo.Latitude,
        Longitude = photo.Longitude,
        CameraModel = photo.CameraModel,
        CameraMake = photo.CameraMake,
        Orientation = photo.Orientation,
        AlbumId = photo.AlbumId,
        IsPinned = photo.IsPinned,
    };

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

        // Move file to the Windows Recycle Bin
        bool moved = await RecycleBinHelper.MoveToRecycleBinAsync(photo.FilePath);
        if (!moved)
        {
            _logger.LogWarning("MoveToRecycleBin failed for {Path}", photo.FilePath);
            return null;
        }

        // Only persist undo data and remove from DB for photos that are actually indexed.
        // Synthetic photos (Id == 0) were never in the database, so skip those steps.
        if (photo.Id != 0)
        {
            // Capture thumbnail info before deletion (cascade will drop the Thumbnails row)
            var thumb = await _db.GetThumbnailAsync(photo.Id, ct);

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
        }

        // Rebuild photo list
        var newList = _photos.Where(p => !ReferenceEquals(p, photo)).ToList();
        _photos = newList;

        var filmItem = FilmStripItems.FirstOrDefault(f => ReferenceEquals(f.Photo, photo));
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
        _exifCts?.Cancel();
        _exifCts?.Dispose();
        _exifCts = null;
    }
}
