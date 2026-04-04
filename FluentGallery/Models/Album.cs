namespace FluentGallery.Models;

/// <summary>
/// Represents an album (either directory-backed or manually created).
/// </summary>
public sealed class Album
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>File path used as the album cover. Null means derive automatically.</summary>
    public string? CoverPath { get; set; }

    /// <summary>
    /// Corresponding file-system directory.
    /// Null for manually created albums that are not tied to a folder.
    /// </summary>
    public string? DirectoryPath { get; set; }

    /// <summary>ISO 8601 row-creation timestamp.</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>ISO 8601 last-modified timestamp.</summary>
    public string ModifiedAt { get; set; } = string.Empty;

    /// <summary>Whether this album is pinned to the navigation sidebar.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Display order of pinned albums in the sidebar (lower = higher).</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Sort field used when viewing photos inside this album.
    /// Stores the integer value of <c>PhotoSortField</c> enum. Default 4 = TakenAt.
    /// </summary>
    public int PhotoSortField { get; set; } = 4;

    /// <summary>
    /// Sort direction used when viewing photos inside this album.
    /// Stores the integer value of <c>SortDirection</c> enum. Default 1 = Descending.
    /// </summary>
    public int PhotoSortDirection { get; set; } = 1;

    // ── Computed / transient (not persisted) ────────────────────────────────

    /// <summary>Total number of photos in this album. Populated by queries, not stored.</summary>
    public int PhotoCount { get; set; }

    // Photo-timestamp aggregates — populated by DatabaseService, never written to DB.
    // MAX of each field across all photos in the album (= time of the most recent photo).
    // Null means the album has no photos (or no photos with that timestamp field).
    public string? MaxPhotoTakenAt    { get; set; }
    public string? MaxPhotoCreatedAt  { get; set; }
    public string? MaxPhotoModifiedAt { get; set; }
}
