namespace FluentGallery.Models;

/// <summary>
/// Runtime representation of application settings.
/// Persisted via <see cref="FluentGallery.Data.DatabaseService"/> (Settings table, key/value).
/// </summary>
public sealed class AppSettings
{
    // ── Scan ────────────────────────────────────────────────────────────────

    /// <summary>Directories to scan for photos.</summary>
    public List<string> ScanDirectories { get; set; } = [];

    /// <summary>Whether to recurse into subdirectories during scanning.</summary>
    public bool RecursiveScan { get; set; } = true;

    /// <summary>Directory paths to exclude from scanning (relative patterns or absolute paths).</summary>
    public List<string> ExcludeDirectories { get; set; } = [];

    // ── Appearance ──────────────────────────────────────────────────────────

    /// <summary>
    /// BCP-47 language tag, e.g. "en-US" or "zh-CN".
    /// Empty string means follow the system locale.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>0 = System, 1 = Light, 2 = Dark (maps to ElementTheme).</summary>
    public int Theme { get; set; } = 0;

    // ── Behaviour ───────────────────────────────────────────────────────────

    /// <summary>Show a confirmation dialog before deleting a photo.</summary>
    public bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>Number of adjacent photos to pre-load in the detail view (1–10).</summary>
    public int PreloadCount { get; set; } = 5;

    /// <summary>Maximum in-memory image cache size in bytes. Default 512 MB.</summary>
    public long MemoryCacheLimitBytes { get; set; } = 512L * 1024 * 1024;

    // ── Thumbnail ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fit-inside box size (px) used when generating thumbnails.
    /// Must be one of: 128, 256, 384, 512, 768, 1024, 1536, 2048.
    /// Default 512.
    /// </summary>
    public int ThumbnailSize { get; set; } = 512;
}
