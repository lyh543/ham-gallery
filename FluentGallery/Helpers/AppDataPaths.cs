namespace FluentGallery.Helpers;

/// <summary>
/// Centralised identity and path constants for the application.
/// All names and paths automatically reflect the current build environment
/// (production vs. development) so the two can run side-by-side without
/// data collisions.
/// </summary>
public static class AppDataPaths
{
    // ── Build-time identity ────────────────────────────────────────────────────

#if DEV_BUILD
    /// <summary>Folder name under %LocalAppData% (also used as the process identity).</summary>
    public const string AppFolderName = "FluentGallery-Dev";

    /// <summary>Human-readable application display name (window title, etc.).</summary>
    public const string DisplayName = "Fluent Gallery (Dev)";
#else
    /// <summary>Folder name under %LocalAppData% (also used as the process identity).</summary>
    public const string AppFolderName = "FluentGallery";

    /// <summary>Human-readable application display name (window title, etc.).</summary>
    public const string DisplayName = "Fluent Gallery";
#endif

    // ── Runtime paths ─────────────────────────────────────────────────────────

    private static readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>Root directory: %LocalAppData%\FluentGallery[-Dev]\</summary>
    public static string RootDirectory => _root;

    /// <summary>SQLite database file path.</summary>
    public static string DatabasePath => Path.Combine(_root, "gallery.db");

    /// <summary>Directory where generated thumbnail JPEG files are stored.</summary>
    public static string ThumbnailsDirectory => Path.Combine(_root, "Thumbnails");

    /// <summary>Directory for rolling application log files.</summary>
    public static string LogsDirectory => Path.Combine(_root, "logs");

    /// <summary>Directory for temporary files (e.g. pre-crop photo backups).</summary>
    public static string TempDirectory => Path.Combine(_root, "Temp");

    /// <summary>Ensures the most-used subdirectories exist on disk.</summary>
    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
