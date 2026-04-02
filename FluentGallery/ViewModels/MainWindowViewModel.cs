using CommunityToolkit.Mvvm.ComponentModel;
using FluentGallery.Data;
using FluentGallery.Models;
using System.Collections.ObjectModel;

namespace FluentGallery.ViewModels;

/// <summary>
/// ViewModel for <see cref="FluentGallery.MainWindow"/>.
/// Owns the live collection of pinned albums that drives the dynamic nav items.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DatabaseService _db;

    /// <summary>Pinned albums in sidebar order (SortOrder ASC, then Name ASC).</summary>
    public ObservableCollection<Album> PinnedAlbums { get; } = new();

    public MainWindowViewModel(DatabaseService db) => _db = db;

    /// <summary>
    /// Reloads pinned albums from the database and refreshes <see cref="PinnedAlbums"/>.
    /// Safe to call multiple times; previous items are replaced atomically.
    /// </summary>
    public async Task LoadPinnedAlbumsAsync(CancellationToken ct = default)
    {
        var pinned = await _db.GetPinnedAlbumsAsync(ct);
        PinnedAlbums.Clear();
        foreach (var album in pinned)
            PinnedAlbums.Add(album);
    }

    /// <summary>
    /// Unpins the album in the database and removes it from <see cref="PinnedAlbums"/>.
    /// </summary>
    public async Task UnpinAlbumAsync(long albumId, CancellationToken ct = default)
    {
        await _db.SetAlbumPinnedAsync(albumId, false, ct);
        var item = PinnedAlbums.FirstOrDefault(a => a.Id == albumId);
        if (item is not null)
            PinnedAlbums.Remove(item);
    }
}
