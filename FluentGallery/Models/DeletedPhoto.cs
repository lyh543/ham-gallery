namespace FluentGallery.Models;

/// <summary>
/// Snapshot of a deleted photo stored in the database so the deletion can be
/// undone (Ctrl+Z / Undo button). Records are automatically cleaned up after
/// one month by <see cref="FluentGallery.Data.DatabaseService.CleanupOldDeletedPhotosAsync"/>.
/// </summary>
public sealed class DeletedPhoto
{
    public long Id { get; set; }

    /// <summary>Original absolute file path — used to locate the file in the Recycle Bin.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>JSON-serialised <see cref="Photo"/> so all metadata is preserved on restore.</summary>
    public string PhotoJson { get; set; } = string.Empty;

    /// <summary>Absolute path of the generated thumbnail .jpg file, or <c>null</c> if none existed.</summary>
    public string? ThumbPath { get; set; }

    /// <summary>
    /// The <see cref="Thumbnail.SourceModifiedAt"/> value stored in the Thumbnails row.
    /// Needed to rebuild the Thumbnails row on restore without having to regenerate the thumbnail.
    /// </summary>
    public string? ThumbSourceModifiedAt { get; set; }

    /// <summary>ISO 8601 UTC timestamp of deletion — used for the one-month cleanup sweep.</summary>
    public string DeletedAt { get; set; } = string.Empty;
}
