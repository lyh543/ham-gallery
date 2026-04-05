using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Models;
using Microsoft.UI.Xaml;
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

    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();

    [ObservableProperty] public partial bool           IsLoading      { get; set; }
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

    public AlbumListViewModel(DatabaseService db, ScanService scan, ThumbnailService thumbnails)
    {
        _db         = db;
        _scan       = scan;
        _thumbnails = thumbnails;
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

            Albums.Clear();
            foreach (var a in sorted)
                Albums.Add(new AlbumItemViewModel(a));
        }
        finally
        {
            IsLoading = false;
        }
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
        var album = await _db.GetAlbumAsync(vm.Id, ct);
        if (album is null) return;
        album.Name = newName.Trim();
        await _db.UpdateAlbumAsync(album, ct);
        vm.Name = album.Name;
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteAlbumAsync(AlbumItemViewModel vm, CancellationToken ct = default)
    {
        await _db.DeleteAlbumAsync(vm.Id, ct);
        Albums.Remove(vm);
    }

    // ── Pin / Unpin ───────────────────────────────────────────────────────────

    public async Task SetPinnedAsync(AlbumItemViewModel vm, bool pinned, CancellationToken ct = default)
    {
        await _db.SetAlbumPinnedAsync(vm.Id, pinned, ct);
        vm.IsPinned = pinned;
    }

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
}
