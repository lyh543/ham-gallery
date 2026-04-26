using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Loaders;
using FluentGallery.Models;
using FluentGallery.Controls;
using FluentGallery.ViewModels;
using FluentGallery.Converters;
using Microsoft.UI.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage : Page
{
    // ── ViewModel ─────────────────────────────────────────────────────────────

    public PhotoDetailViewModel ViewModel { get; }

    // ── Toolbar auto-hide ─────────────────────────────────────────────────────

    private readonly DispatcherTimer _hideTimer;
    private bool _toolbarVisible = false;
    private bool _hideChromeInProgress = false;

    // Track pointer count over Chrome elements; pause hide timer when > 0
    private int _chromePointerCount = 0;

    // Last pointer position on the page. Used to ignore synthetic PointerMoved
    // events that can fire without actual mouse movement when overlays hide/show.
    private Point? _lastPagePointerPosition;

    // ── Rotation ──────────────────────────────────────────────────────────────

    private double _rotationAngle = 0.0;

    // ── Filmstrip selection guard ─────────────────────────────────────────────

    private bool _suppressFilmStripChange = false;

    // ── Filmstrip lazy-loading ────────────────────────────────────────────────

    private HashSet<int> _loadedFilmstripIndices = new();
    private readonly object _filmstripLoadLock = new();

    // ── Filmstrip drag-scroll ──────────────────────────────────────────────────

    private bool _filmstripDragging = false;
    private bool _filmstripPointerCaptured = false;
    private double _filmstripLastX = 0;
    private Point _filmstripDragStart = default;

    // ── Fullscreen ────────────────────────────────────────────────────────────

    private bool _isFullscreen = false;
    private bool _wasMaximizedBeforeFullscreen = false;

    // ── CancellationTokens ────────────────────────────────────────────────────

    private CancellationTokenSource _cts = new();

    // Per-path CTS for in-flight preload tasks. Allows cancelling individual paths
    // rather than all at once when the preload window shifts.
    private readonly Dictionary<string, CancellationTokenSource> _preloadTasks =
        new(StringComparer.OrdinalIgnoreCase);

    // Incremented on every photo navigation; stale LoadCurrentImageAsync completions
    // check this and discard their result if a newer load has started.
    private int _loadGeneration = 0;

    // ── Preload debounce ──────────────────────────────────────────────────────

    // Fires 1 s after the last navigation to start/update preload tasks.
    private readonly DispatcherTimer _preloadDebounce;
    private int _pendingPreloadIndex = -1;
    private bool _isInitialized = false;

    // ── Pending navigation args (set in OnNavigatedTo, consumed in Loaded) ───

    private PhotoDetailArgs?     _pendingArgs;
    private PhotoDetailFileArgs? _pendingFileArgs;
    private string?              _pendingIndexDirectory;
    private string?              _pendingPromptDirectory;
    private bool                 _indexPromptShown;
    private readonly Flyout      _indexPromptFlyout;
    private readonly IndexPrompt _indexPromptContent;
    private readonly DispatcherTimer _indexPromptAutoHideTimer;
    private bool _indexPromptClosingByButton;
    private bool _indexPromptReopenScheduled;
    private bool _indexPromptAllowImmediateClose;
    private bool _indexPromptDismissAnimating;

    // ── Computed properties for XAML bindings ─────────────────────────────────

    public string FilmStripPinTooltip =>
        ViewModel.IsFilmStripAvailable ? "显示胶片栏" : "当前目录未被索引，胶片栏不可用";

    private bool DebugKeepChromeVisible => ViewModel.DebugKeepPhotoDetailChromeVisible;

    // ── Image loaders ─────────────────────────────────────────────────────────

    private readonly WicImageLoader    _wicLoader;
    private readonly MagickImageLoader _magickLoader;
    private readonly StringToImageSourceConverter _imageSourceConverter = new();

    // ── Swipe preview ────────────────────────────────────────────────────────

    private string? _swipePreviewPath;
    private bool _touchSwipeDragging = false;
    private uint? _touchSwipePointerId;
    private Point _touchSwipeStart;
    private bool _touchSwipePreviewActive = false;
    private readonly Dictionary<uint, Point> _touchPointers = new();
    private bool _touchPinching = false;
    private double _touchPinchStartDistance = 0.0;
    private double _touchPinchStartZoom = 1.0;
    private DateTime _lastTouchTapTime = DateTime.MinValue;
    private Point _lastTouchTapPosition;
    private bool _mouseOverlayDragging = false;
    private bool _mouseOverlayMoved = false;
    private bool _mouseOverlayPreviewActive = false;
    private Point _mouseOverlayStart;
    private Point _mouseOverlayLastPoint;
    private DateTime _lastMouseTapTime = DateTime.MinValue;
    private Point _lastMouseTapPosition;

    // ── Toast ─────────────────────────────────────────────────────────────────

    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _edgeBoundaryThrottle;
    private bool _edgeBoundaryThrottleActive = false;
    private enum ToastKind { Normal, Error }
    private readonly ScanService _scanService =
        App.Current.Services.GetRequiredService<ScanService>();

    private readonly ILogger<PhotoDetailPage> _logger =
        App.Current.Services.GetRequiredService<ILogger<PhotoDetailPage>>();

    // ── Constructor ───────────────────────────────────────────────────────────

    public PhotoDetailPage()
    {
        InitializeComponent();

        _indexPromptContent = new IndexPrompt();
        _indexPromptContent.ConfirmClicked += IndexPrompt_ConfirmClicked;
        _indexPromptContent.CancelClicked += IndexPrompt_CancelClicked;

        _indexPromptFlyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content   = _indexPromptContent,
        };

        _indexPromptFlyout.OverlayInputPassThroughElement = this;
        _indexPromptFlyout.Closing += IndexPromptFlyout_Closing;
        _indexPromptFlyout.Closed += IndexPromptFlyout_Closed;

        _indexPromptAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _indexPromptAutoHideTimer.Tick += IndexPromptAutoHideTimer_Tick;

        ViewModel = App.Current.Services.GetRequiredService<PhotoDetailViewModel>();

        _wicLoader    = App.Current.Services.GetRequiredService<WicImageLoader>();
        _magickLoader = App.Current.Services.GetRequiredService<MagickImageLoader>();
        ApplyPreloadCount(ViewModel.PreloadCountBack, ViewModel.PreloadCountForward);

        // Auto-hide timer
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) => HideChrome();

        // Preload debounce: 1 s after last navigation, compute diff and launch tasks.
        _preloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _preloadDebounce.Tick += (_, _) =>
        {
            _preloadDebounce.Stop();
            if (_pendingPreloadIndex >= 0)
                UpdatePreloadTasks(_pendingPreloadIndex);
        };

        // Toast auto-dismiss timer (3 s)
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => HideToast();

        // Edge boundary throttle timer (500 ms) - prevents too frequent toast notifications
        _edgeBoundaryThrottle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _edgeBoundaryThrottle.Tick += (_, _) =>
        {
            _edgeBoundaryThrottleActive = false;
            _edgeBoundaryThrottle.Stop();
        };

        ZoomImage.SwipeLeft  += OnZoomImageSwipeLeft;
        ZoomImage.SwipeRight += OnZoomImageSwipeRight;
        ZoomImage.ZoomUserChanged += ShowChrome;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _scanService.ScanCompleted += OnScanCompleted;
        SizeChanged += PhotoDetailPage_SizeChanged;
    }

    // ── Page lifecycle ────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _cts = new CancellationTokenSource();

        if (e.Parameter is PhotoDetailArgs args)
        {
            _pendingArgs     = args;
            _pendingFileArgs = null;
        }
        else if (e.Parameter is PhotoDetailFileArgs fileArgs)
        {
            _pendingFileArgs = fileArgs;
            _pendingArgs     = null;
        }
        else
        {
            return;
        }

        // Defer all heavy work (DB query, filmstrip build, image decode) until
        // after the first layout pass so the page skeleton renders immediately.
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;

        // Set right-column width to exactly the caption buttons area so our content
        // doesn't underlay the system min/max/close buttons.
        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
            var dpi = WindowsApiHelper.GetDpiForWindow(hwnd);
            double scale = dpi / 96.0;

            // Set toolbar height to 75 physical pixels (converted to logical pixels)
            double logicalToolbarHeight = 75.0 / scale;
            Toolbar.Height = logicalToolbarHeight;

            // RightInset is in physical pixels; convert to logical pixels using DPI scale
            double logicalRightInset = appWindow.TitleBar.RightInset / scale;
            CaptionButtonsColumn.Width = new GridLength(logicalRightInset);
        }

        ElasticScrollHelper.Attach(FilmStrip, ElasticScrollHelper.ScrollAxis.Horizontal);
        ElasticScrollHelper.Attach(InfoScrollViewer);

        // Add pointer event handlers to FilmStrip to capture drag events even on ListViewItems
        FilmStrip.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(FilmStrip_PointerPressed), handledEventsToo: true);
        FilmStrip.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(FilmStrip_PointerMoved), handledEventsToo: true);
        FilmStrip.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(FilmStrip_PointerReleased), handledEventsToo: true);
        FilmStrip.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(FilmStrip_PointerReleased), handledEventsToo: true);

        // Attach pointer events to ZoomSlider container (for pause/resume hide timer)
        AttachZoomSliderPointerEvents();

        if (_pendingArgs is not null)
        {
            await ViewModel.InitializeAsync(
                _pendingArgs.Photos,
                _pendingArgs.InitialIndex,
                DispatcherQueue,
                _cts.Token);
        }
        else if (_pendingFileArgs is not null)
        {
            await ViewModel.InitializeFromFileAsync(
                _pendingFileArgs.FilePath,
                DispatcherQueue,
                _cts.Token);

            // Set up ContentFrame so pressing Back reveals the album's photo list.
            if (ViewModel.AlbumId is long albumId && App.Current.MainWindow is MainWindow mw)
                mw.NavigateContentToAlbum(albumId);

            await PromptToIndexDirectoryIfNeededAsync();
        }
        else
        {
            return;
        }

        // LoadCurrentImageAsync / UpdateCounterText / PreloadAdjacent are already
        // triggered by ViewModel_PropertyChanged when CurrentImagePath is set inside
        // InitializeAsync → NavigateToIndexAsync.
        ApplyFilmStripPinState();
        ApplyShowPreloadStatus();
        _chromePointerCount = 0;
        _hideChromeInProgress = false;
        ShowChrome();

        // Mark initialization complete; subsequent navigations will use debounce
        _isInitialized = true;

        // Ensure focus so keyboard navigation works immediately
        Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _hideTimer.Stop();
        _preloadDebounce.Stop();
        _toastTimer.Stop();
        _edgeBoundaryThrottle.Stop();
        _touchSwipeDragging = false;
        _touchSwipePreviewActive = false;
        _touchSwipePointerId = null;
        _touchPointers.Clear();
        _touchPinching = false;
        _touchPinchStartDistance = 0;
        _touchPinchStartZoom = 1;
        _mouseOverlayDragging = false;
        _mouseOverlayPreviewActive = false;
        ResetSwipePreviewTransforms();
        _indexPromptAutoHideTimer.Stop();
        _indexPromptFlyout.Hide();
        _cts.Cancel();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _scanService.ScanCompleted -= OnScanCompleted;
        SizeChanged -= PhotoDetailPage_SizeChanged;
        ViewModel.Dispose();

        // Signal cancellation on all in-flight preload tasks. Do NOT Dispose here —
        // each task's ContinueWith callback owns the CTS lifetime and will Dispose it.
        foreach (var cts in _preloadTasks.Values) cts.Cancel();
        _preloadTasks.Clear();

        // Release the currently displayed BitmapImage so the WIC decoder surface
        // (~48 MB per 12 MP HEIC in BGRA8) is freed as soon as GC runs.
        // Without this, the page stays in Frame's BackStack with MainImage.Source
        // still set, keeping the COM reference count alive and locking WIC memory.
        ZoomImage.SetLoading();

        // Loaders are singletons; clear their caches when leaving the page so
        // BitmapImage objects (and their GPU/WIC memory) are released promptly.
        _wicLoader.ClearCache();
        _magickLoader.ClearCache();

        _logger.LogDebug("OnNavigatedFrom: image caches cleared");
    }

    private async Task PromptToIndexDirectoryIfNeededAsync()
    {
        if (_indexPromptShown || !ViewModel.ShouldPromptToIndexCurrentDirectory())
            return;

        var directoryPath = ViewModel.GetCurrentDirectoryPath();
        if (string.IsNullOrEmpty(directoryPath))
            return;

        _indexPromptShown = true;
        _pendingPromptDirectory = directoryPath;

        _indexPromptContent.Title = "加入相册";
        _indexPromptContent.Message = "当前图片所在目录尚未加入扫描范围。是否将该目录加入相册并在后台建立索引？建立完成后将自动启用胶片栏。";
        _indexPromptContent.ConfirmText = "加入并索引";
        _indexPromptContent.CancelText = "暂不加入";
        ShowIndexPrompt();
    }

    private async void IndexPrompt_ConfirmClicked(object sender, RoutedEventArgs e)
    {
        var directoryPath = _pendingPromptDirectory;
        _pendingPromptDirectory = null;
        await HideIndexPromptWithFadeAsync(fromButton: true);

        if (string.IsNullOrEmpty(directoryPath))
            return;

        _pendingIndexDirectory = directoryPath;
        await ViewModel.EnsureDirectoryIndexedAsync(directoryPath, DispatcherQueue, _cts.Token);
        ShowToast("已加入扫描范围，正在建立索引…", ToastKind.Normal, showUndo: false);
    }

    private async void IndexPrompt_CancelClicked(object sender, RoutedEventArgs e)
    {
        _pendingPromptDirectory = null;
        await HideIndexPromptWithFadeAsync(fromButton: true);
    }

    private void IndexPromptFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (_indexPromptAllowImmediateClose)
            return;

        // Always keep prompt visible in always-on mode unless closed by buttons.
        if (DebugKeepChromeVisible && !_indexPromptClosingByButton)
        {
            args.Cancel = true;
            return;
        }

        // Light-dismiss in normal mode should fade out instead of abrupt close.
        if (!_indexPromptClosingByButton)
        {
            args.Cancel = true;
            if (_indexPromptDismissAnimating)
                return;

            _pendingPromptDirectory = null;
            _ = HideIndexPromptWithFadeAsync(fromButton: false);
        }
    }

    private void IndexPromptFlyout_Closed(object? sender, object e)
    {
        _indexPromptAutoHideTimer.Stop();
        _indexPromptDismissAnimating = false;
        _indexPromptAllowImmediateClose = false;

        // Button-close should fully dismiss and never auto-reopen.
        if (_indexPromptClosingByButton)
        {
            _indexPromptClosingByButton = false;
            return;
        }

        // Keep the prompt visible during resize when
        // toolbar-always-on mode is enabled.
        if (DebugKeepChromeVisible &&
            !string.IsNullOrEmpty(_pendingPromptDirectory) &&
            !_indexPromptReopenScheduled)
        {
            _indexPromptReopenScheduled = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _indexPromptReopenScheduled = false;
                if (!string.IsNullOrEmpty(_pendingPromptDirectory))
                    ShowIndexPrompt();
            });
            return;
        }

        _pendingPromptDirectory = null;
    }

    private async void IndexPromptAutoHideTimer_Tick(object? sender, object e)
    {
        _indexPromptAutoHideTimer.Stop();
        if (DebugKeepChromeVisible || !_indexPromptFlyout.IsOpen)
            return;

        _pendingPromptDirectory = null;
        await HideIndexPromptWithFadeAsync(fromButton: false);
    }

    private void PhotoDetailPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!DebugKeepChromeVisible)
            return;

        if (string.IsNullOrEmpty(_pendingPromptDirectory))
            return;

        if (_indexPromptFlyout.IsOpen || _indexPromptReopenScheduled)
            return;

        _indexPromptReopenScheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _indexPromptReopenScheduled = false;
            if (!string.IsNullOrEmpty(_pendingPromptDirectory) && !_indexPromptFlyout.IsOpen)
                ShowIndexPrompt();
        });
    }

    private void ShowIndexPrompt()
    {
        _indexPromptAutoHideTimer.Stop();
        _indexPromptFlyout.ShowAt(IndexPromptAnchor, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        });

        // Fade-in animation for prompt content.
        _indexPromptContent.Opacity = 0;
        AnimateOpacity(_indexPromptContent, 1.0, durationMs: 250);

        if (!DebugKeepChromeVisible)
            _indexPromptAutoHideTimer.Start();
    }

    private async Task HideIndexPromptWithFadeAsync(bool fromButton)
    {
        _indexPromptAutoHideTimer.Stop();
        if (!_indexPromptFlyout.IsOpen)
            return;

        _indexPromptClosingByButton = fromButton;
        AnimateOpacity(_indexPromptContent, 0.0, durationMs: 150);
        _indexPromptDismissAnimating = true;
        await Task.Delay(150);
        _indexPromptAllowImmediateClose = true;
        _indexPromptFlyout.Hide();
    }

    private async void OnScanCompleted()
    {
        var pendingDirectory = _pendingIndexDirectory;
        if (string.IsNullOrEmpty(pendingDirectory))
            return;

        if (!ViewModel.IsCurrentFileInDirectory(pendingDirectory))
            return;

        try
        {
            await ViewModel.ReloadCurrentFileContextAsync(DispatcherQueue, _cts.Token);
            _pendingIndexDirectory = null;
            _pendingPromptDirectory = null;

            if (ViewModel.AlbumId is long albumId && App.Current.MainWindow is MainWindow mw)
                mw.NavigateContentToAlbum(albumId);

            ApplyFilmStripPinState();
            Bindings.Update();
            ShowToast("索引完成，胶片栏已可用", ToastKind.Normal, showUndo: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload direct-open file after indexing");
        }
    }

    // ── FilmStrip pin toggle ──────────────────────────────────────────────────

    private void FilmStripPin_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.ToggleFilmStripPinnedAsync(_cts.Token);
        ApplyFilmStripPinState();
    }

    private void ApplyFilmStripPinState()
    {
        bool available = ViewModel.IsFilmStripAvailable;
        bool pinned    = available && ViewModel.FilmStripPinned;
        FilmStripPinButton.IsChecked = pinned;
        FilmStripRow.Height = pinned ? GridLength.Auto : new GridLength(0);
    }

    private void ApplyShowPreloadStatus()
    {
        bool show = ViewModel.ShowPreloadStatus;
        foreach (var item in ViewModel.FilmStripItems)
            item.ShowPreloadBadge = show;
    }

    // ── ViewModel property changes → UI ───────────────────────────────────────

    private void ViewModel_PropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PhotoDetailViewModel.CurrentImagePath):
                _ = LoadCurrentImageAsync();
                UpdateCounterText();
                TitleText.Text = ViewModel.CurrentPhoto?.FileName ?? string.Empty;
                // Only debounce if initialized; skip debounce on first navigation
                if (_isInitialized)
                {
                    _pendingPreloadIndex = ViewModel.CurrentIndex;
                    _preloadDebounce.Stop();
                    _preloadDebounce.Start();
                }
                else
                {
                    // First load: trigger preload immediately without debounce
                    _pendingPreloadIndex = ViewModel.CurrentIndex;
                    UpdatePreloadTasks(_pendingPreloadIndex);
                }
                break;

            case nameof(PhotoDetailViewModel.InfoFileName):
                InfoFileName.Text    = ViewModel.InfoFileName ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoFilePath):
                InfoFilePath.Text    = ViewModel.InfoFilePath ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoFileSize):
                InfoFileSize.Text    = ViewModel.InfoFileSize ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoResolution):
                InfoResolution.Text  = ViewModel.InfoResolution ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoCreatedAt):
                InfoCreatedAt.Text   = ViewModel.InfoCreatedAt ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoModifiedAt):
                InfoModifiedAt.Text  = ViewModel.InfoModifiedAt ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoTakenAt):
                InfoTakenAt.Text     = ViewModel.InfoTakenAt ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoCamera):
                InfoCamera.Text      = ViewModel.InfoCamera ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoLens):
                InfoLens.Text        = ViewModel.InfoLens ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoAperture):
                InfoAperture.Text    = ViewModel.InfoAperture ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoShutter):
                InfoShutter.Text     = ViewModel.InfoShutter ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoIso):
                InfoIso.Text         = ViewModel.InfoIso ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoFocalLength):
                InfoFocalLength.Text = ViewModel.InfoFocalLength ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoGps):
                var gps = ViewModel.InfoGps;
                bool hasGps = !string.IsNullOrEmpty(gps);
                GpsLabel.Visibility = hasGps ? Visibility.Visible : Visibility.Collapsed;
                InfoGps.Visibility  = hasGps ? Visibility.Visible : Visibility.Collapsed;
                InfoGps.Text        = gps ?? string.Empty;
                break;
            case nameof(PhotoDetailViewModel.InfoOrientation):
                InfoOrientation.Text = ViewModel.InfoOrientation ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoColorSpace):
                InfoColorSpace.Text  = ViewModel.InfoColorSpace  ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoBitDepth):
                InfoBitDepth.Text    = ViewModel.InfoBitDepth    ?? "—"; break;

            case nameof(PhotoDetailViewModel.InfoGifDuration):
                InfoGifDuration.Text = ViewModel.InfoGifDuration ?? "—";
                GifSection.Visibility = ViewModel.InfoGifDuration is not null
                    ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(PhotoDetailViewModel.InfoGifFrames):
                InfoGifFrames.Text   = ViewModel.InfoGifFrames   ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoGifFrameRate):
                InfoGifFrameRate.Text = ViewModel.InfoGifFrameRate ?? "—"; break;

            case nameof(PhotoDetailViewModel.IsFilmStripAvailable):
                ApplyFilmStripPinState();
                // Notify XAML that the computed tooltip text has changed.
                Bindings.Update();
                break;

            case nameof(PhotoDetailViewModel.CurrentIndex):
                SyncFilmStripSelection();
                break;

            case nameof(PhotoDetailViewModel.PreloadCountBack):
            case nameof(PhotoDetailViewModel.PreloadCountForward):
                ApplyPreloadCount(ViewModel.PreloadCountBack, ViewModel.PreloadCountForward);
                break;

            case nameof(PhotoDetailViewModel.DebugKeepPhotoDetailChromeVisible):
                if (DebugKeepChromeVisible)
                {
                    _indexPromptAutoHideTimer.Stop();
                    if (!string.IsNullOrEmpty(_pendingPromptDirectory) && !_indexPromptFlyout.IsOpen)
                        ShowIndexPrompt();
                }
                else if (!string.IsNullOrEmpty(_pendingPromptDirectory) && _indexPromptFlyout.IsOpen)
                {
                    _indexPromptAutoHideTimer.Stop();
                    _indexPromptAutoHideTimer.Start();
                }
                break;
        }
    }

    private void ApplyPreloadCount(int back, int forward)
    {
        int cacheSize = back + forward + 1;
        _wicLoader.MaxCacheSize    = cacheSize;
        _magickLoader.MaxCacheSize = cacheSize;
    }

    // ── Keyboard navigation ───────────────────────────────────────────────────

    private async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control);
        bool isCtrl = ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case VirtualKey.Left:
                if (!isCtrl)
                {
                    e.Handled = true;
                    await NavigateRelativeAsync(-1);
                }
                break;

            case VirtualKey.Right:
                if (!isCtrl)
                {
                    e.Handled = true;
                    await NavigateRelativeAsync(1);
                }
                break;

            case VirtualKey.Z:
                if (isCtrl)
                {
                    e.Handled = true;
                    await UndoDeleteAsync();
                }
                break;

            case VirtualKey.F:
                e.Handled = true;
                ToggleFullscreen();
                break;

            case VirtualKey.I:
                e.Handled = true;
                ToggleInfoPanel();
                break;

            case VirtualKey.Delete:
                e.Handled = true;
                await DeleteWithConfirmAsync();
                break;

            case VirtualKey.Escape:
                if (_isFullscreen)
                {
                    e.Handled = true;
                    ExitFullscreen();
                }
                break;
        }
    }

    // ── Toolbar button handlers ───────────────────────────────────────────────

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is MainWindow mw)
            mw.ClosePhotoDetail();
        else if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private async void RotateCw_Click(object sender, RoutedEventArgs e)
    {
        _rotationAngle = (_rotationAngle + 90) % 360;
        ZoomImage.RotationAngle = _rotationAngle;
        await ViewModel.RotateAsync(clockwise: true, _cts.Token);
    }

    private async void RotateCcw_Click(object sender, RoutedEventArgs e)
    {
        _rotationAngle = (_rotationAngle - 90 + 360) % 360;
        ZoomImage.RotationAngle = _rotationAngle;
        await ViewModel.RotateAsync(clockwise: false, _cts.Token);
    }

    private async void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.CurrentImagePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);

            var oainfo = new WindowsApiHelper.OPENASINFO
            {
                pcszFile = path,
                pcszClass = null,
                oaifInFlags = WindowsApiHelper.OAIF_ALLOW_REGISTRATION | WindowsApiHelper.OAIF_EXEC
            };

            int hResult = WindowsApiHelper.SHOpenWithDialog(hwnd, ref oainfo);
            if (hResult != 0)
            {
                _logger.LogWarning("SHOpenWithDialog failed with HRESULT: {HResult:X8}", hResult);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open file with dialog");
        }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.CurrentImagePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            WindowsApiHelper.SHParseDisplayName(
                Path.GetDirectoryName(path) ?? string.Empty,
                IntPtr.Zero,
                out IntPtr pidlFolder,
                0,
                out _);

            if (pidlFolder != IntPtr.Zero)
            {
                try
                {
                    WindowsApiHelper.SHParseDisplayName(
                        path,
                        IntPtr.Zero,
                        out IntPtr pidlFile,
                        0,
                        out _);

                    if (pidlFile != IntPtr.Zero)
                    {
                        try
                        {
                            int hResult = WindowsApiHelper.SHOpenFolderAndSelectItems(
                                pidlFolder,
                                1,
                                new[] { pidlFile },
                                0);

                            if (hResult != 0)
                            {
                                _logger.LogWarning("SHOpenFolderAndSelectItems failed with HRESULT: {HResult:X8}", hResult);
                            }
                        }
                        finally
                        {
                            WindowsApiHelper.CoTaskMemFree(pidlFile);
                        }
                    }
                }
                finally
                {
                    WindowsApiHelper.CoTaskMemFree(pidlFolder);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show file in explorer");
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
        => await DeleteWithConfirmAsync();

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
        => ToggleFullscreen();

    private void InfoToggle_Click(object sender, RoutedEventArgs e)
        => ToggleInfoPanel();

    // ── Info panel toggle ─────────────────────────────────────────────────────

    private void ToggleInfoPanel()
    {
        ViewModel.IsInfoPanelOpen = !ViewModel.IsInfoPanelOpen;
        InfoToggleButton.IsChecked = ViewModel.IsInfoPanelOpen;
        InfoPanelColumn.Width = ViewModel.IsInfoPanelOpen
            ? new GridLength(300)
            : new GridLength(0);
    }

    // ── Delete with confirmation ──────────────────────────────────────────────

    private async Task DeleteWithConfirmAsync()
    {
        if (ViewModel.CurrentPhoto is null) return;

        if (ViewModel.ConfirmBeforeDelete)
        {
            // Build the checkbox「下次不再提示」
            var dontAskCheck = new CheckBox
            {
                Content = "下次不再提示",
                Margin  = new Thickness(0, 12, 0, 0),
            };

            var panel = new StackPanel { Spacing = 0 };
            panel.Children.Add(new TextBlock
            {
                Text        = $"将「{ViewModel.CurrentPhoto.FileName}」移入回收站？",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(dontAskCheck);

            bool confirmed = await ConfirmDialogHelper.ShowAsync(
                XamlRoot,
                "删除照片",
                panel,
                "删除",
                confirmStyle: DialogButtonStyle.Danger);

            if (!confirmed) return;

            if (dontAskCheck.IsChecked == true)
            {
                await ViewModel.DisableDeleteConfirmAsync(_cts.Token);
                ShowToast("已关闭删除确认弹窗，可在设置中重新开启", ToastKind.Normal, showUndo: false);
            }
        }

        var deletedName = await ViewModel.DeleteAsync(_cts.Token);

        if (deletedName is null)
        {
            ShowToast("删除失败，请检查文件权限", ToastKind.Error, showUndo: false);
            return;
        }

        ShowToast($"照片「{deletedName}」已删除", ToastKind.Normal, showUndo: true);

        if (ViewModel.CurrentImagePath is null)
        {
            if (App.Current.MainWindow is MainWindow mw)
                mw.ClosePhotoDetail();
            else if (Frame.CanGoBack)
                Frame.GoBack();
        }
    }

    // ── Undo delete ───────────────────────────────────────────────────────────

    private async Task UndoDeleteAsync()
    {
        if (!ViewModel.CanUndo) return;

        HideToast();

        var restoredName = await ViewModel.UndoDeleteAsync(_cts.Token);

        if (restoredName is null)
            ShowToast("恢复失败，文件可能已被移动或删除", ToastKind.Error, showUndo: false);
        else
            ShowToast($"照片「{restoredName}」已恢复", ToastKind.Normal, showUndo: false);
    }

    // ── Toast undo button handler ─────────────────────────────────────────────

    private async void ToastUndo_Click(object sender, RoutedEventArgs e)
        => await UndoDeleteAsync();

    // ── Counter text ──────────────────────────────────────────────────────────

    private void UpdateCounterText()
    {
        int total = ViewModel.FilmStripItems.Count;
        CounterText.Text = total > 0
            ? $"{ViewModel.CurrentIndex + 1} / {total}"
            : string.Empty;
    }
}
