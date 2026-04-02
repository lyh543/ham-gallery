using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentGallery.Data;
using FluentGallery.Models;
using System.Collections.ObjectModel;

namespace FluentGallery.ViewModels;

public enum AlbumSortField { Name, CreatedAt, ModifiedAt, PhotoCount }
public enum SortDirection  { Ascending, Descending }

/// <summary>ViewModel for the album grid page (5.2).</summary>
public sealed partial class AlbumListViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseService _db;
    private readonly ScanService     _scan;

    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();

    [ObservableProperty] public partial bool           IsLoading     { get; set; }
    [ObservableProperty] public partial bool           IsLargeView   { get; set; }
    [ObservableProperty] public partial AlbumSortField SortField     { get; set; }
    [ObservableProperty] public partial SortDirection  SortDirection { get; set; }

    public AlbumListViewModel(DatabaseService db, ScanService scan)
    {
        _db           = db;
        _scan         = scan;
        IsLargeView   = true;
        SortField     = AlbumSortField.Name;
        SortDirection = SortDirection.Ascending;

        // Subscribe to scan events (fired on UI thread via DispatcherQueue)
        _scan.PhotosBatchDiscovered += OnPhotosBatchDiscovered;
        _scan.PhotosBatchUpdated    += OnPhotosBatchUpdated;
    }

    // ── Scan event handlers ────────────────────────────────────────────────

    /// <summary>
    /// Called on the UI thread when a batch of new photos is inserted during a scan.
    /// Increments photo counts on existing album VMs or adds new albums from the DB.
    /// </summary>
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
                // New album created during this scan — load from DB and insert
                var album = await _db.GetAlbumAsync(group.Key);
                if (album is not null)
                {
                    album.PhotoCount = group.Count();
                    InsertSorted(new AlbumItemViewModel(album));
                }
            }
        }
    }

    /// <summary>
    /// Called when existing photos are updated (metadata changed, same album).
    /// Photo count does not change; no action needed for the album list.
    /// </summary>
    private void OnPhotosBatchUpdated(IReadOnlyList<Photo> photos) { /* count unchanged */ }

    // ── Insert helper ──────────────────────────────────────────────────────

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

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        _scan.PhotosBatchDiscovered -= OnPhotosBatchDiscovered;
        _scan.PhotosBatchUpdated    -= OnPhotosBatchUpdated;
    }

    // ── Load ───────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var raw = await _db.GetAlbumsAsync(ct);
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

    // ── Sort ───────────────────────────────────────────────────────────────

    partial void OnSortFieldChanged(AlbumSortField oldValue, AlbumSortField newValue)       => ReSortInPlace();
    partial void OnSortDirectionChanged(SortDirection oldValue, SortDirection newValue) => ReSortInPlace();

    private void ReSortInPlace()
    {
        var sorted = ApplySort(Albums.Select(vm => new Album
        {
            Id         = vm.Id,
            Name       = vm.Name,
            CreatedAt  = vm.ModifiedAt, // best-effort; real CreatedAt not cached in vm
            ModifiedAt = vm.ModifiedAt,
            PhotoCount = vm.PhotoCount,
            IsPinned   = vm.IsPinned,
        })).ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var target = sorted[i];
            var current = Albums.First(vm => vm.Id == target.Id);
            int from = Albums.IndexOf(current);
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

    // ── Create ────────────────────────────────────────────────────────────

    public async Task<AlbumItemViewModel> CreateAlbumAsync(string name, CancellationToken ct = default)
    {
        var album = new Album { Name = name.Trim() };
        var id = await _db.InsertAlbumAsync(album, ct);
        album.Id = id;

        var vm = new AlbumItemViewModel(album);
        // Insert at alphabetically correct position when sorting by name
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

    // ── Rename ────────────────────────────────────────────────────────────

    public async Task RenameAlbumAsync(AlbumItemViewModel vm, string newName, CancellationToken ct = default)
    {
        var album = await _db.GetAlbumAsync(vm.Id, ct);
        if (album is null) return;
        album.Name = newName.Trim();
        await _db.UpdateAlbumAsync(album, ct);
        vm.Name = album.Name;
    }

    // ── Delete ────────────────────────────────────────────────────────────

    public async Task DeleteAlbumAsync(AlbumItemViewModel vm, CancellationToken ct = default)
    {
        await _db.DeleteAlbumAsync(vm.Id, ct);
        Albums.Remove(vm);
    }

    // ── Pin / Unpin ───────────────────────────────────────────────────────

    public async Task SetPinnedAsync(AlbumItemViewModel vm, bool pinned, CancellationToken ct = default)
    {
        await _db.SetAlbumPinnedAsync(vm.Id, pinned, ct);
        vm.IsPinned = pinned;
    }

    // ── View toggle ───────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleView() => IsLargeView = !IsLargeView;
}
