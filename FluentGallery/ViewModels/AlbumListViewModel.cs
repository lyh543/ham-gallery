using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Models;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;

namespace FluentGallery.ViewModels;

public enum AlbumSortField { Name, CreatedAt, ModifiedAt, PhotoCount }
public enum SortDirection  { Ascending, Descending }

/// <summary>ViewModel for the album grid page.</summary>
public sealed partial class AlbumListViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseService  _db;
    private readonly ScanService      _scan;
    private readonly ThumbnailService _thumbnails;

    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();

    [ObservableProperty] public partial bool           IsLoading     { get; set; }
    [ObservableProperty] public partial bool           IsLargeView   { get; set; }
    [ObservableProperty] public partial AlbumSortField SortField     { get; set; }
    [ObservableProperty] public partial SortDirection  SortDirection { get; set; }

    // ── Cover-refresh throttle (200 ms timer, UI-thread only) ─────────────────
    //
    // During a background scan, PhotosBatchDiscovered may fire many times per second.
    // Instead of refreshing every album's cover on each event, we collect the affected
    // album IDs and let a DispatcherTimer coalesce updates into 200 ms bursts.

    private DispatcherTimer?                _coverTimer;        // lazily created on first scan event
    private readonly HashSet<long>          _pendingCoverIds  = new();
    private bool                            _isCoverRefreshing;
    private CancellationTokenSource         _pageCts          = new();

    public AlbumListViewModel(DatabaseService db, ScanService scan, ThumbnailService thumbnails)
    {
        _db         = db;
        _scan       = scan;
        _thumbnails = thumbnails;
        IsLargeView   = true;
        SortField     = AlbumSortField.Name;
        SortDirection = SortDirection.Ascending;

        _scan.PhotosBatchDiscovered += OnPhotosBatchDiscovered;
        _scan.PhotosBatchUpdated    += OnPhotosBatchUpdated;
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

    // ── Sort ───────────────────────────────────────────────────────────────────

    partial void OnSortFieldChanged(AlbumSortField oldValue, AlbumSortField newValue)       => ReSortInPlace();
    partial void OnSortDirectionChanged(SortDirection oldValue, SortDirection newValue) => ReSortInPlace();

    private void ReSortInPlace()
    {
        var sorted = ApplySort(Albums.Select(vm => new Album
        {
            Id         = vm.Id,
            Name       = vm.Name,
            CreatedAt  = vm.ModifiedAt,
            ModifiedAt = vm.ModifiedAt,
            PhotoCount = vm.PhotoCount,
            IsPinned   = vm.IsPinned,
        })).ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var target  = sorted[i];
            var current = Albums.First(vm => vm.Id == target.Id);
            int from    = Albums.IndexOf(current);
            if (from != i) Albums.Move(from, i);
        }
    }

    private IEnumerable<Album> ApplySort(IEnumerable<Album> source)
    {
        IOrderedEnumerable<Album> ordered = SortField switch
        {
            AlbumSortField.CreatedAt  => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => a.CreatedAt)
                : source.OrderByDescending(a => a.CreatedAt),
            AlbumSortField.ModifiedAt => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => a.ModifiedAt)
                : source.OrderByDescending(a => a.ModifiedAt),
            AlbumSortField.PhotoCount => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => a.PhotoCount)
                : source.OrderByDescending(a => a.PhotoCount),
            _ => SortDirection == SortDirection.Ascending
                ? source.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                : source.OrderByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered;
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

    // ── View toggle ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleView() => IsLargeView = !IsLargeView;
}
