using FluentGallery.Data;
using FluentGallery.Loaders;
using FluentGallery.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    private readonly Dictionary<string, CancellationTokenSource> _preloadTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PhotoThumbItem> _filmStripItemIndex =
        new(StringComparer.OrdinalIgnoreCase);
    private int _loadGeneration = 0;

    private DispatcherTimer _preloadDebounce = null!;
    private int _pendingPreloadIndex = -1;
    private bool _isInitialized = false;

    private void HandleCurrentImagePathChanged()
    {
        _ = LoadCurrentImageAsync();
        UpdateCounterText();
        TitleText.Text = ViewModel.CurrentPhoto?.FileName ?? string.Empty;

        if (_isInitialized)
        {
            _pendingPreloadIndex = ViewModel.CurrentIndex;
            _preloadDebounce.Stop();
            _preloadDebounce.Start();
        }
        else
        {
            _pendingPreloadIndex = ViewModel.CurrentIndex;
            UpdatePreloadTasks(_pendingPreloadIndex);
        }
    }

    private void UpdateCounterText()
    {
        int total = ViewModel.FilmStripItems.Count;
        CounterText.Text = total > 0
            ? $"{ViewModel.CurrentIndex + 1} / {total}"
            : string.Empty;
    }

    private async Task LoadCurrentImageAsync()
    {
        int gen  = ++_loadGeneration;
        var path = ViewModel.CurrentImagePath;
        if (string.IsNullOrEmpty(path)) return;

        bool keepSwipePreviewDuringLoad = _swipeCommitPending &&
                                          string.Equals(_swipeCommitTargetPath, path, StringComparison.OrdinalIgnoreCase) &&
                                          SwipePreviewImage.Source is not null;

        _logger.LogDebug("LoadCurrentImage: {Path}", path);
        if (!keepSwipePreviewDuringLoad)
            ZoomImage.SetLoading();

        if (_touchSwipeDragging)
        {
            _logger.LogDebug("LoadCurrentImage reset touch swipe state");
            EndTouchSwipeState();
        }
        _touchPointers.Clear();
        _touchPinching = false;
        _touchPinchStartDistance = 0;
        _touchPinchStartZoom = 1;
        if (!keepSwipePreviewDuringLoad)
            ResetSwipePreviewTransforms();
        try
        {
            Loaders.LoadedImage? loaded;
            if (keepSwipePreviewDuringLoad && TryConsumeSwipePreviewLoadedImage(path, out var previewLoaded))
            {
                loaded = previewLoaded;
            }
            else
            {
                var loader = GetLoader(path);
                loaded = await loader.LoadAsync(path, _cts.Token);
            }

            if (loaded is null)
            {
                if (keepSwipePreviewDuringLoad)
                    ResetSwipePreviewTransforms();
                else
                    CancelSwipeCommit();
                return;
            }

            if (gen != _loadGeneration)
            {
                if (loaded.Source is IDisposable d)
                    DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => { try { d.Dispose(); } catch { } });
                return;
            }

            ZoomImage.SetSource(loaded, _cts.Token, skipFadeOut: keepSwipePreviewDuringLoad);

            // Note: For seamless swipe, ResetSwipePreviewTransforms is deferred until after
            // the pending image is swapped into main (see SwapPendingToMain in ZoomableImage).
            if (keepSwipePreviewDuringLoad)
            {
                // Seamless mode: preview will be cleared by ZoomableImage after swap.
                ConsumeSwipeCommitForPath(path);
            }
            else if (ConsumeSwipeCommitForPath(path))
            {
                ResetSwipePreviewTransforms();
            }

            var loadedItem = FindFilmStripItem(path);
            if (loadedItem is not null)
                loadedItem.PreloadState = PreloadState.Loaded;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CancelSwipeCommit();
            ResetSwipePreviewTransforms();
            _logger.LogWarning(ex, "LoadCurrentImage failed: {Path}", path);
        }
    }

    private void UpdatePreloadTasks(int newIndex)
    {
        var newPaths = new HashSet<string>(
            ViewModel.GetPreloadPaths(newIndex),
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in _preloadTasks.Keys.Where(p => !newPaths.Contains(p)).ToList())
        {
            _preloadTasks[path].Cancel();
            _preloadTasks.Remove(path);

            var item = FindFilmStripItem(path);
            if (item?.PreloadState == PreloadState.Loading)
                item.PreloadState = PreloadState.NotLoaded;
        }

        foreach (var path in newPaths)
        {
            if (_preloadTasks.ContainsKey(path)) continue;

            var item = FindFilmStripItem(path);
            if (item is null) continue;
            if (item.PreloadState is PreloadState.Loaded or PreloadState.Loading) continue;

            var cts          = new CancellationTokenSource();
            var capturedPath = path;
            var captured     = item;
            _preloadTasks[path] = cts;
            item.PreloadState   = PreloadState.Loading;

            _ = GetLoader(path).PreloadAsync(path, cts.Token).ContinueWith(t =>
            {
                bool succeeded = t.IsCompletedSuccessfully;
                DispatcherQueue.TryEnqueue(() =>
                {
                    captured.PreloadState = succeeded ? PreloadState.Loaded : PreloadState.NotLoaded;
                    if (_preloadTasks.TryGetValue(capturedPath, out var current) &&
                        ReferenceEquals(current, cts))
                        _preloadTasks.Remove(capturedPath);
                    cts.Dispose();
                });
            }, TaskScheduler.Default);
        }
    }

    private PhotoThumbItem? FindFilmStripItem(string path) =>
        _filmStripItemIndex.TryGetValue(path, out var item) ? item : null;

    internal void RebuildFilmStripItemIndex()
    {
        var index = new Dictionary<string, PhotoThumbItem>(
            ViewModel.FilmStripItems.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in ViewModel.FilmStripItems)
            index.TryAdd(item.Photo.FilePath, item);
        _filmStripItemIndex = index;
    }

    private IImageLoader GetLoader(string path) =>
        _wicLoader.IsSupported(Path.GetExtension(path)) ? _wicLoader : _magickLoader;

    // ── Cleanup ──────────────────────────────────────────────────────────────

    private void CleanupLoading()
    {
        _preloadDebounce.Stop();

        var preloadTokens = _preloadTasks.Values.ToList();
        foreach (var cts in preloadTokens)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _preloadTasks.Clear();

        ZoomImage.SetLoading();

        _wicLoader.ClearCache();
        _magickLoader.ClearCache();

        // Force garbage collection to reclaim managed heap LOH allocations (pixel byte arrays)
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Trim the process working set so that freed native heap pages (from
        // SoftwareBitmap / Magick.NET temporary allocations) are released back to
        // the OS instead of sitting in the working set as committed-but-unused pages.
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    [System.Runtime.InteropServices.LibraryImport("psapi.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(nint hProcess);
}
