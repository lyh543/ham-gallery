namespace FluentGallery.Models;

/// <summary>
/// Represents a single photo record stored in the database.
/// This is a plain data model — no change-notification logic here.
/// </summary>
public sealed class Photo
{
    public long Id { get; set; }

    /// <summary>Absolute file path on disk. Must be unique.</summary>
    public string FilePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>EXIF DateTimeOriginal stored as ISO 8601 string (nullable — many files have no EXIF).</summary>
    public string? TakenAt { get; set; }

    /// <summary>ISO 8601 timestamp at which the row was inserted.</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>File-system LastWriteTime in ISO 8601. Used to detect whether the file has changed.</summary>
    public string ModifiedAt { get; set; } = string.Empty;

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public string? CameraModel { get; set; }
    public string? CameraMake { get; set; }

    /// <summary>EXIF Orientation tag value (1–8).</summary>
    public int? Orientation { get; set; }

    public long? AlbumId { get; set; }

    public bool IsPinned { get; set; }
}
