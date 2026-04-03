using CommunityToolkit.Mvvm.ComponentModel;
using FluentGallery.Data;
using FluentGallery.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace FluentGallery.ViewModels;

/// <summary>
/// Per-item ViewModel that wraps an <see cref="Album"/> for display in the album grid.
/// Supports inline rename (IsEditing flag), observable pin state, and lazy cover thumbnail.
/// </summary>
public sealed partial class AlbumItemViewModel : ObservableObject
{
    public long Id { get; }

    [ObservableProperty] public partial string      Name                { get; set; }
    [ObservableProperty] public partial int         PhotoCount          { get; set; }
    [ObservableProperty] public partial string      ModifiedAt          { get; set; }
    [ObservableProperty] public partial bool        IsPinned            { get; set; }
    [ObservableProperty] public partial bool        IsEditing           { get; set; }
    [ObservableProperty] public partial string      EditName            { get; set; }
    [ObservableProperty] public partial BitmapImage? CoverThumbnailSource { get; set; }
    [ObservableProperty] public partial bool        IsCoverLoading      { get; set; }

    public AlbumItemViewModel(Album album)
    {
        Id         = album.Id;
        Name       = album.Name;
        PhotoCount = album.PhotoCount;
        ModifiedAt = album.ModifiedAt;
        IsPinned   = album.IsPinned;
        EditName   = string.Empty;
    }

    public void BeginEdit()
    {
        EditName  = Name;
        IsEditing = true;
    }

    public string CommitEdit()
    {
        IsEditing = false;
        Name      = EditName.Trim();
        return Name;
    }

    public void CancelEdit() => IsEditing = false;

    // ── Cover thumbnail ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads the cover thumbnail from the most recently added photo in this album.
    /// Must be called on the UI thread (required by <see cref="BitmapImage.SetSourceAsync"/>).
    /// Pass <paramref name="forceRefresh"/>=<c>true</c> during periodic scan refreshes to
    /// reload even when a cover is already displayed.
    /// </summary>
    public async Task LoadCoverAsync(
        DatabaseService   db,
        ThumbnailService  thumbnails,
        bool              forceRefresh = false,
        CancellationToken ct           = default)
    {
        if (IsCoverLoading) return;
        if (!forceRefresh && CoverThumbnailSource is not null) return;

        IsCoverLoading = true;
        try
        {
            var photo = await db.GetLatestPhotoByAlbumAsync(Id, ct);
            if (photo is null) { CoverThumbnailSource = null; return; }

            var path = await thumbnails.GetOrCreateThumbnailAsync(photo, ct);
            if (path is null || !File.Exists(path)) { CoverThumbnailSource = null; return; }

            await using var stream = File.OpenRead(path);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream.AsRandomAccessStream());
            CoverThumbnailSource = bmp;
        }
        catch (OperationCanceledException) { throw; }
        catch { CoverThumbnailSource = null; }
        finally { IsCoverLoading = false; }
    }

    /// <summary>Releases the loaded cover image (called when a container is recycled).</summary>
    public void ClearCover() => CoverThumbnailSource = null;
}
