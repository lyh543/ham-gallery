using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using Windows.Storage;

namespace FluentGallery.ViewModels;

public enum PhotoSortField { Name, Size, CreatedAt, ModifiedAt, TakenAt }

/// <summary>ViewModel for the per-album photo grid (section 5.3).</summary>
public sealed partial class PhotoListViewModel : ObservableObject
{
    private readonly DatabaseService  _db;
    private readonly ScanService      _scan;
    private readonly ThumbnailService _thumbs;
    private readonly ExifService      _exif;
    private readonly ILogger<PhotoListViewModel> _logger;

    private long _albumId;

    /// <summary>Id of the album currently loaded. Set by <see cref="LoadAsync"/>.</summary>
    public long AlbumId => _albumId;

    // ── Collections ──────────────────────────────────────────────────────────

    public ObservableCollection<PhotoItemViewModel> Photos { get; } = new();

    // ── Observable properties ────────────────────────────────────────────────

    [ObservableProperty] public partial string        AlbumName          { get; set; } = string.Empty;
    [ObservableProperty] public partial bool          IsLoading          { get; set; }
    [ObservableProperty] public partial bool          IsMultiSelectMode  { get; set; }
    [ObservableProperty] public partial bool          IsRenamingAlbum    { get; set; }
    [ObservableProperty] public partial string        EditAlbumName      { get; set; } = string.Empty;
    [ObservableProperty] public partial PhotoSortField SortField         { get; set; } = PhotoSortField.TakenAt;
    [ObservableProperty] public partial SortDirection  SortDirection     { get; set; } = SortDirection.Descending;

    // Prevents sort from being written back to DB while LoadAsync is initialising
    // SortField / SortDirection from the album's stored preferences.
    private bool _loadingSort;
    [ObservableProperty] public partial int            PhotoCardWidth      { get; set; } = 165; // CardWidthSteps[7]
    [ObservableProperty] public partial bool           ShowCardSizeToast   { get; set; }
    [ObservableProperty] public partial bool           ConfirmBeforeDelete { get; set; } = true;

    // Non-uniform zoom steps: smaller diffs at small sizes, larger diffs at large sizes.
    // Range: 60–400 px.
    private static readonly int[] CardWidthSteps =
        [60, 70, 80, 90, 100, 110, 120, 130, 150, 175, 200, 230, 260, 300, 350, 400];

    public PhotoListViewModel(
        DatabaseService              db,
        ScanService                  scan,
        ThumbnailService             thumbs,
        ExifService                  exif,
        ILogger<PhotoListViewModel>  logger)
    {
        _db     = db;
        _scan   = scan;
        _thumbs = thumbs;
        _exif   = exif;
        _logger = logger;
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>Loads album metadata and the sorted photo list from the database.</summary>
    public async Task LoadAsync(long albumId, CancellationToken ct = default)
    {
        _albumId  = albumId;
        IsLoading = true;
        try
        {
            var album    = await _db.GetAlbumAsync(albumId, ct);
            AlbumName    = album?.Name ?? string.Empty;

            var settings        = await _db.LoadSettingsAsync(ct);
            ConfirmBeforeDelete = settings.ConfirmBeforeDelete;
            ShowCardSizeToast   = settings.ShowCardSizeToast;
            PhotoCardWidth      = SnapToStep(settings.PhotoCardWidth);

            // Restore the per-album sort preference stored in the database.
            _loadingSort = true;
            try
            {
                SortField     = album is not null ? (PhotoSortField)album.PhotoSortField     : PhotoSortField.TakenAt;
                SortDirection = album is not null ? (SortDirection) album.PhotoSortDirection : SortDirection.Ascending;
            }
            finally { _loadingSort = false; }

            var raw    = await _db.GetPhotosByAlbumAsync(albumId, ct);
            var sorted = ApplySort(raw).ToList();

            Photos.Clear();
            foreach (var p in sorted)
                Photos.Add(new PhotoItemViewModel(p));
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Sort ─────────────────────────────────────────────────────────────────

    partial void OnSortFieldChanged(PhotoSortField oldValue, PhotoSortField newValue)
    {
        ReSortInPlace();
        if (!_loadingSort) SavePhotoSort();
    }

    partial void OnSortDirectionChanged(SortDirection oldValue, SortDirection newValue)
    {
        ReSortInPlace();
        if (!_loadingSort) SavePhotoSort();
    }

    private async void SavePhotoSort()
    {
        if (_albumId == 0) return;
        try
        {
            await _db.SaveAlbumPhotoSortAsync(_albumId, (int)SortField, (int)SortDirection);
        }
        catch { /* best-effort: sort preference loss is non-critical */ }
    }

    private void ReSortInPlace()
    {
        var snapshot = Photos.Select(vm => vm.GetPhoto()).ToList();
        var sorted   = ApplySort(snapshot).ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var target  = sorted[i];
            var current = Photos.First(vm => vm.Id == target.Id);
            int from    = Photos.IndexOf(current);
            if (from != i) Photos.Move(from, i);
        }
    }

    private IEnumerable<Photo> ApplySort(IEnumerable<Photo> src) => SortField switch
    {
        PhotoSortField.Size       => SortDirection == SortDirection.Ascending
            ? src.OrderBy(p => p.FileSize)
            : src.OrderByDescending(p => p.FileSize),
        PhotoSortField.CreatedAt  => SortDirection == SortDirection.Ascending
            ? src.OrderBy(p => p.CreatedAt)
            : src.OrderByDescending(p => p.CreatedAt),
        PhotoSortField.ModifiedAt => SortDirection == SortDirection.Ascending
            ? src.OrderBy(p => p.ModifiedAt)
            : src.OrderByDescending(p => p.ModifiedAt),
        PhotoSortField.TakenAt    => SortDirection == SortDirection.Ascending
            ? src.OrderBy(TakenAtOrFallback)
            : src.OrderByDescending(TakenAtOrFallback),
        _                         => SortDirection == SortDirection.Ascending
            ? src.OrderBy(p => p.FileName, StringComparer.CurrentCultureIgnoreCase)
            : src.OrderByDescending(p => p.FileName, StringComparer.CurrentCultureIgnoreCase),
    };

    /// <summary>
    /// Returns TakenAt if present; otherwise falls back to whichever of CreatedAt /
    /// ModifiedAt is earlier. ISO 8601 strings compare correctly as plain strings.
    /// </summary>
    private static string TakenAtOrFallback(Photo p) =>
        !string.IsNullOrEmpty(p.TakenAt)
            ? p.TakenAt
            : string.Compare(p.CreatedAt, p.ModifiedAt, StringComparison.Ordinal) <= 0
                ? p.CreatedAt
                : p.ModifiedAt;

    // ── Add photos ───────────────────────────────────────────────────────────

    /// <summary>
    /// Imports the picked files into the current album.
    /// EXIF is read synchronously on a background thread (Task.Run) to keep UI responsive.
    /// </summary>
    public async Task AddPhotosAsync(IReadOnlyList<StorageFile> files, CancellationToken ct = default)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = await file.GetBasicPropertiesAsync().AsTask(ct);
                var exif = await Task.Run(() => _exif.ReadExif(file.Path), ct);
                var now  = DateTime.UtcNow.ToString("O");

                var photo = new Photo
                {
                    FilePath    = file.Path,
                    FileName    = file.Name,
                    FileSize    = (long)info.Size,
                    Width       = exif.Width,
                    Height      = exif.Height,
                    TakenAt     = exif.TakenAt,
                    CreatedAt   = now,
                    ModifiedAt  = info.DateModified.UtcDateTime.ToString("O"),
                    Latitude    = exif.Latitude,
                    Longitude   = exif.Longitude,
                    CameraModel = exif.CameraModel,
                    CameraMake  = exif.CameraMake,
                    Orientation = exif.Orientation,
                    AlbumId     = _albumId,
                };

                var id = await _db.InsertPhotoAsync(photo, ct);
                photo.Id = id;

                var vm = new PhotoItemViewModel(photo);
                Photos.Add(vm);

                // Kick off thumbnail generation without blocking the import loop
                _ = vm.LoadThumbnailAsync(_thumbs, CancellationToken.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import photo {Path}", file.Path);
            }
        }
    }

    // ── Delete photos ────────────────────────────────────────────────────────

    /// <summary>
    /// Moves each photo to the Recycle Bin and removes its database record.
    /// Files that cannot be found are still removed from the DB.
    /// </summary>
    public async Task DeletePhotosAsync(IEnumerable<PhotoItemViewModel> items, CancellationToken ct = default)
    {
        foreach (var vm in items.ToList())
        {
            try
            {
                await RecycleBinHelper.MoveToRecycleBinAsync(vm.FilePath);
                await _db.DeletePhotoAsync(vm.Id, ct);
                Photos.Remove(vm);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete {Path}", vm.FilePath);
            }
        }
    }

    // ── Move to album ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reassigns each photo's <c>AlbumId</c> to <paramref name="targetAlbumId"/> and
    /// removes it from the current view (since it belongs to a different album now).
    /// </summary>
    public async Task MoveToAlbumAsync(
        IEnumerable<PhotoItemViewModel> items,
        long                            targetAlbumId,
        CancellationToken               ct = default)
    {
        foreach (var vm in items.ToList())
        {
            var photo = vm.GetPhoto();
            photo.AlbumId = targetAlbumId;
            await _db.UpdatePhotoAsync(photo, ct);
            Photos.Remove(vm);
        }
    }

    // ── Album rename ──────────────────────────────────────────────────────────

    public void BeginRenameAlbum()
    {
        EditAlbumName   = AlbumName;
        IsRenamingAlbum = true;
    }

    public async Task CommitRenameAlbumAsync(CancellationToken ct = default)
    {
        IsRenamingAlbum = false;
        var trimmed = EditAlbumName.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == AlbumName) return;

        var album = await _db.GetAlbumAsync(_albumId, ct);
        if (album is null) return;
        album.Name = trimmed;
        await _db.UpdateAlbumAsync(album, ct);
        AlbumName = trimmed;
    }

    public void CancelRenameAlbum()
    {
        IsRenamingAlbum = false;
        EditAlbumName   = AlbumName;
    }

    // ── Multi-select toggle ───────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleMultiSelectMode() => IsMultiSelectMode = !IsMultiSelectMode;

    // ── Card width (zoom) ─────────────────────────────────────────────────────

    [RelayCommand]
    public void ZoomIn()
    {
        int idx = CurrentStepIndex();
        if (idx < CardWidthSteps.Length - 1)
            PhotoCardWidth = CardWidthSteps[idx + 1];
    }

    [RelayCommand]
    public void ZoomOut()
    {
        int idx = CurrentStepIndex();
        if (idx > 0)
            PhotoCardWidth = CardWidthSteps[idx - 1];
    }

    /// <summary>Steps in the direction of <paramref name="delta"/> (sign only).</summary>
    public void AdjustCardWidth(int delta)
    {
        if (delta > 0) ZoomIn();
        else if (delta < 0) ZoomOut();
    }

    partial void OnPhotoCardWidthChanged(int oldValue, int newValue)
    {
        SaveCardWidthSettings();
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanZoomOut));
    }

    public bool CanZoomIn  => PhotoCardWidth < CardWidthSteps[^1];
    public bool CanZoomOut => PhotoCardWidth > CardWidthSteps[0];

    private int CurrentStepIndex()
    {
        int idx = Array.BinarySearch(CardWidthSteps, PhotoCardWidth);
        if (idx < 0) idx = Math.Clamp(~idx, 0, CardWidthSteps.Length - 1);
        return idx;
    }

    private static int SnapToStep(int value)
    {
        int idx = Array.BinarySearch(CardWidthSteps, value);
        if (idx >= 0) return value;
        idx = ~idx;
        if (idx >= CardWidthSteps.Length) return CardWidthSteps[^1];
        if (idx == 0) return CardWidthSteps[0];
        return value - CardWidthSteps[idx - 1] <= CardWidthSteps[idx] - value
            ? CardWidthSteps[idx - 1]
            : CardWidthSteps[idx];
    }

    private async void SaveCardWidthSettings()
    {
        try
        {
            var settings          = await _db.LoadSettingsAsync();
            settings.PhotoCardWidth = PhotoCardWidth;
            await _db.SaveSettingsAsync(settings);
        }
        catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns all albums (used to populate the "Move to album" submenu).</summary>
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
        => _db.GetAlbumsAsync(ct);

    public async Task<IReadOnlyList<(string Name, string DirectoryPath)>> GetAlbumDirectoriesAsync(
        CancellationToken ct = default)
    {
        var albums = await _db.GetAlbumsAsync(ct);
        return albums
            .Where(a => !string.IsNullOrWhiteSpace(a.DirectoryPath))
            .Select(a => (Name: a.Name, DirectoryPath: a.DirectoryPath!))
            .DistinctBy(a => a.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task MovePhotosToDirectoryAsync(
        IEnumerable<PhotoItemViewModel> items,
        string targetDir,
        CancellationToken ct = default)
    {
        var list = items.ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(targetDir)) return;

        await RunWithScanPausedAsync(async () =>
        {
            Directory.CreateDirectory(targetDir);
            var targetAlbumId = await _db.GetOrCreateDirectoryAlbumAsync(targetDir, ct);

            foreach (var vm in list)
            {
                ct.ThrowIfCancellationRequested();

                var photo = vm.GetPhoto();
                var targetPath = GetTargetPath(targetDir, photo.FileName);
                await Task.Run(() => File.Move(photo.FilePath, targetPath), ct);

                photo.FilePath = targetPath;
                photo.FileName = Path.GetFileName(targetPath);
                photo.AlbumId = targetAlbumId;
                photo.ModifiedAt = File.GetLastWriteTimeUtc(targetPath).ToString("O");
                await _db.UpdatePhotoAsync(photo, ct);
            }
        });

        foreach (var vm in list)
            Photos.Remove(vm);
    }

    public async Task CopyPhotosToDirectoryAsync(
        IEnumerable<PhotoItemViewModel> items,
        string targetDir,
        CancellationToken ct = default)
    {
        var list = items.ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(targetDir)) return;

        await RunWithScanPausedAsync(async () =>
        {
            Directory.CreateDirectory(targetDir);
            foreach (var vm in list)
            {
                ct.ThrowIfCancellationRequested();
                var targetPath = GetTargetPath(targetDir, vm.FileName);
                await Task.Run(() => File.Copy(vm.FilePath, targetPath), ct);
            }
        });
    }

    public void OpenPhotoInExplorer(PhotoItemViewModel vm)
    {
        if (!File.Exists(vm.FilePath)) return;

        try
        {
            var directory = Path.GetDirectoryName(vm.FilePath);
            if (string.IsNullOrWhiteSpace(directory)) return;

            WindowsApiHelper.SHParseDisplayName(directory, IntPtr.Zero, out var pidlFolder, 0, out _);
            if (pidlFolder == IntPtr.Zero) return;

            try
            {
                WindowsApiHelper.SHParseDisplayName(vm.FilePath, IntPtr.Zero, out var pidlFile, 0, out _);
                if (pidlFile == IntPtr.Zero) return;

                try
                {
                    int hResult = WindowsApiHelper.SHOpenFolderAndSelectItems(pidlFolder, 1, [pidlFile], 0);
                    if (hResult != 0)
                        _logger.LogWarning("SHOpenFolderAndSelectItems failed with HRESULT: {HResult:X8}", hResult);
                }
                finally
                {
                    WindowsApiHelper.CoTaskMemFree(pidlFile);
                }
            }
            finally
            {
                WindowsApiHelper.CoTaskMemFree(pidlFolder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show file in explorer {Path}", vm.FilePath);
        }
    }

    private static string GetTargetPath(string targetDir, string fileName)
    {
        var destination = Path.Combine(targetDir, fileName);
        if (!File.Exists(destination)) return destination;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; ; i++)
        {
            destination = Path.Combine(targetDir, $"{name} ({i}){ext}");
            if (!File.Exists(destination)) return destination;
        }
    }

    private async Task RunWithScanPausedAsync(Func<Task> action)
    {
        var settings = await _db.LoadSettingsAsync();
        await _scan.StopAsync();

        try
        {
            await action();
        }
        finally
        {
            await _scan.StartAsync(settings, DispatcherQueue.GetForCurrentThread());
        }
    }
}
