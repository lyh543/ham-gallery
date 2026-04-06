using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FluentGallery.Helpers;

/// <summary>
/// Lightweight threading invariant checker for service-layer methods.
///
/// Convention:
///   - Services that perform I/O or CPU-heavy work call <see cref="EnsureBackground"/> at entry.
///   - Code that must run on the UI thread calls <see cref="EnsureUiThread"/>.
///
/// In DEBUG builds, violations throw <see cref="InvalidOperationException"/> immediately
/// so they surface in testing rather than as hard-to-reproduce jank in production.
/// In Release builds all checks compile away to nothing (zero overhead).
/// </summary>
public static class ThreadGuard
{
    /// <summary>
    /// Asserts that the calling method is NOT running on the UI thread.
    /// Use at the entry of any method that does file I/O, database access,
    /// image decoding, or other CPU-bound work.
    /// </summary>
    [Conditional("DEBUG")]
    public static void EnsureBackground([CallerMemberName] string caller = "")
    {
        if (IsOnUiThread())
            throw new InvalidOperationException(
                $"[ThreadGuard] '{caller}' must not run on the UI thread. " +
                $"Wrap the call site in Task.Run() or use ConfigureAwait(false) " +
                $"on all awaits in the calling chain.");
    }

    /// <summary>
    /// Asserts that the calling method IS running on the UI thread.
    /// Use to guard methods that access DependencyObjects or DispatcherQueue.
    /// </summary>
    [Conditional("DEBUG")]
    public static void EnsureUiThread([CallerMemberName] string caller = "")
    {
        if (!IsOnUiThread())
            throw new InvalidOperationException(
                $"[ThreadGuard] '{caller}' must run on the UI thread.");
    }

    /// <summary>
    /// Returns true when the calling thread has a WinUI DispatcherQueue (i.e. is the UI thread).
    /// Returns false both when on a background thread AND when the Windows App SDK COM server
    /// is not initialized (e.g. unit test processes), treating both as non-UI-thread.
    /// </summary>
    private static bool IsOnUiThread()
    {
        try   { return DispatcherQueue.GetForCurrentThread() is not null; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }
}
