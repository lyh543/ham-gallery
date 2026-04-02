namespace FluentGallery.Helpers;

/// <summary>
/// Centralised paths for all application-managed data directories and files.
/// All paths are resolved lazily under %LocalAppData%\FluentGallery\.
/// </summary>
public static class AppDataPaths
{
    private static readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentGallery");

    /// <summary>Root directory: %LocalAppData%\FluentGallery\</summary>
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
