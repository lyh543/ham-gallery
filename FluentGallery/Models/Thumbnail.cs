namespace FluentGallery.Models;

/// <summary>
/// A cached thumbnail record. Stored in a separate table so the main Photos table stays lean.
/// </summary>
public sealed class Thumbnail
{
    /// <summary>FK → Photos.Id (also PK in the Thumbnails table).</summary>
    public long PhotoId { get; set; }

    /// <summary>
    /// Absolute path to the generated thumbnail on disk, or <c>null</c> when
    /// <see cref="ThumbnailDisabled"/> is <c>true</c> (no file is created).
    /// </summary>
    public string? ThumbPath { get; set; }

    /// <summary>
    /// When <c>true</c> the source format does not produce a separate thumbnail file
    /// (e.g. GIF — the original is shown directly). <see cref="ThumbPath"/> is null.
    /// </summary>
    public bool ThumbnailDisabled { get; set; }

    /// <summary>ISO 8601 timestamp when the thumbnail was generated.</summary>
    public string GeneratedAt { get; set; } = string.Empty;

    /// <summary>
    /// The source file's LastWriteTime (ISO 8601) at the time the thumbnail was generated.
    /// Compare against the current file LastWriteTime to decide whether to regenerate.
    /// </summary>
    public string SourceModifiedAt { get; set; } = string.Empty;
}
