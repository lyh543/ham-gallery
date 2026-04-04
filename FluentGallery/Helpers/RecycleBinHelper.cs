using System.Security.Principal;
using Windows.Storage;

namespace FluentGallery.Helpers;

/// <summary>
/// Moves files to the Windows Recycle Bin and supports restoring them back
/// to their original location for the in-app Undo feature.
///
/// Deletion uses <see cref="StorageDeleteOption.Default"/> so the file always
/// ends up in the drive's <c>$Recycle.Bin</c> folder as expected.
///
/// Restoration reads the <c>$I*.ext</c> metadata files that Windows writes
/// to <c>{Drive}:\$Recycle.Bin\{UserSID}\</c> and moves the corresponding
/// <c>$R*.ext</c> content file back to the original path.
/// </summary>
public static class RecycleBinHelper
{
    // ── Deletion ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves <paramref name="filePath"/> to the Windows Recycle Bin.
    /// </summary>
    /// <returns>
    /// <c>true</c> on success or when the file is already missing;
    /// <c>false</c> if the move failed.
    /// </returns>
    public static async Task<bool> MoveToRecycleBinAsync(string filePath)
    {
        if (!File.Exists(filePath)) return true;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            await file.DeleteAsync(StorageDeleteOption.Default);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Restoration ──────────────────────────────────────────────────────────

    /// <summary>
    /// Locates the most-recently-deleted Recycle Bin item whose original path
    /// matches <paramref name="originalPath"/> and moves it back.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the file was successfully restored to its original location;
    /// <c>false</c> if the item could not be found or the restore failed.
    /// </returns>
    public static async Task<bool> RestoreFromRecycleBinAsync(string originalPath)
    {
        try
        {
            var entry = await Task.Run(() => FindBinEntry(originalPath)).ConfigureAwait(false);
            if (entry is null) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            await Task.Run(() =>
            {
                File.Move(entry.RFilePath, originalPath);
                // Remove the $I metadata file so Windows doesn't show a ghost entry
                if (File.Exists(entry.IFilePath))
                    File.Delete(entry.IFilePath);
            }).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private sealed record BinEntry(string IFilePath, string RFilePath);

    /// <summary>
    /// Scans <c>{drive}:\$Recycle.Bin\{SID}\$I*{ext}</c> files to find the
    /// most recent Recycle Bin entry whose stored original path matches
    /// <paramref name="originalPath"/>.
    /// Returns <c>null</c> if not found or if the folder is inaccessible.
    /// </summary>
    private static BinEntry? FindBinEntry(string originalPath)
    {
        var drive = Path.GetPathRoot(originalPath);
        if (string.IsNullOrEmpty(drive)) return null;

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) return null;

        var binFolder = Path.Combine(drive, "$Recycle.Bin", sid);
        if (!Directory.Exists(binFolder)) return null;

        var ext = Path.GetExtension(originalPath);

        IEnumerable<string> candidates;
        try
        {
            // Match by extension for efficiency; "$I" prefix marks metadata files
            candidates = Directory.GetFiles(binFolder, $"$I*{ext}");
        }
        catch
        {
            return null;
        }

        BinEntry?  best     = null;
        DateTime   bestTime = DateTime.MinValue;

        foreach (var iFile in candidates)
        {
            try
            {
                var (storedPath, deletedAt) = ReadIFile(iFile);
                if (!string.Equals(storedPath, originalPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Derive the $R (content) file name from the $I (metadata) file name
                var iName = Path.GetFileName(iFile);          // e.g. "$IABC123.jpg"
                var rName = "$R" + iName.AsSpan(2).ToString();// → "$RABC123.jpg"
                var rFile = Path.Combine(binFolder, rName);

                if (!File.Exists(rFile)) continue;

                // If the same original path was deleted multiple times, pick the most recent
                if (best is null || deletedAt > bestTime)
                {
                    best     = new BinEntry(iFile, rFile);
                    bestTime = deletedAt;
                }
            }
            catch { /* skip unreadable or malformed $I files */ }
        }

        return best;
    }

    /// <summary>
    /// Parses a Windows Recycle Bin <c>$I</c> metadata file and returns the
    /// original file path and deletion timestamp.
    ///
    /// Format (both versions share the same 24-byte header):
    /// <list type="bullet">
    ///   <item>Offset 0  — int64  : version (1 = Vista/Win7, 2 = Win8+)</item>
    ///   <item>Offset 8  — int64  : original file size (unused here)</item>
    ///   <item>Offset 16 — int64  : deletion time (FILETIME)</item>
    ///   <item>Offset 24 — int32  : (v2 only) path length in UTF-16 chars</item>
    ///   <item>Offset 28 — UTF-16 : original path</item>
    /// </list>
    /// Version 1 stores the path as 520 bytes (260 UTF-16 chars, null-padded)
    /// starting at offset 24.
    /// </summary>
    private static (string Path, DateTime DeletedAt) ReadIFile(string iFilePath)
    {
        using var fs = File.OpenRead(iFilePath);
        using var br = new BinaryReader(fs, System.Text.Encoding.Unicode, leaveOpen: false);

        if (fs.Length < 24) throw new InvalidDataException("$I file too small");

        long version    = br.ReadInt64();
        _               = br.ReadInt64();   // file size — skip
        long fileTimeRaw = br.ReadInt64();
        var  deletedAt  = DateTime.FromFileTimeUtc(fileTimeRaw);

        string path;
        if (version == 2)
        {
            if (fs.Length < 28) throw new InvalidDataException("$I v2 truncated");
            int charCount = br.ReadInt32();
            if (charCount is <= 0 or > 32_767) throw new InvalidDataException("Invalid path length");
            var bytes = br.ReadBytes(charCount * 2);
            path = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        else
        {
            // Version 1: fixed 520-byte path field (260 UTF-16 chars, null-padded)
            var bytes = br.ReadBytes(520);
            path = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }

        return (path, deletedAt);
    }
}
