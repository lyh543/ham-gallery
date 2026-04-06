using CommunityToolkit.Mvvm.ComponentModel;
using FluentGallery.Data;
using FluentGallery.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentGallery.ViewModels;

/// <summary>
/// Per-item ViewModel that wraps an <see cref="Album"/> for display in the album grid.
/// Supports inline rename (IsEditing flag), observable pin state, and lazy cover thumbnail.
/// </summary>
public sealed partial class AlbumItemViewModel : ObservableObject
{
    public long Id { get; }

    [ObservableProperty] public partial string       Name                { get; set; }
    [ObservableProperty] public partial int          PhotoCount          { get; set; }
    [ObservableProperty] public partial string       CreatedAt           { get; set; }
    [ObservableProperty] public partial string       ModifiedAt          { get; set; }
    [ObservableProperty] public partial bool         IsPinned            { get; set; }
    [ObservableProperty] public partial bool         IsEditing           { get; set; }
    [ObservableProperty] public partial string       EditName            { get; set; }
    [ObservableProperty] public partial BitmapImage? CoverThumbnailSource { get; set; }
    [ObservableProperty] public partial bool         IsCoverLoading      { get; set; }

    // MAX photo-timestamp aggregates (most-recent photo in this album for each field).
    public string? MaxPhotoTakenAt    { get; set; }
    public string? MaxPhotoCreatedAt  { get; set; }
    public string? MaxPhotoModifiedAt { get; set; }

    public AlbumItemViewModel(Album album)
    {
        Id                 = album.Id;
        Name               = album.Name;
        PhotoCount         = album.PhotoCount;
        CreatedAt          = album.CreatedAt;
        ModifiedAt         = album.ModifiedAt;
        IsPinned           = album.IsPinned;
        EditName           = string.Empty;
        MaxPhotoTakenAt    = album.MaxPhotoTakenAt;
        MaxPhotoCreatedAt  = album.MaxPhotoCreatedAt;
        MaxPhotoModifiedAt = album.MaxPhotoModifiedAt;
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
    /// Safe to call on the UI thread: thumbnail generation is offloaded to a thread-pool
    /// thread via <c>Task.Run</c>, and the <see cref="BitmapImage"/> is created with a
    /// URI so the OS handles the decode asynchronously in the background.
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

            // Offload to thread pool so ThumbnailService.GetOrCreateThumbnailAsync
            // (which requires a background thread) does not run on the UI thread.
            var path = await Task.Run(() => thumbnails.GetOrCreateThumbnailAsync(photo, ct), ct);

            // path is null when ThumbnailDisabled (e.g. GIF) — fall back to the source file.
            var displayPath = (path is not null && File.Exists(path)) ? path
                            : (File.Exists(photo.FilePath)            ? photo.FilePath : null);
            if (displayPath is null) { CoverThumbnailSource = null; return; }

            // UriSource triggers a background decode without blocking the UI thread,
            // unlike SetSourceAsync(File.OpenRead(...).AsRandomAccessStream()) which
            // pins the stream to the STA and causes WIC to marshal every read back.
            CoverThumbnailSource = new BitmapImage(new Uri(displayPath));
        }
        catch (OperationCanceledException) { throw; }
        catch { CoverThumbnailSource = null; }
        finally { IsCoverLoading = false; }
    }

    /// <summary>Releases the loaded cover image (called when a container is recycled).</summary>
    public void ClearCover() => CoverThumbnailSource = null;
}
