namespace FluentGallery.Services;

public sealed record ThumbnailRefreshEventArgs(long PhotoId, string FilePath, string? ThumbPath, string ModifiedAt);

public sealed class ThumbnailRefreshService
{
    public event EventHandler<ThumbnailRefreshEventArgs>? ThumbnailRefreshed;

    public void NotifyThumbnailRefreshed(long photoId, string filePath, string? thumbPath, string modifiedAt)
    {
        ThumbnailRefreshed?.Invoke(this, new ThumbnailRefreshEventArgs(photoId, filePath, thumbPath, modifiedAt));
    }
}