using Microsoft.Win32;
using System.Diagnostics;

namespace FluentGallery.Helpers;

/// <summary>
/// Registers or unregisters Windows file-type associations for the running
/// (unpackaged) executable via HKCU so no elevation is required.
/// </summary>
public static class FileAssociationHelper
{
    // ProgID used in HKCU\Software\Classes
    private const string ProgId = "FluentGallery.AssocFile";

    private static readonly string[] SupportedExtensions =
    [
        ".jpg", ".jpeg", ".png", ".bmp",
        ".gif", ".webp", ".heic", ".heif",
        ".tif", ".tiff",
    ];

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when every supported extension already points to
    /// <see cref="ProgId"/> in HKCU.
    /// </summary>
    public static bool AreAssociationsRegistered()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
        if (classes is null) return false;

        foreach (var ext in SupportedExtensions)
        {
            using var extKey = classes.OpenSubKey(ext);
            if (extKey?.GetValue(null) as string != ProgId)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Writes the ProgID and all extension entries to HKCU\Software\Classes,
    /// then notifies the shell so Explorer updates immediately.
    /// </summary>
    public static void Register()
    {
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;

        using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
        {
            // ProgID → open command
            using (var progId  = classes.CreateSubKey(ProgId))
            using (var shell   = progId.CreateSubKey(@"shell\open\command"))
            {
                shell.SetValue(null, $"\"{exePath}\" \"%1\"");
            }

            // ProgID → default icon
            using (var progId  = classes.CreateSubKey(ProgId))
            using (var icon    = progId.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, $"\"{exePath}\",0");
            }

            // Extension entries
            foreach (var ext in SupportedExtensions)
            {
                using var extKey = classes.CreateSubKey(ext);
                extKey.SetValue(null, ProgId);
            }
        }

        NotifyShell();
    }

    /// <summary>
    /// Removes the ProgID and extension entries written by <see cref="Register"/>.
    /// Extensions that were already pointing elsewhere before registration are
    /// left as-is (we only remove entries whose value equals our ProgID).
    /// </summary>
    public static void Unregister()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        if (classes is null) return;

        // Remove extension entries only if they still point to our ProgID.
        foreach (var ext in SupportedExtensions)
        {
            using var extKey = classes.OpenSubKey(ext);
            if (extKey?.GetValue(null) as string == ProgId)
                classes.DeleteSubKeyTree(ext, throwOnMissingSubKey: false);
        }

        // Remove the ProgID subtree.
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);

        NotifyShell();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    private static void NotifyShell()
        // SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
        => SHChangeNotify(0x08000000, 0x0000, nint.Zero, nint.Zero);
}
