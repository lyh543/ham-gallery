using System.Runtime.InteropServices;

namespace FluentGallery.Helpers;

/// <summary>
/// Wraps the Win32 <c>IFileOpenDialog</c> COM API to provide multi-folder selection,
/// which WinUI 3's <see cref="Windows.Storage.Pickers.FolderPicker"/> does not support.
/// <para>
/// Internally spawns a dedicated STA thread (required by the COM file dialog).
/// </para>
/// </summary>
public static class MultiFolderPicker
{
    // IFileOpenDialog option flags
    private const uint FOS_ALLOWMULTISELECT = 0x00000200;
    private const uint FOS_PICKFOLDERS      = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM  = 0x00000040;

    // IShellItem display name format: filesystem path
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    // HRESULT returned when the user clicks Cancel
    private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

    /// <summary>
    /// Shows the multi-folder picker associated with <paramref name="hwnd"/>.
    /// </summary>
    /// <returns>
    /// The selected folder paths, or an empty list if the user cancelled or an error occurred.
    /// </returns>
    public static Task<IReadOnlyList<string>> PickAsync(IntPtr hwnd)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();

        // IFileOpenDialog requires an STA apartment thread.
        var thread = new Thread(() =>
        {
            try   { tcs.SetResult(ShowDialog(hwnd)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    // ────────────────────────────────────────────────────────────────────
    // Private implementation
    // ────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ShowDialog(IntPtr hwnd)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
        try
        {
            // Enable multi-select + folder mode
            dialog.GetOptions(out var opts);
            dialog.SetOptions(opts | FOS_ALLOWMULTISELECT | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
            dialog.SetTitle("选择目录（按住 Ctrl 可多选）");
            dialog.SetOkButtonLabel("添加");

            int hr = dialog.Show(hwnd);

            // User pressed Cancel — not an error
            if (hr == HRESULT_CANCELLED) return [];

            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResults(out var items);
            items.GetCount(out var count);

            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
            return paths;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // COM interop definitions
    // ────────────────────────────────────────────────────────────────────

    /// <summary>CoClass for FileOpenDialog (CLSID_FileOpenDialog).</summary>
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCoClass { }

    /// <summary>
    /// IFileOpenDialog (IID: D57C7288-D4AD-4768-BE02-9D969532D960).
    /// Vtable order: IModalWindow::Show, then IFileDialog methods, then GetResults/GetSelectedItems.
    /// </summary>
    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr hwnd);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IntPtr psi);
        void SetFolder(IntPtr psi);
        void GetFolder(out IntPtr ppsi);
        void GetCurrentSelection(out IntPtr ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IntPtr ppsi);
        void AddPlace(IntPtr psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    /// <summary>IShellItemArray (IID: b63ea76d-1f85-456f-a19c-48159efa858b).</summary>
    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(uint AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    /// <summary>IShellItem (IID: 43826D1E-E718-42EE-BC55-A1E261C37BFE).</summary>
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
