using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using Windows.Storage;

namespace FluentGallery.ViewModels;

public enum PhotoSortField { Name, Size, CreatedAt, ModifiedAt, TakenAt, Natural }

/// <summary>ViewModel for the per-album photo grid (section 5.3).</summary>
public sealed partial class PhotoListViewModel : ObservableObject
{
    private readonly DatabaseService  _db;
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
    [ObservableProperty] public partial PhotoSortField SortField         { get; set; } = PhotoSortField.Natural;
    [ObservableProperty] public partial SortDirection  SortDirection     { get; set; } = SortDirection.Ascending;
    [ObservableProperty] public partial int            ColumnCount       { get; set; } = 4;
    [ObservableProperty] public partial bool           ConfirmBeforeDelete { get; set; } = true;

    public PhotoListViewModel(
        DatabaseService              db,
        ThumbnailService             thumbs,
        ExifService                  exif,
        ILogger<PhotoListViewModel>  logger)
    {
        _db     = db;
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

            var settings = await _db.LoadSettingsAsync(ct);
            ConfirmBeforeDelete = settings.ConfirmBeforeDelete;

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

    partial void OnSortFieldChanged(PhotoSortField oldValue, PhotoSortField newValue)       => ReSortInPlace();
    partial void OnSortDirectionChanged(SortDirection oldValue, SortDirection newValue) => ReSortInPlace();

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
            ? src.OrderBy(p => p.TakenAt)
            : src.OrderByDescending(p => p.TakenAt),
        PhotoSortField.Natural    => SortDirection == SortDirection.Ascending
            ? NaturalSortHelper.SortNatural(src)
            : NaturalSortHelper.SortNatural(src).Reverse(),
        _                         => SortDirection == SortDirection.Ascending
            ? src.OrderBy(p => p.FileName, StringComparer.CurrentCultureIgnoreCase)
            : src.OrderByDescending(p => p.FileName, StringComparer.CurrentCultureIgnoreCase),
    };

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

    // ── Column count (pinch gesture) ──────────────────────────────────────────

    /// <summary>Adjusts column count by <paramref name="delta"/>, clamped to [2, 8].</summary>
    public void AdjustColumnCount(int delta)
        => ColumnCount = Math.Clamp(ColumnCount + delta, 2, 8);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns all albums (used to populate the "Move to album" submenu).</summary>
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
        => _db.GetAlbumsAsync(ct);
}
