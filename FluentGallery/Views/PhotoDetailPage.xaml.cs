using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Loaders;
using FluentGallery.ViewModels;
using FluentGallery.Controls;
using FluentGallery.Converters;
using Microsoft.UI.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
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

    private readonly ILogger<PhotoDetailPage> _logger =
        App.Current.Services.GetRequiredService<ILogger<PhotoDetailPage>>();

    // ── Constructor ───────────────────────────────────────────────────────────

    public PhotoDetailPage()
    {
        InitializeComponent();

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
            // Set system buttons to Tall (75 physical pixels)
            try
            {
                appWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
            }
            catch { }

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
        _cts.Cancel();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
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

    // ── Attach pointer events to ZoomSlider ───────────────────────────────────

    private void AttachZoomSliderPointerEvents()
    {
        // Find ZoomSliderContainer in ZoomImage's visual tree and attach pointer events
        var zoomSliderContainer = FindVisualChild<Border>(ZoomImage, "ZoomSliderContainer");
        if (zoomSliderContainer is not null)
        {
            zoomSliderContainer.AddHandler(UIElement.PointerEnteredEvent,
                new PointerEventHandler(ChromeElement_PointerEntered), handledEventsToo: true);
            zoomSliderContainer.AddHandler(UIElement.PointerExitedEvent,
                new PointerEventHandler(ChromeElement_PointerExited), handledEventsToo: true);
        }
    }

    private void TouchSwipeOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageViewport).Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            if (!e.GetCurrentPoint(ImageViewport).Properties.IsLeftButtonPressed)
                return;

            _mouseOverlayDragging = true;
            _mouseOverlayMoved = false;
            _mouseOverlayPreviewActive = false;
            _mouseOverlayStart = point;
            _mouseOverlayLastPoint = point;
            TouchSwipeOverlay.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            return;

        _touchPointers[e.Pointer.PointerId] = point;
        TouchSwipeOverlay.CapturePointer(e.Pointer);

        if (_touchPointers.Count == 1)
        {
            _touchSwipeDragging = ZoomImage.IsAtFitZoom;
            _touchSwipePreviewActive = false;
            _touchSwipePointerId = e.Pointer.PointerId;
            _touchSwipeStart = point;
            _logger.LogDebug("Touch overlay pressed: pointer={PointerId}, pos=({X:F1},{Y:F1}), fit={IsAtFitZoom}", _touchSwipePointerId, point.X, point.Y, ZoomImage.IsAtFitZoom);
        }
        else if (_touchPointers.Count == 2)
        {
            CancelTouchSwipePreview();
            _touchPinching = true;
            _touchPinchStartDistance = GetTouchPinchDistance();
            _touchPinchStartZoom = ZoomImage.CurrentZoomFactor;
            _logger.LogDebug("Touch overlay pinch started: distance={Distance:F1}, zoom={Zoom:F3}", _touchPinchStartDistance, _touchPinchStartZoom);
        }

        e.Handled = true;
    }

    private void TouchSwipeOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageViewport).Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && _mouseOverlayDragging)
        {
            double mouseDx = point.X - _mouseOverlayLastPoint.X;
            double mouseDy = point.Y - _mouseOverlayLastPoint.Y;
            double totalDx = point.X - _mouseOverlayStart.X;
            double totalDy = point.Y - _mouseOverlayStart.Y;

            if (ZoomImage.IsAtFitZoom)
            {
                const double MouseHorizontalIntentThreshold = 12.0;
                if (!_mouseOverlayPreviewActive)
                {
                    if (Math.Abs(totalDx) < MouseHorizontalIntentThreshold || Math.Abs(totalDx) <= Math.Abs(totalDy))
                        return;

                    _mouseOverlayPreviewActive = true;
                    _mouseOverlayMoved = true;
                    _logger.LogDebug("Mouse overlay preview started: dx={Dx:F1}, dy={Dy:F1}", totalDx, totalDy);
                }

                OnZoomImageSwipePreviewProgress(new SwipePreviewEventArgs(totalDx, ImageViewport.ActualWidth));
            }
            else
            {
                if (Math.Abs(mouseDx) > 1 || Math.Abs(mouseDy) > 1)
                    _mouseOverlayMoved = true;
                ZoomImage.PanBy(mouseDx, mouseDy);
            }

            _mouseOverlayLastPoint = point;
            e.Handled = true;
            return;
        }

        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch || !_touchPointers.ContainsKey(e.Pointer.PointerId))
            return;

        _touchPointers[e.Pointer.PointerId] = point;

        if (_touchPinching && _touchPointers.Count >= 2)
        {
            double distance = GetTouchPinchDistance();
            if (_touchPinchStartDistance > 0 && distance > 0)
            {
                double scale = distance / _touchPinchStartDistance;
                ZoomImage.ZoomToFactorAt(_touchPinchStartZoom * scale, GetTouchPinchCenter());
            }
            e.Handled = true;
            return;
        }

        if (!_touchSwipeDragging || _touchSwipePointerId != e.Pointer.PointerId || !ZoomImage.IsAtFitZoom)
            return;

        double dx = point.X - _touchSwipeStart.X;
        double dy = point.Y - _touchSwipeStart.Y;

        const double HorizontalIntentThreshold = 12.0;
        if (!_touchSwipePreviewActive)
        {
            if (Math.Abs(dx) < HorizontalIntentThreshold || Math.Abs(dx) <= Math.Abs(dy))
                return;

            _touchSwipePreviewActive = true;
            _logger.LogDebug("Touch overlay preview started: dx={Dx:F1}, dy={Dy:F1}", dx, dy);
        }

        OnZoomImageSwipePreviewProgress(new SwipePreviewEventArgs(dx, ImageViewport.ActualWidth));
        e.Handled = true;
    }

    private async void TouchSwipeOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageViewport).Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            if (_mouseOverlayDragging)
            {
                if (_mouseOverlayPreviewActive)
                    OnZoomImageSwipePreviewCompleted();

                double totalDx = point.X - _mouseOverlayStart.X;
                double totalDy = point.Y - _mouseOverlayStart.Y;
                bool mouseHadPreview = _mouseOverlayPreviewActive;

                _mouseOverlayDragging = false;
                _mouseOverlayPreviewActive = false;
                ReleaseTouchOverlayPointer(e.Pointer.PointerId);

                if (mouseHadPreview)
                {
                    const double MouseMinSwipe = 60.0;
                    if (Math.Abs(totalDx) >= MouseMinSwipe && Math.Abs(totalDx) >= Math.Abs(totalDy) * 1.5)
                    {
                        if (totalDx < 0)
                            await NavigateRelativeAsync(1);
                        else
                            await NavigateRelativeAsync(-1);
                    }
                }
                else if (!_mouseOverlayMoved)
                {
                    HandleMouseTap(point);
                }

                e.Handled = true;
            }
            return;
        }

        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            return;

        bool wasSwipePointer = _touchSwipePointerId == e.Pointer.PointerId;
        _touchPointers.Remove(e.Pointer.PointerId);
        ReleaseTouchOverlayPointer(e.Pointer.PointerId);

        if (_touchPinching)
        {
            if (_touchPointers.Count < 2)
            {
                _touchPinching = false;
                _touchPinchStartDistance = 0;
                _touchPinchStartZoom = 1;
                _logger.LogDebug("Touch overlay pinch ended");
            }
            e.Handled = true;
            return;
        }

        if (!wasSwipePointer)
            return;

        double dx = point.X - _touchSwipeStart.X;
        double dy = point.Y - _touchSwipeStart.Y;
        bool hadPreview = _touchSwipePreviewActive;
        _logger.LogDebug("Touch overlay released: dx={Dx:F1}, dy={Dy:F1}, hadPreview={HadPreview}", dx, dy, hadPreview);

        EndTouchSwipeState();

        if (!hadPreview)
        {
            HandleTouchTap(point);
            e.Handled = true;
            return;
        }

        const double MinSwipe = 60.0;
        if (Math.Abs(dx) < MinSwipe || Math.Abs(dx) < Math.Abs(dy) * 1.5)
        {
            _logger.LogDebug("Touch overlay swipe rejected by threshold");
            e.Handled = true;
            return;
        }

        if (dx < 0)
            await NavigateRelativeAsync(1);
        else
            await NavigateRelativeAsync(-1);

        e.Handled = true;
    }

    private void TouchSwipeOverlay_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            _mouseOverlayDragging = false;
            ReleaseTouchOverlayPointer(e.Pointer.PointerId);
            return;
        }

        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            return;

        _touchPointers.Remove(e.Pointer.PointerId);
        ReleaseTouchOverlayPointer(e.Pointer.PointerId);

        if (_touchSwipePointerId == e.Pointer.PointerId)
            EndTouchSwipeState();

        if (_touchPointers.Count < 2)
        {
            _touchPinching = false;
            _touchPinchStartDistance = 0;
            _touchPinchStartZoom = 1;
        }

        _logger.LogDebug("Touch overlay cancelled: pointer={PointerId}", e.Pointer.PointerId);
        e.Handled = true;
    }

    private void HandleTouchTap(Point point)
    {
        DateTime now = DateTime.UtcNow;
        double dt = (now - _lastTouchTapTime).TotalMilliseconds;
        double dx = point.X - _lastTouchTapPosition.X;
        double dy = point.Y - _lastTouchTapPosition.Y;

        if (dt <= 350 && Math.Sqrt(dx * dx + dy * dy) <= 48)
        {
            _lastTouchTapTime = DateTime.MinValue;
            _logger.LogDebug("Touch overlay double tap: pos=({X:F1},{Y:F1})", point.X, point.Y);
            ZoomImage.ToggleZoomAt(point);
            return;
        }

        _lastTouchTapTime = now;
        _lastTouchTapPosition = point;
    }

    private void HandleMouseTap(Point point)
    {
        DateTime now = DateTime.UtcNow;
        double dt = (now - _lastMouseTapTime).TotalMilliseconds;
        double dx = point.X - _lastMouseTapPosition.X;
        double dy = point.Y - _lastMouseTapPosition.Y;

        if (dt <= 350 && Math.Sqrt(dx * dx + dy * dy) <= 6)
        {
            _lastMouseTapTime = DateTime.MinValue;
            _logger.LogDebug("Mouse overlay double click: pos=({X:F1},{Y:F1})", point.X, point.Y);
            ZoomImage.ToggleZoomAt(point);
            return;
        }

        _lastMouseTapTime = now;
        _lastMouseTapPosition = point;
    }

    private void CancelTouchSwipePreview()
    {
        if (_touchSwipePreviewActive)
            OnZoomImageSwipePreviewCompleted();

        _touchSwipeDragging = false;
        _touchSwipePreviewActive = false;
        _touchSwipePointerId = null;
    }

    private void EndTouchSwipeState()
    {
        CancelTouchSwipePreview();
    }

    private double GetTouchPinchDistance()
    {
        if (_touchPointers.Count < 2) return 0;
        var points = _touchPointers.Values.Take(2).ToArray();
        double dx = points[0].X - points[1].X;
        double dy = points[0].Y - points[1].Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private Point GetTouchPinchCenter()
    {
        var points = _touchPointers.Values.Take(2).ToArray();
        return new Point((points[0].X + points[1].X) / 2.0, (points[0].Y + points[1].Y) / 2.0);
    }

    private void ReleaseTouchOverlayPointer(uint pointerId)
    {
        var captured = TouchSwipeOverlay.PointerCaptures.FirstOrDefault(c => c.PointerId == pointerId);
        if (captured is not null)
            TouchSwipeOverlay.ReleasePointerCapture(captured);
    }

    private void OnZoomImageSwipeLeft()
    {
        _logger.LogDebug("PhotoDetail swipe event: next photo from index {CurrentIndex}", ViewModel.CurrentIndex);
        _ = NavigateRelativeAsync(1);
    }

    private void OnZoomImageSwipeRight()
    {
        _logger.LogDebug("PhotoDetail swipe event: previous photo from index {CurrentIndex}", ViewModel.CurrentIndex);
        _ = NavigateRelativeAsync(-1);
    }

    private async Task NavigateRelativeAsync(int delta)
    {
        int targetIndex = ViewModel.CurrentIndex + delta;
        _logger.LogDebug(
            "NavigateRelativeAsync: current={CurrentIndex}, delta={Delta}, target={TargetIndex}, count={Count}",
            ViewModel.CurrentIndex,
            delta,
            targetIndex,
            ViewModel.FilmStripItems.Count);

        if (targetIndex < 0 || targetIndex >= ViewModel.FilmStripItems.Count)
        {
            _logger.LogDebug("NavigateRelativeAsync blocked by boundary");
            ShowEdgeBoundaryToast(delta < 0);
            return;
        }

        await ViewModel.NavigateToIndexAsync(targetIndex, _cts.Token);
    }

    private void ShowEdgeBoundaryToast(bool isFirst)
    {
        if (_edgeBoundaryThrottleActive)
            return;

        ShowToast(isFirst ? "已经是第一张照片了" : "已经是最后一张照片了", ToastKind.Normal, showUndo: false);
        _edgeBoundaryThrottleActive = true;
        _edgeBoundaryThrottle.Stop();
        _edgeBoundaryThrottle.Start();
    }

    private void OnZoomImageSwipePreviewProgress(SwipePreviewEventArgs args)
    {
        int direction = args.HorizontalOffset < 0 ? 1 : -1;
        int targetIndex = ViewModel.CurrentIndex + direction;
        _logger.LogDebug(
            "Swipe preview progress: current={CurrentIndex}, target={TargetIndex}, offset={Offset:F1}, viewport={Viewport:F1}",
            ViewModel.CurrentIndex,
            targetIndex,
            args.HorizontalOffset,
            args.ViewportWidth);

        if (targetIndex < 0 || targetIndex >= ViewModel.FilmStripItems.Count)
        {
            _logger.LogDebug("Swipe preview blocked by boundary");
            ResetSwipePreviewTransforms();
            return;
        }

        EnsureSwipePreviewImage(targetIndex);

        double viewportWidth = args.ViewportWidth > 0 ? args.ViewportWidth : ImageViewport.ActualWidth;
        if (viewportWidth <= 0)
        {
            _logger.LogDebug("Swipe preview skipped because viewport width is invalid");
            return;
        }

        double offset = Math.Clamp(args.HorizontalOffset, -viewportWidth, viewportWidth);
        ZoomImageTransform.X = offset;
        SwipePreviewTransform.X = offset < 0 ? viewportWidth + offset : -viewportWidth + offset;

        double progress = Math.Clamp(Math.Abs(offset) / viewportWidth, 0.0, 1.0);
        SwipePreviewImage.Opacity = Math.Min(1.0, 0.2 + progress * 0.8);
    }

    private void OnZoomImageSwipePreviewCompleted()
    {
        _logger.LogDebug("Swipe preview completed/reset");
        ResetSwipePreviewTransforms();
    }

    private void EnsureSwipePreviewImage(int targetIndex)
    {
        string targetPath = ViewModel.FilmStripItems[targetIndex].Photo.FilePath;
        if (string.Equals(_swipePreviewPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            SwipePreviewImage.Visibility = Visibility.Visible;
            return;
        }

        _logger.LogDebug("Loading swipe preview image for target index {TargetIndex}: {Path}", targetIndex, targetPath);
        _swipePreviewPath = targetPath;
        SwipePreviewImage.Source = _imageSourceConverter.Convert(targetPath, typeof(ImageSource), string.Empty, string.Empty) as ImageSource;
        SwipePreviewImage.Visibility = SwipePreviewImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        _logger.LogDebug("Swipe preview image source resolved: hasSource={HasSource}", SwipePreviewImage.Source is not null);
    }

    private void ResetSwipePreviewTransforms()
    {
        _swipePreviewPath = null;
        ZoomImageTransform.X = 0;
        SwipePreviewTransform.X = 0;
        SwipePreviewImage.Opacity = 0;
        SwipePreviewImage.Source = null;
        SwipePreviewImage.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Recursively search for a named element of type T in the visual tree.
    /// </summary>
    private T? FindVisualChild<T>(DependencyObject parent, string elementName) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            
            // Check if this element matches the name
            if (child is FrameworkElement fe && fe.Name == elementName && child is T foundElement)
                return foundElement;

            // Recursively search in children
            var result = FindVisualChild<T>(child, elementName);
            if (result is not null)
                return result;
        }
        return null;
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
        }
    }

    private void ApplyPreloadCount(int back, int forward)
    {
        int cacheSize = back + forward + 1;
        _wicLoader.MaxCacheSize    = cacheSize;
        _magickLoader.MaxCacheSize = cacheSize;
    }

    // ── Toolbar chrome show / hide ────────────────────────────────────────────

    // ShowChrome/HideChrome only control the Toolbar and nav arrows.
    // FilmStrip visibility is controlled exclusively by the pin button via ApplyFilmStripPinState.
    private void ShowChrome()
    {
        if (DebugKeepChromeVisible)
        {
            _hideTimer.Stop();
            _hideChromeInProgress = false;
            _chromePointerCount = 0;
            EnsureChromeVisible();
            ZoomImage.ShowZoomSlider();
            return;
        }

        // Ignore if we're in the middle of hiding chrome animation
        if (_hideChromeInProgress) return;

        bool wasHidden = !_toolbarVisible;
        EnsureChromeVisible();
        if (wasHidden)
            ZoomImage.ShowZoomSlider();

        RestartHideTimer();
    }

    private void EnsureChromeVisible()
    {
        if (_toolbarVisible)
            return;

        _toolbarVisible = true;
        AnimateOpacity(Toolbar, 1.0);
        AnimateOpacity(BottomToolbar, 1.0);
        AnimateOpacity(PrevButton, 1.0);
        AnimateOpacity(NextButton, 1.0);
    }

    private void RestartHideTimer()
    {
        if (DebugKeepChromeVisible)
        {
            _hideTimer.Stop();
            return;
        }

        if (_chromePointerCount > 0 || !_toolbarVisible)
            return;

        _hideTimer.Stop();
        _hideTimer.Start();
        _logger.LogDebug("Chrome: Hide timer restarted (1s interval)");
    }

    private void HideChrome()
    {
        if (DebugKeepChromeVisible)
        {
            _hideTimer.Stop();
            return;
        }

        _hideTimer.Stop();
        _toolbarVisible = false;
        _hideChromeInProgress = true;

        AnimateOpacity(Toolbar, 0.0);
        AnimateOpacity(BottomToolbar, 0.0);
        AnimateOpacity(PrevButton, 0.0);
        AnimateOpacity(NextButton, 0.0);
        ZoomImage.HideZoomSlider();

        _logger.LogDebug("Chrome: Hiding chrome");

        // Re-enable ShowChrome after animation duration + small buffer
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() => _hideChromeInProgress = false);
        });
    }

    private static void AnimateOpacity(UIElement element, double target,
        double durationMs = 200)
    {
        var anim = new DoubleAnimation
        {
            To             = target,
            Duration       = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, element);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    // ── Pointer / mouse-move: show chrome ─────────────────────────────────────

    private void Page_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (DebugKeepChromeVisible)
            return;

        Point pos = e.GetCurrentPoint(this).Position;

        // Ignore synthetic / duplicate move events with no actual pointer movement.
        if (_lastPagePointerPosition is Point last)
        {
            const double epsilon = 0.1;
            if (Math.Abs(pos.X - last.X) < epsilon && Math.Abs(pos.Y - last.Y) < epsilon)
                return;
        }

        _lastPagePointerPosition = pos;
        ShowChrome();
    }

    private void Page_GotFocus(object sender, RoutedEventArgs e)
        => ShowChrome();

    private void FullscreenAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullscreen();
    }

    // ── Chrome element pointer events (pause/resume hide timer) ────────────────

    private void ChromeElement_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (DebugKeepChromeVisible)
            return;

        _chromePointerCount++;
        _hideTimer.Stop();
        _logger.LogDebug("Chrome PointerEntered: count={Count}, timer stopped", _chromePointerCount);
    }

    private void ChromeElement_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (DebugKeepChromeVisible)
            return;

        _chromePointerCount = Math.Max(0, _chromePointerCount - 1);
        _logger.LogDebug("Chrome PointerExited: count={Count}", _chromePointerCount);

        // When pointer leaves Chrome and count reaches 0, restart the hide timer
        if (_chromePointerCount == 0)
            RestartHideTimer();
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

    // ── Filmstrip ─────────────────────────────────────────────────────────────

    private void FilmStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ignore selection changes while dragging
        if (_filmstripDragging) return;
        if (_suppressFilmStripChange) return;
        if (FilmStrip.SelectedIndex < 0) return;
        _ = ViewModel.NavigateToIndexAsync(FilmStrip.SelectedIndex, _cts.Token);
    }

    private void SyncFilmStripSelection()
    {
        _suppressFilmStripChange = true;
        try
        {
            FilmStrip.SelectedIndex = ViewModel.CurrentIndex;
            FilmStrip.ScrollIntoView(FilmStrip.SelectedItem);
        }
        finally
        {
            _suppressFilmStripChange = false;
        }
    }

    // ── Filmstrip lazy-loading (load thumbnails on demand) ────────────────────

    private void FilmStrip_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        int index = args.ItemIndex;
        if (index < 0 || index >= ViewModel.FilmStripItems.Count)
            return;

        // Only load if not already loaded
        lock (_filmstripLoadLock)
        {
            if (_loadedFilmstripIndices.Contains(index))
                return;
            _loadedFilmstripIndices.Add(index);
        }

        _logger.LogInformation("Filmstrip lazy-loading started for index {Index}", index);

        // Load the thumbnail for this item asynchronously
        _ = LoadFilmstripThumbnailAsync(index);
    }

    private async Task LoadFilmstripThumbnailAsync(int index)
    {
        try
        {
            if (index < 0 || index >= ViewModel.FilmStripItems.Count)
                return;

            var item = ViewModel.FilmStripItems[index];
            if (!string.IsNullOrEmpty(item.ThumbPath))
                return;

            var photo = item.Photo;
            if (photo == null || string.IsNullOrEmpty(photo.FilePath))
                return;

            if (!File.Exists(photo.FilePath))
                return;

            // Synthetic photos (opened directly, not yet indexed) have Id == 0.
            // Skip the thumbnail service for them to avoid foreign-key confusion;
            // their filmstrip slot will remain blank.
            if (photo.Id == 0)
                return;

            var thumbnail = App.Current.Services.GetRequiredService<ThumbnailService>();
            var thumbPath = await Task.Run(
                () => thumbnail.GetOrCreateThumbnailAsync(photo, _cts.Token),
                _cts.Token);

            if (thumbPath is not null && index < ViewModel.FilmStripItems.Count)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (index < ViewModel.FilmStripItems.Count)
                    {
                        ViewModel.FilmStripItems[index].ThumbPath = thumbPath;
                        _logger.LogDebug("Filmstrip lazy-loading completed for index {Index}", index);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Filmstrip thumb load failed for index {Index}", index);
        }
    }

    // ── Filmstrip drag-scroll (mouse + touch) ──────────────────────────────────

    private void FilmStrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only capture for non-touch pointer (mouse, pen)
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            return;

        var pt = e.GetCurrentPoint(FilmStrip);
        // Check if left mouse button or pen is pressed
        if (!pt.Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen)
            return;

        _filmstripDragStart = pt.Position;
        _filmstripLastX = _filmstripDragStart.X;
        _filmstripDragging = false;
        _filmstripPointerCaptured = false;
    }

    private void FilmStrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            return;

        var pt = e.GetCurrentPoint(FilmStrip);
        
        // If not pressed, stop dragging
        if (!pt.Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen)
        {
            if (_filmstripDragging)
            {
                _filmstripDragging = false;
                if (_filmstripPointerCaptured)
                {
                    FilmStrip.ReleasePointerCapture(e.Pointer);
                    _filmstripPointerCaptured = false;
                }
            }
            return;
        }

        double currentX = pt.Position.X;
        double delta = currentX - _filmstripLastX;

        // Start dragging if movement exceeds threshold
        if (!_filmstripDragging && Math.Abs(currentX - _filmstripDragStart.X) > 5)
        {
            _filmstripDragging = true;
            _filmstripPointerCaptured = FilmStrip.CapturePointer(e.Pointer);
        }

        if (_filmstripDragging && Math.Abs(delta) > 0.5)
        {
            var scrollViewer = FindScrollViewer(FilmStrip);
            if (scrollViewer != null)
            {
                double newOffset = scrollViewer.HorizontalOffset - delta;
                newOffset = Math.Max(0, Math.Min(newOffset, scrollViewer.ScrollableWidth));
                scrollViewer.ChangeView(newOffset, null, null, disableAnimation: true);
            }
            e.Handled = true;
        }

        _filmstripLastX = currentX;
    }

    private void FilmStrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_filmstripDragging)
        {
            e.Handled = true;
        }

        _filmstripDragging = false;
        if (_filmstripPointerCaptured)
        {
            FilmStrip.ReleasePointerCapture(e.Pointer);
            _filmstripPointerCaptured = false;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

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

    // ── Toast helpers ─────────────────────────────────────────────────────────

    private void ShowToast(string message, ToastKind kind, bool showUndo)
    {
        _toastTimer.Stop();

        ToastText.Text = message;

        if (kind == ToastKind.Error)
        {
            ToastCard.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xC0, 0x20, 0x20));
            ToastIcon.Visibility = Visibility.Visible;
        }
        else
        {
            ToastCard.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x20));
            ToastIcon.Visibility = Visibility.Collapsed;
        }

        ToastUndoButton.Visibility = showUndo ? Visibility.Visible : Visibility.Collapsed;
        ToastHost.IsHitTestVisible = showUndo;

        // Dynamically calculate toast margin based on BottomToolbar height + FilmStripRow height
        double toolbarHeight = BottomToolbar.ActualHeight > 0 ? BottomToolbar.ActualHeight : 52;
        double toolbarBottomMargin = 20; // from BottomToolbar Margin="0,0,0,20"
        double filmstripHeight = FilmStripRow.ActualHeight;
        double spacing = 10; // additional spacing above toolbar
        double bottomMargin = filmstripHeight + toolbarHeight + toolbarBottomMargin + spacing;
        
        ToastHost.Margin = new Thickness(0, 0, 0, bottomMargin);

        AnimateOpacity(ToastHost, 1.0, durationMs: 180);
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        AnimateOpacity(ToastHost, 0.0, durationMs: 250);
        ToastHost.IsHitTestVisible = false;
    }

    private void Toolbar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_isFullscreen)
            return;

        e.Handled = true;
        ExitFullscreen();
    }

    // ── Fullscreen ────────────────────────────────────────────────────────────

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else               EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        var appWindow = GetAppWindow();
        if (appWindow is null)
            return;

        _wasMaximizedBeforeFullscreen =
            appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };

        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        _isFullscreen = true;
        FullscreenButton.IsChecked = true;
    }

    private void ExitFullscreen()
    {
        var appWindow = GetAppWindow();
        if (appWindow is null)
            return;

        appWindow.SetPresenter(AppWindowPresenterKind.Default);

        if (_wasMaximizedBeforeFullscreen && appWindow.Presenter is OverlappedPresenter overlappedPresenter)
            overlappedPresenter.Restore();

        _isFullscreen = false;
        _wasMaximizedBeforeFullscreen = false;
        FullscreenButton.IsChecked = false;
    }

    private AppWindow? GetAppWindow()
    {
        var hwnd     = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    // ── Counter text ──────────────────────────────────────────────────────────

    private void UpdateCounterText()
    {
        int total = ViewModel.FilmStripItems.Count;
        CounterText.Text = total > 0
            ? $"{ViewModel.CurrentIndex + 1} / {total}"
            : string.Empty;
    }
}
