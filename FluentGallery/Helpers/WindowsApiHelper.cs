using System;
using System.Runtime.InteropServices;

namespace FluentGallery.Helpers;

public static class WindowsApiHelper
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct OPENASINFO
	{
		public string pcszFile;
		public string? pcszClass;
		public uint oaifInFlags;
	}

	public const uint OAIF_ALLOW_REGISTRATION = 0x00000001;
	public const uint OAIF_REGISTER_EXT = 0x00000002;
	public const uint OAIF_EXEC = 0x00000004;
	public const uint OAIF_DEFAULT_ONLY = 0x00000020;

	[DllImport("user32.dll")]
	public static extern uint GetDpiForWindow(IntPtr hwnd);

	[DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	public static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO oainfo);

	[DllImport("shell32.dll", SetLastError = true)]
	public static extern void SHParseDisplayName(
		[MarshalAs(UnmanagedType.LPWStr)] string pszName,
		IntPtr pbc,
		out IntPtr ppidl,
		uint sfgaoIn,
		out uint psfgaoOut);

	[DllImport("shell32.dll", SetLastError = true)]
	public static extern int SHOpenFolderAndSelectItems(
		IntPtr pidlFolder,
		uint cidl,
		[In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
		int grfFlags);

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

	[DllImport("ole32.dll")]
	public static extern void CoTaskMemFree(IntPtr pv);

	public static void NotifyShellAssociationChanged()
		=> SHChangeNotify(0x08000000, 0x0000, nint.Zero, nint.Zero);
}
