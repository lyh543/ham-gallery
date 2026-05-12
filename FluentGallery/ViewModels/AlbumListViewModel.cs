using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Collections.ObjectModel;

namespace FluentGallery.ViewModels;

public enum AlbumSortField { Name, CreatedAt, ModifiedAt, PhotoCount, TakenAt }
public enum SortDirection  { Ascending, Descending }

/// <summary>ViewModel for the album grid page.</summary>
public sealed partial class AlbumListViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseService  _db;
    private readonly ScanService      _scan;
    private readonly ThumbnailService _thumbnails;
    private readonly ILogger<AlbumListViewModel> _logger;

    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();

    [ObservableProperty] public partial bool           IsLoading      { get; set; }
    [ObservableProperty] public partial bool           IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial int            AlbumCardWidth { get; set; }
    [ObservableProperty] public partial bool           ShowCardSizeToast { get; set; }
    [ObservableProperty] public partial AlbumSortField SortField     { get; set; }
    [ObservableProperty] public partial SortDirection  SortDirection { get; set; }

    // Non-uniform zoom steps: smaller diffs at small sizes, larger diffs at large sizes.
    // Range: 100–300 px.
    private static readonly int[] CardWidthSteps =
        [100, 110, 120, 130, 150, 175, 200, 230, 260, 300];

    // ── Cover-refresh throttle (200 ms timer, UI-thread only) ─────────────────
    //
    // During a background scan, PhotosBatchDiscovered may fire many times per second.
    // Instead of refreshing every album's cover on each event, we collect the affected
    // album IDs and let a DispatcherTimer coalesce updates into 200 ms bursts.

    private DispatcherTimer?                _coverTimer;        // lazily created on first scan event
    private readonly HashSet<long>          _pendingCoverIds  = new();
    private bool                            _isCoverRefreshing;
    private CancellationTokenSource         _pageCts          = new();

    // Prevents sort-settings from being written back to DB while LoadAsync is
    // initialising SortField / SortDirection from the stored AppSettings.
    private bool _loadingSort;

    public AlbumListViewModel(
        DatabaseService db,
        ScanService scan,
        ThumbnailService thumbnails,
        ILogger<AlbumListViewModel> logger)
    {
        _db         = db;
        _scan       = scan;
        _thumbnails = thumbnails;
        _logger     = logger;
        AlbumCardWidth = CardWidthSteps[5]; // default: 170 px
        SortField      = AlbumSortField.TakenAt;
        SortDirection  = SortDirection.Descending;

        _scan.PhotosBatchDiscovered += OnPhotosBatchDiscovered;
        _scan.PhotosBatchUpdated    += OnPhotosBatchUpdated;
        _scan.ScanCompleted         += OnScanCompleted;
    }

    // ── Page lifecycle (called by AlbumListPage.OnNavigatedTo/From) ───────────

    /// <summary>
    /// Reset the page-scoped cancellation token each time the album list page becomes active.
    /// </summary>
    public void ActivatePage()
    {
        if (!_pageCts.IsCancellationRequested) _pageCts.Cancel();
        _pageCts.Dispose();
        _pageCts = new CancellationTokenSource();
    }

    /// <summary>
    /// Cancel in-flight cover loads and stop the refresh timer when leaving the page.
    /// </summary>
    public void DeactivatePage()
    {
        _coverTimer?.Stop();
        _pendingCoverIds.Clear();
        _pageCts.Cancel();
    }

    // ── Scan event handlers ────────────────────────────────────────────────────

    private async void OnPhotosBatchDiscovered(IReadOnlyList<Photo> photos)
    {
        var byAlbum = photos
            .Where(p => p.AlbumId.HasValue)
            .GroupBy(p => p.AlbumId!.Value);

        foreach (var group in byAlbum)
        {
            var existing = Albums.FirstOrDefault(a => a.Id == group.Key);
            if (existing is not null)
            {
                existing.PhotoCount += group.Count();
                existing.TotalSize += group.Sum(p => p.FileSize);
            }
            else
            {
                var album = await _db.GetAlbumAsync(group.Key);
                if (album is not null)
                {
                    album.PhotoCount = group.Count();
                    InsertSorted(new AlbumItemViewModel(album));
                }
            }

            // Mark this album for a cover refresh on the next timer tick
            _pendingCoverIds.Add(group.Key);
        }

        EnsureCoverTimerRunning();
    }

    private void OnPhotosBatchUpdated(IReadOnlyList<Photo> photos) { /* count unchanged */ }

    /// <summary>
    /// Reloads the full album list from the database after a scan completes.
    /// This removes stale directory albums (whose photos were pruned) and applies
    /// the empty-album filter in <see cref="DatabaseService.GetAlbumsAsync"/>.
    /// </summary>
    private async void OnScanCompleted()
    {
        try { await LoadAsync(_pageCts.Token); }
        catch (OperationCanceledException) { }
    }

    // ── Cover-refresh timer ───────────────────────────────────────────────────

    private void EnsureCoverTimerRunning()
    {
        if (_pendingCoverIds.Count == 0) return;

        // DispatcherTimer must be created and used on the UI thread.
        // OnPhotosBatchDiscovered is already dispatched to the UI thread by ScanService.
        if (_coverTimer is null)
        {
            _coverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _coverTimer.Tick += OnCoverTimerTick;
        }

        if (!_coverTimer.IsEnabled)
            _coverTimer.Start();
    }

    private async void OnCoverTimerTick(object? sender, object e)
    {
        if (_pendingCoverIds.Count == 0)
        {
            _coverTimer?.Stop();
            return;
        }

        // Prevent overlapping refresh bursts
        if (_isCoverRefreshing) return;
        _isCoverRefreshing = true;

        var ids = _pendingCoverIds.ToList();
        _pendingCoverIds.Clear();

        var ct = _pageCts.Token;
        try
        {
            foreach (var albumId in ids)
            {
                if (ct.IsCancellationRequested) break;
                var vm = Albums.FirstOrDefault(a => a.Id == albumId);
                if (vm is not null)
                    await vm.LoadCoverAsync(_db, _thumbnails, forceRefresh: true, ct: ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _isCoverRefreshing = false;
        }
    }

    // ── Insert helper ──────────────────────────────────────────────────────────

    private void InsertSorted(AlbumItemViewModel vm)
    {
        int idx = Albums.Count;
        if (SortField == AlbumSortField.Name && SortDirection == SortDirection.Ascending)
        {
            for (int i = 0; i < Albums.Count; i++)
            {
                if (string.Compare(Albums[i].Name, vm.Name,
                        StringComparison.CurrentCultureIgnoreCase) > 0)
                {
                    idx = i;
                    break;
                }
            }
        }
        Albums.Insert(idx, vm);
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _scan.PhotosBatchDiscovered -= OnPhotosBatchDiscovered;
        _scan.PhotosBatchUpdated    -= OnPhotosBatchUpdated;
        _scan.ScanCompleted         -= OnScanCompleted;
        _coverTimer?.Stop();
        _pageCts.Dispose();
    }

    // ── Load ───────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            // Restore persisted sort preferences and card width before populating the grid.
            _loadingSort = true;
            try
            {
                var settings      = await _db.LoadSettingsAsync(ct);
                SortField         = (AlbumSortField)settings.AlbumSortField;
                SortDirection     = (SortDirection)settings.AlbumSortDirection;
                AlbumCardWidth    = SnapToStep(settings.AlbumCardWidth);
                ShowCardSizeToast = settings.ShowCardSizeToast;
            }
            finally { _loadingSort = false; }

            var raw    = await _db.GetAlbumsAsync(ct);
            var sorted = ApplySort(raw);
            SynchronizeAlbums(sorted);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SynchronizeAlbums(IEnumerable<Album> sortedAlbums)
    {
        var ordered = sortedAlbums.ToList();
        var existingById = Albums.ToDictionary(vm => vm.Id);
        var desiredIds = ordered.Select(album => album.Id).ToHashSet();
        bool coverRefreshQueued = false;

        foreach (var stale in Albums.Where(vm => !desiredIds.Contains(vm.Id)).ToList())
            Albums.Remove(stale);

        for (int i = 0; i < ordered.Count; i++)
        {
            var album = ordered[i];
            if (!existingById.TryGetValue(album.Id, out var vm))
            {
                vm = new AlbumItemViewModel(album);
                Albums.Insert(i, vm);
                existingById[album.Id] = vm;
                continue;
            }

            bool coverMayHaveChanged = vm.PhotoCount != album.PhotoCount
                || !string.Equals(vm.MaxPhotoTakenAt, album.MaxPhotoTakenAt, StringComparison.Ordinal)
                || !string.Equals(vm.MaxPhotoCreatedAt, album.MaxPhotoCreatedAt, StringComparison.Ordinal)
                || !string.Equals(vm.MaxPhotoModifiedAt, album.MaxPhotoModifiedAt, StringComparison.Ordinal);

            vm.Name = album.Name;
            vm.PhotoCount = album.PhotoCount;
            vm.CreatedAt = album.CreatedAt;
            vm.ModifiedAt = album.ModifiedAt;
            vm.IsPinned = album.IsPinned;
            vm.MaxPhotoTakenAt = album.MaxPhotoTakenAt;
            vm.MaxPhotoCreatedAt = album.MaxPhotoCreatedAt;
            vm.MaxPhotoModifiedAt = album.MaxPhotoModifiedAt;

            if (coverMayHaveChanged)
            {
                _pendingCoverIds.Add(album.Id);
                coverRefreshQueued = true;
            }

            int currentIndex = Albums.IndexOf(vm);
            if (currentIndex != i)
                Albums.Move(currentIndex, i);
        }

        if (coverRefreshQueued)
            EnsureCoverTimerRunning();
    }

    private async void SaveSortSettings()
    {
        try
        {
            var settings              = await _db.LoadSettingsAsync();
            settings.AlbumSortField     = (int)SortField;
            settings.AlbumSortDirection = (int)SortDirection;
            await _db.SaveSettingsAsync(settings);
        }
        catch { /* best-effort: sort preference loss is non-critical */ }
    }

    private async void SaveCardWidthSettings()
    {
        try
        {
            var settings           = await _db.LoadSettingsAsync();
            settings.AlbumCardWidth = AlbumCardWidth;
            await _db.SaveSettingsAsync(settings);
        }
        catch { /* best-effort */ }
    }

    // ── Sort ───────────────────────────────────────────────────────────────────

    partial void OnSortFieldChanged(AlbumSortField oldValue, AlbumSortField newValue)
    {
        ReSortInPlace();
        if (!_loadingSort) SaveSortSettings();
    }

    partial void OnSortDirectionChanged(SortDirection oldValue, SortDirection newValue)
    {
        ReSortInPlace();
        if (!_loadingSort) SaveSortSettings();
    }

    private void ReSortInPlace()
    {
        var sorted = ApplySort(Albums.Select(vm => new Album
        {
            Id                 = vm.Id,
            Name               = vm.Name,
            CreatedAt          = vm.CreatedAt,
            ModifiedAt         = vm.ModifiedAt,
            PhotoCount         = vm.PhotoCount,
            IsPinned           = vm.IsPinned,
            MaxPhotoTakenAt    = vm.MaxPhotoTakenAt,
            MaxPhotoCreatedAt  = vm.MaxPhotoCreatedAt,
            MaxPhotoModifiedAt = vm.MaxPhotoModifiedAt,
        })).ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var target  = sorted[i];
            var current = Albums.First(vm => vm.Id == target.Id);
            int from    = Albums.IndexOf(current);
            if (from != i) Albums.Move(from, i);
        }
    }

    // Sort key: albums with no photos (null timestamp) sort last in both directions.
    // For all time-based sorts, use the MAX photo timestamp (= time of the most recent photo).
    private static string TimeKey(string? val) => val ?? string.Empty;

    private IEnumerable<Album> ApplySort(IEnumerable<Album> source)
    {
        return SortField switch
        {
            AlbumSortField.TakenAt => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => TimeKey(a.MaxPhotoTakenAt ?? a.MaxPhotoModifiedAt))
                : source.OrderByDescending(a => TimeKey(a.MaxPhotoTakenAt ?? a.MaxPhotoModifiedAt)),
            AlbumSortField.CreatedAt => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => TimeKey(a.MaxPhotoCreatedAt))
                : source.OrderByDescending(a => TimeKey(a.MaxPhotoCreatedAt)),
            AlbumSortField.ModifiedAt => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => TimeKey(a.MaxPhotoModifiedAt))
                : source.OrderByDescending(a => TimeKey(a.MaxPhotoModifiedAt)),
            AlbumSortField.PhotoCount => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => a.PhotoCount)
                : source.OrderByDescending(a => a.PhotoCount),
            _ => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                : source.OrderByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        };
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<AlbumItemViewModel> CreateAlbumAsync(string name, CancellationToken ct = default)
    {
        var album = new Album { Name = name.Trim() };
        var id    = await _db.InsertAlbumAsync(album, ct);
        album.Id  = id;

        var vm  = new AlbumItemViewModel(album);
        int idx = Albums.Count;
        if (SortField == AlbumSortField.Name && SortDirection == SortDirection.Ascending)
        {
            for (int i = 0; i < Albums.Count; i++)
            {
                if (string.Compare(Albums[i].Name, vm.Name, StringComparison.CurrentCultureIgnoreCase) > 0)
                {
                    idx = i;
                    break;
                }
            }
        }
        Albums.Insert(idx, vm);
        return vm;
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    public async Task RenameAlbumAsync(AlbumItemViewModel vm, string newName, CancellationToken ct = default)
    {
        string trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;

        var album = await _db.GetAlbumAsync(vm.Id, ct);
        if (album is null) return;

        if (string.IsNullOrWhiteSpace(album.DirectoryPath))
        {
            album.Name = trimmed;
            await _db.UpdateAlbumAsync(album, ct);
            vm.Name = album.Name;
            ReSortInPlace();
            return;
        }

        string oldDir = album.DirectoryPath;
        string normalizedOldDir = oldDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parentDir = Path.GetDirectoryName(normalizedOldDir);
        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(normalizedOldDir))
            throw new IOException("Album directory not found.");

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Album name contains invalid characters.");

        string newDir = Path.Combine(parentDir, trimmed);
        if (!string.Equals(normalizedOldDir, newDir, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(newDir))
                throw new IOException("Target directory already exists.");

            await RunWithScanPausedAsync(async () =>
            {
                await Task.Run(() => Directory.Move(normalizedOldDir, newDir), ct);

                var photos = await _db.GetPhotosByAlbumAsync(album.Id, ct);
                foreach (var photo in photos)
                {
                    ct.ThrowIfCancellationRequested();
                    string relativePath = Path.GetRelativePath(normalizedOldDir, photo.FilePath);
                    string updatedPath = Path.Combine(newDir, relativePath);
                    photo.FilePath = updatedPath;
                    photo.FileName = Path.GetFileName(updatedPath);
                    photo.ModifiedAt = File.GetLastWriteTimeUtc(updatedPath).ToString("O");
                    await _db.UpdatePhotoAsync(photo, ct);
                }

                var settings = await _db.LoadSettingsAsync(ct);
                bool settingsChanged = ReplaceDirectorySetting(settings.ScanDirectories, normalizedOldDir, newDir)
                    | ReplaceDirectorySetting(settings.ExcludeDirectories, normalizedOldDir, newDir);
                if (settingsChanged)
                    await _db.SaveSettingsAsync(settings, ct);
            });

            album.DirectoryPath = newDir;
            vm.DirectoryPath = newDir;
        }

        album.Name = trimmed;
        await _db.UpdateAlbumAsync(album, ct);
        vm.Name = album.Name;
        ReSortInPlace();
    }

    private static bool ReplaceDirectorySetting(IList<string> directories, string oldDir, string newDir)
    {
        bool changed = false;
        for (int index = 0; index < directories.Count; index++)
        {
            if (!string.Equals(
                    directories[index].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    oldDir,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            directories[index] = newDir;
            changed = true;
        }

        return changed;
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteAlbumAsync(AlbumItemViewModel vm, CancellationToken ct = default)
        => await DeleteAlbumsAsync([vm], ct);

    public async Task<int> DeleteAlbumsAsync(
        IReadOnlyList<AlbumItemViewModel> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return 0;

        var deletedAlbums = new List<AlbumItemViewModel>(items.Count);

        foreach (var album in items)
        {
            bool deleteFailed = false;
            var photos = await _db.GetPhotosByAlbumAsync(album.Id, ct);
            foreach (var photo in photos)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RecycleBinHelper.MoveToRecycleBinAsync(photo.FilePath);
                    await _db.DeletePhotoAsync(photo.Id, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    deleteFailed = true;
                    _logger.LogWarning(ex, "Failed to delete album photo {Path}", photo.FilePath);
                }
            }

            if (deleteFailed)
            {
                _logger.LogWarning("Skipping album deletion because one or more photos failed to delete. AlbumId: {AlbumId}", album.Id);
                continue;
            }

            await _db.DeleteAlbumAsync(album.Id, ct);
            deletedAlbums.Add(album);
        }

        foreach (var album in deletedAlbums)
            Albums.Remove(album);

        return deletedAlbums.Count;
    }

    public async Task ExcludeAlbumsAsync(
        IReadOnlyList<AlbumItemViewModel> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        await RunWithScanPausedAsync(async () =>
        {
            var settings = await _db.LoadSettingsAsync(ct);
            foreach (var dir in items.Select(a => a.DirectoryPath)
                         .OfType<string>()
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!settings.ExcludeDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    settings.ExcludeDirectories.Add(dir);
            }

            await _db.SaveSettingsAsync(settings, ct);
            await _db.DeletePhotosByAlbumIdsAsync(items.Select(a => a.Id), ct);
            await _db.DeleteAlbumsAsync(items.Select(a => a.Id), ct);
        });

        foreach (var album in items)
            Albums.Remove(album);
    }

    // ── Pin / Unpin ───────────────────────────────────────────────────────────

    public async Task SetPinnedAsync(AlbumItemViewModel vm, bool pinned, CancellationToken ct = default)
    {
        await _db.SetAlbumPinnedAsync(vm.Id, pinned, ct);
        vm.IsPinned = pinned;
    }

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

    public async Task MoveAlbumPhotosAsync(
        AlbumItemViewModel album,
        string targetDir,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetDir)) return;

        await RunWithScanPausedAsync(async () =>
        {
            Directory.CreateDirectory(targetDir);
            var targetAlbumId = await _db.GetOrCreateDirectoryAlbumAsync(targetDir, ct);
            var photos = await _db.GetPhotosByAlbumAsync(album.Id, ct);

            foreach (var photo in photos)
            {
                ct.ThrowIfCancellationRequested();
                var targetPath = GetTargetPath(targetDir, photo.FileName);
                await Task.Run(() => File.Move(photo.FilePath, targetPath), ct);
                photo.FilePath = targetPath;
                photo.FileName = Path.GetFileName(targetPath);
                photo.AlbumId = targetAlbumId;
                photo.ModifiedAt = File.GetLastWriteTimeUtc(targetPath).ToString("O");
                await _db.UpdatePhotoAsync(photo, ct);
            }

            await _db.DeleteAlbumAsync(album.Id, ct);
        });

        await LoadAsync(ct);
    }

    public async Task CopyAlbumPhotosAsync(
        AlbumItemViewModel album,
        string targetDir,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetDir)) return;

        await RunWithScanPausedAsync(async () =>
        {
            Directory.CreateDirectory(targetDir);
            var targetAlbumId = await _db.GetOrCreateDirectoryAlbumAsync(targetDir, ct);
            var photos = await _db.GetPhotosByAlbumAsync(album.Id, ct);

            foreach (var photo in photos)
            {
                ct.ThrowIfCancellationRequested();
                var targetPath = GetTargetPath(targetDir, photo.FileName);
                await Task.Run(() => File.Copy(photo.FilePath, targetPath), ct);

                var copied = new Photo
                {
                    FilePath = targetPath,
                    FileName = Path.GetFileName(targetPath),
                    FileSize = new FileInfo(targetPath).Length,
                    Width = photo.Width,
                    Height = photo.Height,
                    TakenAt = photo.TakenAt,
                    CreatedAt = DateTime.UtcNow.ToString("O"),
                    ModifiedAt = File.GetLastWriteTimeUtc(targetPath).ToString("O"),
                    Latitude = photo.Latitude,
                    Longitude = photo.Longitude,
                    CameraMake = photo.CameraMake,
                    CameraModel = photo.CameraModel,
                    Orientation = photo.Orientation,
                    AlbumId = targetAlbumId,
                    IsPinned = photo.IsPinned,
                };

                await _db.InsertPhotoAsync(copied, ct);
            }
        });

        await LoadAsync(ct);
    }

    public void OpenAlbumInExplorer(AlbumItemViewModel vm)
    {
        if (string.IsNullOrWhiteSpace(vm.DirectoryPath) || !Directory.Exists(vm.DirectoryPath))
            return;

        try
        {
            WindowsApiHelper.SHParseDisplayName(vm.DirectoryPath, IntPtr.Zero, out var pidlFolder, 0, out _);
            if (pidlFolder == IntPtr.Zero) return;

            try
            {
                int hResult = WindowsApiHelper.SHOpenFolderAndSelectItems(pidlFolder, 0, [], 0);
                if (hResult != 0)
                    _logger.LogWarning("SHOpenFolderAndSelectItems failed with HRESULT: {HResult:X8}", hResult);
            }
            finally
            {
                WindowsApiHelper.CoTaskMemFree(pidlFolder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open album directory {Directory}", vm.DirectoryPath);
        }
    }

    public async Task AddScanDirectoriesAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0) return;

        var settings = await _db.LoadSettingsAsync(ct);
        bool added = false;
        foreach (var path in normalized)
        {
            if (settings.ScanDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))
                continue;

            settings.ScanDirectories.Add(path);
            added = true;
        }

        if (!added) return;

        await _db.SaveSettingsAsync(settings, ct);
        await _scan.StartAsync(settings, DispatcherQueue.GetForCurrentThread());
    }

    [RelayCommand]
    private void ToggleMultiSelectMode() => IsMultiSelectMode = !IsMultiSelectMode;

    // ── Card width changed ────────────────────────────────────────────────────

    partial void OnAlbumCardWidthChanged(int oldValue, int newValue)
    {
        if (!_loadingSort) SaveCardWidthSettings();
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanZoomOut));
    }

    public bool CanZoomIn  => AlbumCardWidth < CardWidthSteps[^1];
    public bool CanZoomOut => AlbumCardWidth > CardWidthSteps[0];

    // ── Zoom ──────────────────────────────────────────────────────────────────

    [RelayCommand]
    public void ZoomIn()
    {
        int idx = CurrentStepIndex();
        if (idx < CardWidthSteps.Length - 1)
            AlbumCardWidth = CardWidthSteps[idx + 1];
    }

    [RelayCommand]
    public void ZoomOut()
    {
        int idx = CurrentStepIndex();
        if (idx > 0)
            AlbumCardWidth = CardWidthSteps[idx - 1];
    }

    /// <summary>Steps in the direction of <paramref name="delta"/> (sign only).</summary>
    public void AdjustCardWidth(int delta)
    {
        if (delta > 0) ZoomIn();
        else if (delta < 0) ZoomOut();
    }

    private int CurrentStepIndex()
    {
        // Find the index of the closest step to the current width.
        int idx = Array.BinarySearch(CardWidthSteps, AlbumCardWidth);
        if (idx < 0) idx = Math.Clamp(~idx, 0, CardWidthSteps.Length - 1);
        return idx;
    }

    private static int SnapToStep(int value)
    {
        // Clamp to the nearest defined step.
        int idx = Array.BinarySearch(CardWidthSteps, value);
        if (idx >= 0) return value;
        idx = ~idx;
        if (idx >= CardWidthSteps.Length) return CardWidthSteps[^1];
        if (idx == 0) return CardWidthSteps[0];
        // Pick whichever neighbour is closer.
        return value - CardWidthSteps[idx - 1] <= CardWidthSteps[idx] - value
            ? CardWidthSteps[idx - 1]
            : CardWidthSteps[idx];
    }

    private async Task RunWithScanPausedAsync(Func<Task> action)
    {
        await _scan.StopAsync();
        try
        {
            await action();
        }
        finally
        {
            var settings = await _db.LoadSettingsAsync();
            await _scan.StartAsync(settings, DispatcherQueue.GetForCurrentThread());
        }
    }

    private static string GetTargetPath(string targetDir, string fileName)
    {
        var candidate = Path.Combine(targetDir, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        int index = 1;
        do
        {
            candidate = Path.Combine(targetDir, $"{stem} ({index++}){ext}");
        }
        while (File.Exists(candidate));

        return candidate;
    }
}
