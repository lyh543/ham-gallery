using FluentGallery.Models;
using System.Runtime.InteropServices;

namespace FluentGallery.Helpers;

/// <summary>
/// Exposes Windows Shell's natural sort order (same as Windows Explorer)
/// via StrCmpLogicalW from shlwapi.dll.
/// </summary>
public static class NaturalSortHelper
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    /// <summary>A string comparer that uses Windows Shell natural ordering.</summary>
    public static IComparer<string> NaturalComparer
        => Comparer<string>.Create((x, y) =>
               StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty));

    /// <summary>
    /// Sorts <paramref name="photos"/> by file name using Windows Shell natural ordering
    /// (numbers inside names are compared numerically, matching Explorer's default sort).
    /// </summary>
    public static IEnumerable<Photo> SortNatural(IEnumerable<Photo> photos)
        => photos.OrderBy(p => p.FileName, NaturalComparer);
}
