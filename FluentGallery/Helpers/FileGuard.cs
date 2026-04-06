namespace FluentGallery.Helpers;

/// <summary>
/// Centralises all file-deletion operations so that a stale path or a bug can
/// never accidentally delete a user's photo or document.
///
/// Rules enforced here:
/// <list type="bullet">
///   <item><see cref="DeleteAppDataFile"/> — target must be inside
///         <see cref="AppDataPaths.RootDirectory"/>.</item>
///   <item><see cref="DeleteTempFile"/> — target must be inside
///         <see cref="Path.GetTempPath"/>.</item>
///   <item><see cref="DeleteRecycleBinMetadata"/> — target must be inside a
///         <c>$Recycle.Bin</c> directory (used when restoring a file cleans up
///         the <c>$I</c> metadata entry).</item>
/// </list>
///
/// <see cref="MoveToRecycleBinAsync"/> in <see cref="RecycleBinHelper"/> is the
/// correct path for any user-file deletion — it is intentionally unrestricted
/// (the user explicitly requested the delete) and does not go through this class.
/// </summary>
public static class FileGuard
{
    /// <summary>
    /// Deletes a file that must reside inside the application's data directory
    /// (<see cref="AppDataPaths.RootDirectory"/>).
    /// Throws <see cref="InvalidOperationException"/> if the resolved path
    /// escapes the app-data root (path traversal guard).
    /// </summary>
    public static void DeleteAppDataFile(string path)
    {
        AssertUnder(path, AppDataPaths.RootDirectory, "app data");
        File.Delete(path);
    }

    /// <summary>
    /// Deletes a file that must reside inside the system temp directory
    /// (<see cref="Path.GetTempPath"/>). Used for short-lived work files created
    /// by the app (e.g. EXIF write-back scratch files).
    /// </summary>
    public static void DeleteTempFile(string path)
    {
        AssertUnder(path, Path.GetTempPath(), "temp");
        File.Delete(path);
    }

    /// <summary>
    /// Deletes a <c>$I*</c> metadata file from the Windows Recycle Bin.
    /// The target must reside inside a <c>$Recycle.Bin</c> directory.
    /// Used during file-restore to remove the stale metadata entry.
    /// </summary>
    public static void DeleteRecycleBinMetadata(string path)
    {
        var full = Path.GetFullPath(path);
        const string marker = @"\$Recycle.Bin\";
        if (full.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException(
                $"FileGuard: refusing to delete '{full}' — not inside $Recycle.Bin.");

        File.Delete(path);
    }

    // ── Internal helper ───────────────────────────────────────────────────────

    private static void AssertUnder(string path, string root, string label)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"FileGuard: refusing to delete '{fullPath}' — not inside {label} directory '{fullRoot}'.");
    }
}
