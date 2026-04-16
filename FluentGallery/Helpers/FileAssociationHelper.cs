using Microsoft.Win32;
using System.Collections.Generic;
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

    private static readonly string[] SupportedExtensions = new[]
    {
        ".jpg", ".jpeg", ".png", ".bmp",
        ".gif", ".webp", ".heic", ".heif",
        ".tif", ".tiff",
    };

    private static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".bmp"] = "image/bmp",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
    };

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
    /// preserving Explorer image-thumbnail behavior for associated image files,
    /// then notifies the shell so Explorer updates immediately.
    /// </summary>
    public static void Register()
    {
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;

        using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
        {
            using (var progId = classes.CreateSubKey(ProgId))
            {
                progId.SetValue(null, AppDataPaths.DisplayName);
                progId.SetValue("FriendlyTypeName", AppDataPaths.DisplayName);
                progId.SetValue("PerceivedType", "image");

                using (var shell = progId.CreateSubKey(@"shell\open\command"))
                {
                    shell.SetValue(null, $"\"{exePath}\" \"%1\"");
                }

                using (var icon = progId.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(null, $"\"{exePath}\",0");
                }
            }

            foreach (var ext in SupportedExtensions)
            {
                using var extKey = classes.CreateSubKey(ext);
                extKey.SetValue(null, ProgId);
                extKey.SetValue("PerceivedType", "image");

                if (ContentTypes.TryGetValue(ext, out var contentType))
                    extKey.SetValue("Content Type", contentType);
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

#if TEST_BUILD
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    private static void NotifyShell()
        // SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
        => SHChangeNotify(0x08000000, 0x0000, nint.Zero, nint.Zero);
#else
    private static void NotifyShell()
        // SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
        => WindowsApiHelper.NotifyShellAssociationChanged();
#endif
}
