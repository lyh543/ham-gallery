using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;

namespace FluentGallery.ViewModels;

/// <summary>
/// ViewModel for the all-photos page (section 5.5).
/// Displays all photos grouped by year/month (timeline), with search/filter,
/// multi-select, delete, move-to-album, and sorting capabilities.
/// </summary>
public sealed partial class AllPhotosViewModel : ObservableObject
{
    private readonly DatabaseService  _db;
    private readonly ScanService      _scan;
    private readonly ThumbnailService _thumbs;
    private readonly ExifService      _exif;
    private readonly ILogger<AllPhotosViewModel> _logger;

    // ── Cached main list ──────────────────────────────────────────────────────

    private List<PhotoItemViewModel> _allPhotos = new();

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] public partial bool          IsLoading          { get; set; }
    [ObservableProperty] public partial bool          IsMultiSelectMode  { get; set; }
    [ObservableProperty] public partial PhotoSortField SortField         { get; set; } = PhotoSortField.TakenAt;
    [ObservableProperty] public partial SortDirection  SortDirection     { get; set; } = SortDirection.Descending;
    [ObservableProperty] public partial int            AllPhotosCardWidth  { get; set; } = 165; // CardWidthSteps[7]
    [ObservableProperty] public partial bool           ShowCardSizeToast   { get; set; }
    [ObservableProperty] public partial bool           ConfirmBeforeDelete { get; set; } = true;

    // ── Search state ──────────────────────────────────────────────────────────

    [ObservableProperty] public partial string   SearchKeyword  { get; set; } = string.Empty;
    [ObservableProperty] public partial string?  SearchDateFrom { get; set; }
    [ObservableProperty] public partial string?  SearchDateTo   { get; set; }
    [ObservableProperty] public partial bool     IsSearchActive { get; set; }

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<PhotoGroupViewModel> Groups { get; } = new();

    /// <summary>Flattened view of all photos across all groups (for GridView binding).</summary>
    public ObservableCollection<PhotoItemViewModel> AllPhotoItems { get; } = new();

    // ── Zoom steps ────────────────────────────────────────────────────────────

    private static readonly int[] CardWidthSteps =
        [60, 70, 80, 90, 100, 110, 120, 130, 150, 175, 200, 230, 260, 300, 350, 400];

    private bool _loadingSort; // Prevents sort from being written while LoadAsync initializes

    // ── Constructor ───────────────────────────────────────────────────────────

    public AllPhotosViewModel(
        DatabaseService              db,
        ScanService                  scan,
        ThumbnailService             thumbs,
        ExifService                  exif,
        ILogger<AllPhotosViewModel>  logger)
    {
        _db     = db;
        _scan   = scan;
        _thumbs = thumbs;
        _exif   = exif;
        _logger = logger;
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>Loads all photos from the database, sorts them, and rebuilds groups.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var settings        = await _db.LoadSettingsAsync(ct);
            ConfirmBeforeDelete = settings.ConfirmBeforeDelete;
            ShowCardSizeToast   = settings.ShowCardSizeToast;
            AllPhotosCardWidth  = SnapToStep(settings.AllPhotosCardWidth);

            var raw = await _db.GetAllPhotosAsync(ct);
            _allPhotos = raw.Select(p => new PhotoItemViewModel(p)).ToList();

            _loadingSort = true;
            try
            {
                SortField = PhotoSortField.TakenAt;
                SortDirection = SortDirection.Descending;
            }
            finally { _loadingSort = false; }

            var sorted = ApplySort(_allPhotos).ToList();
            RebuildGroups(sorted);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task SearchAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SearchKeyword) &&
            string.IsNullOrEmpty(SearchDateFrom) &&
            string.IsNullOrEmpty(SearchDateTo))
        {
            return; // Nothing to search for
        }

        IsLoading = true;
        try
        {
            var photos = await _db.SearchPhotosAsync(
                keyword  : string.IsNullOrWhiteSpace(SearchKeyword) ? null : SearchKeyword.Trim(),
                dateField: "TakenAt",
                dateFrom : SearchDateFrom,
                dateTo   : SearchDateTo,
                albumId  : null,
                ct       : ct);

            var results = photos.Select(p => new PhotoItemViewModel(p)).ToList();
            results = ApplySort(results).ToList();
            RebuildGroups(results);
            IsSearchActive = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ClearSearchAsync(CancellationToken ct = default)
    {
        SearchKeyword  = string.Empty;
        SearchDateFrom = null;
        SearchDateTo   = null;
        IsSearchActive = false;

        // Rebuild groups from the cached main list without querying the database
        var sorted = ApplySort(_allPhotos).ToList();
        RebuildGroups(sorted);

        await Task.CompletedTask;
    }

    // ── Grouping ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Groups photos by year/month and rebuilds the Groups collection.
    /// Also updates AllPhotoItems (flattened view).
    /// Groups are ordered descending (newest first) with latest photos at the top.
    /// </summary>
    private void RebuildGroups(List<PhotoItemViewModel> items)
    {
        var grouped = items
            .GroupBy(vm => GetGroupKey(vm))
            .OrderByDescending(g => g.Key) // Descending: newest months first
            .ToList();

        Groups.Clear();
        AllPhotoItems.Clear();

        foreach (var group in grouped)
        {
            var groupVm = new PhotoGroupViewModel(group.Key);
            foreach (var photo in group)
            {
                groupVm.Photos.Add(photo);
                AllPhotoItems.Add(photo); // Also add to flattened collection
            }
            Groups.Add(groupVm);
        }
    }

    /// <summary>
    /// Extracts the year/month from a photo's date (TakenAt or CreatedAt).
    /// Returns "YYYY年MM月" or "未知日期" if no date is available.
    /// </summary>
    private static string GetGroupKey(PhotoItemViewModel vm)
    {
        var dateStr = !string.IsNullOrEmpty(vm.TakenAt)
            ? vm.TakenAt
            : vm.CreatedAt;

        if (string.IsNullOrEmpty(dateStr) || dateStr.Length < 7)
            return "未知日期";

        // ISO 8601 format: "2024-12-15T10:30:00.0000000Z" → extract "2024-12"
        var parts = dateStr.Substring(0, 7).Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], out var year) && int.TryParse(parts[1], out var month))
            return $"{year}年{month:D2}月";

        return "未知日期";
    }

    // ── Sort ──────────────────────────────────────────────────────────────────

    partial void OnSortFieldChanged(PhotoSortField oldValue, PhotoSortField newValue)
    {
        ReSortGroups();
        if (!_loadingSort) SaveSortPreferences();
    }

    partial void OnSortDirectionChanged(SortDirection oldValue, SortDirection newValue)
    {
        ReSortGroups();
        if (!_loadingSort) SaveSortPreferences();
    }

    private void ReSortGroups()
    {
        var snapshot = Groups.SelectMany(g => g.Photos).ToList();
        var sorted   = ApplySort(snapshot).ToList();
        RebuildGroups(sorted);
    }

    private IEnumerable<PhotoItemViewModel> ApplySort(IEnumerable<PhotoItemViewModel> src) => SortField switch
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

    private static string TakenAtOrFallback(PhotoItemViewModel vm) =>
        !string.IsNullOrEmpty(vm.TakenAt)
            ? vm.TakenAt
            : string.Compare(vm.CreatedAt, vm.ModifiedAt, StringComparison.Ordinal) <= 0
                ? vm.CreatedAt
                : vm.ModifiedAt;

    private async void SaveSortPreferences()
    {
        // For AllPhotosPage, we don't persist sort preferences (no album context)
        // This is intentional — the page resets to TakenAt/Descending on each visit
        await Task.CompletedTask;
    }

    // ── Delete photos ─────────────────────────────────────────────────────────

    public async Task DeletePhotosAsync(IEnumerable<PhotoItemViewModel> items, CancellationToken ct = default)
    {
        foreach (var vm in items.ToList())
        {
            try
            {
                await RecycleBinHelper.MoveToRecycleBinAsync(vm.FilePath);
                await _db.DeletePhotoAsync(vm.Id, ct);

                // Remove from caches and collections
                _allPhotos.RemoveAll(p => p.Id == vm.Id);
                AllPhotoItems.Remove(vm);
                foreach (var group in Groups)
                    group.Photos.Remove(vm);

                // Remove empty groups by rebuilding from non-empty ones
                var nonEmptyGroups = Groups.Where(g => g.Photos.Count > 0).ToList();
                Groups.Clear();
                foreach (var group in nonEmptyGroups)
                    Groups.Add(group);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete {Path}", vm.FilePath);
            }
        }
    }

    // ── Move to album ─────────────────────────────────────────────────────────

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

            // Remove from caches and collections
            _allPhotos.RemoveAll(p => p.Id == vm.Id);
            AllPhotoItems.Remove(vm);
            foreach (var group in Groups)
                group.Photos.Remove(vm);

            // Remove empty groups by rebuilding from non-empty ones
            var nonEmptyGroups = Groups.Where(g => g.Photos.Count > 0).ToList();
            Groups.Clear();
            foreach (var group in nonEmptyGroups)
                Groups.Add(group);
        }
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
            AllPhotosCardWidth = CardWidthSteps[idx + 1];
    }

    [RelayCommand]
    public void ZoomOut()
    {
        int idx = CurrentStepIndex();
        if (idx > 0)
            AllPhotosCardWidth = CardWidthSteps[idx - 1];
    }

    public void AdjustCardWidth(int delta)
    {
        if (delta > 0) ZoomIn();
        else if (delta < 0) ZoomOut();
    }

    partial void OnAllPhotosCardWidthChanged(int oldValue, int newValue)
    {
        SaveCardWidthSettings();
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanZoomOut));
    }

    public bool CanZoomIn  => AllPhotosCardWidth < CardWidthSteps[^1];
    public bool CanZoomOut => AllPhotosCardWidth > CardWidthSteps[0];

    private int CurrentStepIndex()
    {
        int idx = Array.BinarySearch(CardWidthSteps, AllPhotosCardWidth);
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
            var settings             = await _db.LoadSettingsAsync();
            settings.AllPhotosCardWidth = AllPhotosCardWidth;
            await _db.SaveSettingsAsync(settings);
        }
        catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

                var modifiedAt = File.GetLastWriteTimeUtc(targetPath).ToString("O");
                photo.FilePath = targetPath;
                photo.FileName = Path.GetFileName(targetPath);
                photo.AlbumId = targetAlbumId;
                photo.ModifiedAt = modifiedAt;
                await _db.UpdatePhotoAsync(photo, ct);
                vm.UpdateFileLocation(targetPath, photo.FileName, targetAlbumId, modifiedAt);
            }
        });

        await RefreshCurrentViewAsync(ct);
    }

    public async Task CopyPhotosToDirectoryAsync(
        IEnumerable<PhotoItemViewModel> items,
        string targetDir,
        CancellationToken ct = default)
    {
        var list = items.ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(targetDir)) return;
        var copiedItems = new List<PhotoItemViewModel>();

        var settings = await _db.LoadSettingsAsync(ct);
        bool shouldIndex = settings.ScanDirectories.Any(scanDir => IsSameDirectoryOrChild(targetDir, scanDir));
        bool excluded = settings.ExcludeDirectories.Any(excludedDir => IsSameDirectoryOrChild(targetDir, excludedDir));
        long? targetAlbumId = shouldIndex && !excluded
            ? await _db.GetOrCreateDirectoryAlbumAsync(targetDir, ct)
            : null;

        await RunWithScanPausedAsync(async () =>
        {
            Directory.CreateDirectory(targetDir);
            foreach (var vm in list)
            {
                ct.ThrowIfCancellationRequested();
                var targetPath = GetTargetPath(targetDir, vm.FileName);
                await Task.Run(() => File.Copy(vm.FilePath, targetPath), ct);

                if (targetAlbumId.HasValue)
                {
                    var source = vm.GetPhoto();
                    var copied = new Photo
                    {
                        FilePath = targetPath,
                        FileName = Path.GetFileName(targetPath),
                        FileSize = new FileInfo(targetPath).Length,
                        Width = source.Width,
                        Height = source.Height,
                        TakenAt = source.TakenAt,
                        CreatedAt = DateTime.UtcNow.ToString("O"),
                        ModifiedAt = File.GetLastWriteTimeUtc(targetPath).ToString("O"),
                        Latitude = source.Latitude,
                        Longitude = source.Longitude,
                        CameraMake = source.CameraMake,
                        CameraModel = source.CameraModel,
                        Orientation = source.Orientation,
                        AlbumId = targetAlbumId,
                        IsPinned = source.IsPinned,
                    };

                    copied.Id = await _db.InsertPhotoAsync(copied, ct);
                    copiedItems.Add(new PhotoItemViewModel(copied));
                }
            }
        });

        if (copiedItems.Count > 0)
            _allPhotos.AddRange(copiedItems);

        await RefreshCurrentViewAsync(ct);
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

    private async Task RefreshCurrentViewAsync(CancellationToken ct)
    {
        if (IsSearchActive)
            await SearchAsync(ct);
        else
            RebuildGroups(ApplySort(_allPhotos).ToList());
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

    private static bool IsSameDirectoryOrChild(string path, string root)
    {
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collects all photos from all groups in order, for photo detail navigation.</summary>
    public List<Photo> GetAllPhotosForDetail()
        => Groups
            .SelectMany(g => g.Photos)
            .Select(vm => vm.GetPhoto())
            .ToList();
}
