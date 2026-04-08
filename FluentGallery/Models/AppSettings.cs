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

    /// <summary>Use Acrylic system backdrop instead of Mica.</summary>
    public bool UseAcrylicBackdrop { get; set; } = false;

    // ── Behaviour ───────────────────────────────────────────────────────────

    /// <summary>Show a confirmation dialog before deleting a photo.</summary>
    public bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>Number of photos before the current one to pre-load in the detail view (0–5).</summary>
    public int PreloadCountBack { get; set; } = 2;

    /// <summary>Number of photos after the current one to pre-load in the detail view (0–10).</summary>
    public int PreloadCountForward { get; set; } = 5;

    /// <summary>Maximum in-memory image cache size in bytes. Default 512 MB.</summary>
    public long MemoryCacheLimitBytes { get; set; } = 512L * 1024 * 1024;

    // ── Album list sort ──────────────────────────────────────────────────────

    /// <summary>
    /// Sort field for the album grid list.
    /// Stores the integer value of <c>AlbumSortField</c> enum. Default 4 = TakenAt.
    /// </summary>
    public int AlbumSortField { get; set; } = 4;

    /// <summary>
    /// Sort direction for the album grid list.
    /// Stores the integer value of <c>SortDirection</c> enum. Default 1 = Descending.
    /// </summary>
    public int AlbumSortDirection { get; set; } = 1;

    // ── Window ──────────────────────────────────────────────────────────────

    /// <summary>Whether the window was maximized when last closed.</summary>
    public bool WindowMaximized { get; set; } = false;

    /// <summary>
    /// Window geometry as fractions of the monitor that contained the window.
    /// All four values are 0 when no geometry has been saved yet (use defaults).
    /// </summary>
    public double WindowWidthRatio  { get; set; } = 0;
    public double WindowHeightRatio { get; set; } = 0;
    public double WindowLeftRatio   { get; set; } = 0;
    public double WindowTopRatio    { get; set; } = 0;

    // ── System integration ────────────────────────────────────────────────────

    /// <summary>
    /// Whether to register Fluent Gallery as the default handler for supported
    /// image file extensions in HKCU (no elevation required).
    /// </summary>
    public bool RegisterFileAssociations { get; set; } = false;

    // ── Thumbnail ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fit-inside box size (px) used when generating thumbnails.
    /// Must be one of: 128, 256, 384, 512, 768, 1024, 1536, 2048.
    /// Default 512.
    /// </summary>
    public int ThumbnailSize { get; set; } = 512;

    // ── Card display widths ───────────────────────────────────────────────────

    /// <summary>Width in pixels of album cards in the album list. Default 170 (index 5 of album steps).</summary>
    public int AlbumCardWidth { get; set; } = 170;

    /// <summary>Width in pixels of photo cards in the photo list. Default 165 (index 7 of photo steps).</summary>
    public int PhotoCardWidth { get; set; } = 165;

    // ── Photo detail ──────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the filmstrip is pinned (always visible) in the photo detail view.
    /// When false the filmstrip auto-hides after 3 s of inactivity (legacy behaviour).
    /// </summary>
    public bool FilmStripPinned { get; set; } = false;

    // ── Debug ─────────────────────────────────────────────────────────────────

    /// <summary>Show a size toast when the album/photo card width changes.</summary>
    public bool ShowCardSizeToast { get; set; } = false;

    /// <summary>Show preload state badges on filmstrip thumbnails in the photo detail view.</summary>
    public bool ShowPreloadStatus { get; set; } = false;
}
