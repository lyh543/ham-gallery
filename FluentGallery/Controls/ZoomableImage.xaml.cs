using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Linq;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FluentGallery.Controls;

public sealed class SwipePreviewEventArgs(double horizontalOffset, double viewportWidth)
{
    public double HorizontalOffset { get; } = horizontalOffset;
    public double ViewportWidth { get; } = viewportWidth;
}

/// <summary>
/// A UserControl that wraps <see cref="ScrollViewer"/> + <see cref="Image"/> to provide:
/// <list type="bullet">
///   <item>Pinch-to-zoom (built-in via ScrollViewer ZoomMode="Enabled")</item>
///   <item>Mouse-wheel zoom (handled here; no Ctrl required when cursor is over the image)</item>
///   <item>Double-tap: toggle between "fit to window" and 100 % (original size)</item>
///   <item>Asynchronous image display via <see cref="SetSource"/></item>
///   <item>Visual rotation via the <see cref="RotationAngle"/> dependency property</item>
/// </list>
/// </summary>
public sealed partial class ZoomableImage : UserControl
{
    // ── State ────────────────────────────────────────────────────────────────

    private float _fitZoom = 1f;
    private readonly ILogger<ZoomableImage> _logger =
        App.Current.Services.GetRequiredService<ILogger<ZoomableImage>>();

    // Tracks the disposable source currently displayed (SoftwareBitmapSource).
    // BitmapImage (GIF) is not IDisposable so _currentDisposable stays null for those.
    private IDisposable? _currentDisposable;

    // ── Swipe-to-navigate events (fired when at fit-zoom + horizontal swipe) ─

    /// <summary>Raised when the user swipes left (towards next photo) at fit zoom.</summary>
    public event Action? SwipeLeft;

    /// <summary>Raised when the user swipes right (towards previous photo) at fit zoom.</summary>
    public event Action? SwipeRight;

    /// <summary>Raised while the user drags horizontally to preview an adjacent photo.</summary>
    public event Action<SwipePreviewEventArgs>? SwipePreviewProgress;

    /// <summary>Raised when horizontal drag preview ends and the host should reset transforms.</summary>
    public event Action? SwipePreviewCompleted;

    // ── Zoom slider state ─────────────────────────────────────────────────────

    // Slider value: percentage relative to fit zoom (100 = fit). Always multiple of 5, 25–1000.
    private int  _sliderValue        = 100;
    private bool _sliderVisible      = false;
    private bool _ignoreSliderChange = false;

    // Dynamic minimum percentage — depends on image size.
    // WinUI 3 MinZoomFactor = 0.1; for large images _fitZoom * 25% < 0.1, so 25 is unreachable.
    private int _minPercentage = 25;

    // Incremented on every SetSource call so stale FitAfterLayout callbacks can be discarded.
    private int _sourceGeneration = 0;

    // Timestamp of last programmatic FitToWindow call.
    // ViewChanged within 300 ms of this is considered programmatic and won’t notify ZoomUserChanged.
    private DateTime _fitToWindowTime = DateTime.MinValue;

    /// <summary>Raised when the user actively changes the zoom (pinch, wheel, button, slider).
    /// PhotoDetailPage subscribes and calls ShowChrome(), which in turn calls ShowZoomSlider().</summary>
    public event Action? ZoomUserChanged;

    // Swipe tracking (AddHandler on ScrollViewer to receive already-handled events)
    private Point _swipeStart;
    private bool  _swipeTracking = false;
    private bool  _swipePreviewActive = false;
    private uint? _swipePointerId;

    // Mouse drag-to-pan when zoomed in.
    private bool _mouseDragging = false;
    private Point _mouseDragStart;
    private double _mouseDragStartHorizontalOffset;
    private double _mouseDragStartVerticalOffset;

    // Last known pointer position relative to Scroll, used as zoom anchor.
    private Point? _lastPointerPos;

    /// <summary>True when the displayed zoom percentage is 100 % (= fit-to-window).</summary>
    public bool IsAtFitZoom => _sliderValue is >= 80 and <= 125;

    // ── RotationAngle dependency property ───────────────────────────────────

    public static readonly DependencyProperty RotationAngleProperty =
        DependencyProperty.Register(
            nameof(RotationAngle),
            typeof(double),
            typeof(ZoomableImage),
            new PropertyMetadata(0.0, OnRotationAngleChanged));

    /// <summary>
    /// Clockwise visual rotation in degrees (0 / 90 / 180 / 270).
    /// Applied via a <see cref="Microsoft.UI.Xaml.Media.RotateTransform"/> on the image.
    /// </summary>
    public double RotationAngle
    {
        get => (double)GetValue(RotationAngleProperty);
        set => SetValue(RotationAngleProperty, value);
    }

    private static void OnRotationAngleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ZoomableImage ctrl)
            ctrl.ApplyRotation((double)e.NewValue);
    }

    private void ApplyRotation(double degrees)
    {
        MainImage.RenderTransformOrigin = new Point(0.5, 0.5);
        MainImage.RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform
        {
            Angle = degrees
        };
        // Re-fit after rotation to keep the image in view
        FitToWindow();
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    public ZoomableImage()
    {
        InitializeComponent();

        // Set Minimum/Maximum/Value here to avoid WinUI 3 XBF parser constraint
        // (Value=0 < Minimum=25 causes XamlParseException when set in XAML).
        ZoomSlider.Minimum = 25;
        ZoomSlider.Maximum = 1000;
        ZoomSlider.Value   = 100;

        Scroll.PointerWheelChanged += OnPointerWheelChanged;
        Scroll.SizeChanged         += OnScrollSizeChanged;
        Scroll.ViewChanged         += OnScrollViewChanged;
        Scroll.PointerMoved += (_, e) => _lastPointerPos = e.GetCurrentPoint(Scroll).Position;
        Scroll.PointerExited += (_, _) =>
        {
            _lastPointerPos = null;
        };

        // Use AddHandler(handledEventsToo: true) so we receive pointer events
        // even when the inner ScrollViewer marks them as handled.
        Scroll.AddHandler(PointerPressedEvent,
            new PointerEventHandler(OnScrollPointerPressed), handledEventsToo: true);
        Scroll.AddHandler(PointerMovedEvent,
            new PointerEventHandler(OnScrollPointerMoved), handledEventsToo: true);
        Scroll.AddHandler(PointerReleasedEvent,
            new PointerEventHandler(OnScrollPointerReleased), handledEventsToo: true);
        Scroll.AddHandler(PointerCanceledEvent,
            new PointerEventHandler(OnScrollPointerCanceled), handledEventsToo: true);
        Scroll.AddHandler(DoubleTappedEvent,
            new DoubleTappedEventHandler(MainImage_DoubleTapped), handledEventsToo: true);
    }

    // ── Deferred disposal ─────────────────────────────────────────────────────

    /// <summary>
    /// Defers <see cref="IDisposable.Dispose"/> of a <see cref="SoftwareBitmapSource"/>
    /// to the next message-loop iteration.
    /// <para>
    /// Setting <c>MainImage.Source = null</c> batches the property change — the compositor
    /// may still be rendering with the old surface during the current frame.  Disposing
    /// immediately frees the GPU texture while the compositor still references it, causing
    /// a native access-violation crash.  Enqueueing the dispose to the next iteration
    /// guarantees a layout/render pass has run and the compositor has released the surface.
    /// </para>
    /// </summary>
    private void DeferDispose(IDisposable? disposable)
    {
        if (disposable is null) return;
        // Double-enqueue: two Low-priority iterations give the compositor time to
        // process Source=null and stop referencing the GPU surface before we release it.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try { disposable.Dispose(); }
                catch { /* SoftwareBitmapSource.Dispose can stow native exceptions */ }
            }));
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Clears the current image and shows the loading indicator immediately.
    /// Releases GPU memory of the previous <see cref="SoftwareBitmapSource"/> after the
    /// compositor has stopped using it (deferred to next message-loop iteration).
    /// </summary>
    public void SetLoading()
    {
        MainImage.Source  = null;
        MainImage.Width   = 0;
        MainImage.Height  = 0;
        MainImage.Opacity = 0;
        DeferDispose(_currentDisposable);
        _currentDisposable = null;
        ShowLoading();
    }

    /// <summary>
    /// Displays a <see cref="LoadedImage"/> and fits it to the viewport.
    /// <list type="bullet">
    ///   <item>If <see cref="Loaders.LoadedImage.PixelWidth"/> &gt; 0 (SoftwareBitmapSource):
    ///     shows immediately.</item>
    ///   <item>If <see cref="Loaders.LoadedImage.PixelWidth"/> == 0 (BitmapImage / GIF):
    ///     attaches <c>ImageOpened</c> / <c>ImageFailed</c> handlers and shows when decoded.</item>
    /// </list>
    /// Disposes the previous <see cref="SoftwareBitmapSource"/> before showing the new image.
    /// Must be called from the UI thread.
    /// </summary>
    public void SetSource(Loaders.LoadedImage image, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Clear Source so the compositor stops using the old surface.
        MainImage.Opacity = 0;
        MainImage.Source  = null;
        DeferDispose(_currentDisposable);
        _currentDisposable = image.Source as IDisposable;
        ShowLoading();

        if (image.PixelWidth > 0)
        {
            // SoftwareBitmapSource is already decoded — dimensions are known immediately.
            MainImage.Source  = image.Source;
            MainImage.Width   = image.PixelWidth;
            MainImage.Height  = image.PixelHeight;
            // Step 1: establish _fitZoom↔slider mapping now (no layout needed for math).
            EstablishFitZoom(image.PixelWidth, image.PixelHeight);
            // Step 2: apply ChangeView after layout commits the new content size.
            FitAfterLayout();
            return;
        }

        // BitmapImage / GIF: still decoding in background — wait for ImageOpened.
        MainImage.Source = image.Source;

        if (image.Source is not BitmapImage bitmap) return;

        RoutedEventHandler?          onOpened = null;
        ExceptionRoutedEventHandler? onFailed = null;

        onOpened = (_, _) =>
        {
            bitmap.ImageOpened -= onOpened;
            bitmap.ImageFailed -= onFailed;
            MainImage.Width  = bitmap.PixelWidth;
            MainImage.Height = bitmap.PixelHeight;
            EstablishFitZoom(bitmap.PixelWidth, bitmap.PixelHeight);
            FitAfterLayout();
        };
        onFailed = (_, _) =>
        {
            bitmap.ImageOpened -= onOpened;
            bitmap.ImageFailed -= onFailed;
            HideLoading();
        };

        bitmap.ImageOpened += onOpened;
        bitmap.ImageFailed += onFailed;
    }

    /// <summary>
    /// Immediately establishes the <see cref="_fitZoom"/>↔slider mapping from image dimensions
    /// and the current viewport size.  Does NOT call ChangeView — that requires layout to have
    /// committed the new content size first (see <see cref="FitAfterLayout"/>).
    /// Updates the slider to 100 % instantly so the UI always reflects the correct fit state.
    /// </summary>
    private void EstablishFitZoom(double imgW, double imgH)
    {
        double vpW = Scroll.ViewportWidth;
        double vpH = Scroll.ViewportHeight;
        if (vpW <= 0 || vpH <= 0 || imgW <= 0 || imgH <= 0) return;

        _fitZoom = Math.Clamp((float)Math.Min(vpW / imgW, vpH / imgH), 0.1f, 10f);

        _fitToWindowTime = DateTime.UtcNow;
        _sliderValue     = 100;

        UpdateZoomBounds();

        _ignoreSliderChange = true;
        ZoomSlider.Value     = 100;
        ZoomPercentText.Text = "100%";
        _ignoreSliderChange = false;

    }

    /// <summary>
    /// Recomputes slider bounds and native ScrollViewer zoom bounds from the current fit zoom.
    /// Keeps the 1000 % logical cap and the native pinch/gesture cap in sync.
    /// </summary>
    private void UpdateZoomBounds()
    {
        int rawMin = (int)Math.Ceiling(0.1 / _fitZoom * 100.0 / 5.0) * 5;
        _minPercentage = Math.Max(25, rawMin);

        Scroll.MinZoomFactor = 0.1f;
        Scroll.MaxZoomFactor = GetMaxAllowedZoomFactor();

        _ignoreSliderChange = true;
        ZoomSlider.Minimum = _minPercentage;
        _ignoreSliderChange = false;
    }

    private void FitAfterLayout()
    {
        int gen = ++_sourceGeneration;

        // ChangeView is silently discarded by WinUI 3 when ScrollViewer.ExtentWidth/Height
        // is 0 (content not yet laid out). LayoutUpdated fires after every layout pass;
        // we wait until Extent > 0 before calling FitToWindow/ChangeView. The generation
        // counter discards stale callbacks when the user navigates quickly.
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (_sourceGeneration != gen) { Scroll.LayoutUpdated -= handler; return; }
            if (Scroll.ExtentWidth <= 0 || Scroll.ExtentHeight <= 0) return;
            Scroll.LayoutUpdated -= handler;
            FitToWindow();
            HideLoading();
            FadeInImage();
        };
        Scroll.LayoutUpdated += handler;
    }

    /// <summary>Scales the image so it fits entirely within the current viewport.</summary>
    public void FitToWindow()
    {
        double imgW = MainImage.Width;
        double imgH = MainImage.Height;

        if (!double.IsFinite(imgW) || imgW <= 0 || !double.IsFinite(imgH) || imgH <= 0) return;

        double vpW = Scroll.ViewportWidth;
        double vpH = Scroll.ViewportHeight;
        if (vpW <= 0 || vpH <= 0) return;

        _fitZoom = Math.Clamp((float)Math.Min(vpW / imgW, vpH / imgH), 0.1f, 10f);

        // Compute the true minimum percentage for this image.
        // Scroll.MinZoomFactor = 0.1 (set in XAML); for large images _fitZoom * 25% can be < 0.1.
        UpdateZoomBounds();

        // Record time so ViewChanged events shortly after are treated as programmatic.
        _fitToWindowTime = DateTime.UtcNow;

        // Single ChangeView call — zoom + center in one atomic call.
        Scroll.ChangeView(0, 0, _fitZoom, disableAnimation: true);

        // Eagerly mark as fit zoom so IsAtFitZoom is correct before ViewChanged fires
        // (ChangeView is async in WinUI 3; ViewChanged may not fire until next frame).
        _sliderValue = 100;
    }

    // ── Double-tap to toggle zoom ─────────────────────────────────────────────

    /// <summary>
    /// When content is smaller than the viewport, WinUI 3 ScrollViewer auto-centers it.
    /// This centering shift must be subtracted before converting viewport ↔ content coords.
    /// </summary>
    private Point GetCenterOffset() => new(
        Math.Max(0.0, (Scroll.ViewportWidth  - MainImage.Width  * Scroll.ZoomFactor) / 2.0),
        Math.Max(0.0, (Scroll.ViewportHeight - MainImage.Height * Scroll.ZoomFactor) / 2.0));

    private void MainImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        Point inScroll = e.GetPosition(Scroll);

        if (IsAtFitZoom)
        {
            ApplyZoomPercentAroundPoint(200, inScroll);
        }
        else
        {
            FitToWindow();
        }
        ZoomUserChanged?.Invoke();
    }

    private void ApplyZoomPercentAroundPoint(int pct, Point anchor)
    {
        if (_fitZoom <= 0) return;
        pct = ClampZoomPercent(pct);
        float newZoom = Math.Clamp((float)(_fitZoom * pct / 100.0), 0.1f, GetMaxAllowedZoomFactor());
        ZoomAroundViewportPoint(anchor, newZoom);
        // Eagerly update so IsAtFitZoom reflects intent before ViewChanged fires.
        _sliderValue = pct;
    }

    // ── Mouse-wheel zoom / navigation ────────────────────────────────────────

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool isCtrl   = ctrlState.HasFlag(CoreVirtualKeyStates.Down);

        if (!isCtrl)
        {
            // Without Ctrl at fit zoom: navigate to next/previous photo.
            // When zoomed in the ScrollViewer handles scrolling normally.
            if (IsAtFitZoom)
            {
                e.Handled = true;
                var props = e.GetCurrentPoint(Scroll).Properties;
                if (props.MouseWheelDelta < 0) SwipeLeft?.Invoke();   // scroll down → next
                else                           SwipeRight?.Invoke();  // scroll up  → prev
            }
            return;
        }

        // Ctrl + wheel → zoom
        e.Handled = true;

        var wheelProps = e.GetCurrentPoint(Scroll).Properties;
        float factor   = wheelProps.MouseWheelDelta > 0 ? 1.15f : 1f / 1.15f;
        float maxZoom  = GetMaxAllowedZoomFactor();
        float newZoom  = Math.Clamp(Scroll.ZoomFactor * factor, 0.1f, maxZoom);

        if (Math.Abs(newZoom - Scroll.ZoomFactor) < 0.0001f)
            return;

        ZoomAroundViewportPoint(e.GetCurrentPoint(Scroll).Position, newZoom);
    }

    // ── Touch swipe-to-navigate ───────────────────────────────────────────────

    private void OnScrollPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Scroll);
        _lastPointerPos = point.Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _swipeStart         = point.Position;
            _swipeTracking      = true;
            _swipePreviewActive = false;
            _swipePointerId     = e.Pointer.PointerId;
            _logger.LogDebug(
                "Swipe touch pressed: pointer={PointerId}, pos=({X:F1},{Y:F1}), zoom={Zoom:F3}, fit={Fit:F3}, slider={Slider}",
                _swipePointerId,
                _swipeStart.X,
                _swipeStart.Y,
                Scroll.ZoomFactor,
                _fitZoom,
                _sliderValue);
            Scroll.CapturePointer(e.Pointer);
            return;
        }

        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;

        if (!point.Properties.IsLeftButtonPressed || Scroll.ZoomFactor <= _fitZoom + 0.01f)
            return;

        _mouseDragging = true;
        _mouseDragStart = point.Position;
        _mouseDragStartHorizontalOffset = Scroll.HorizontalOffset;
        _mouseDragStartVerticalOffset   = Scroll.VerticalOffset;
        Scroll.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnScrollPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Scroll);
        _lastPointerPos = point.Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch && _swipeTracking && _swipePointerId == e.Pointer.PointerId)
        {
            if (!IsAtFitZoom)
            {
                if (_swipePreviewActive)
                {
                    _logger.LogDebug(
                        "Swipe preview cancelled because zoom is not at fit: pointer={PointerId}, zoom={Zoom:F3}, fit={Fit:F3}, slider={Slider}",
                        _swipePointerId,
                        Scroll.ZoomFactor,
                        _fitZoom,
                        _sliderValue);
                    SwipePreviewCompleted?.Invoke();
                    _swipePreviewActive = false;
                }
            }
            else
            {
                double dx = point.Position.X - _swipeStart.X;
                double dy = point.Position.Y - _swipeStart.Y;

                const double HorizontalIntentThreshold = 12.0;
                if (_swipePreviewActive || (Math.Abs(dx) >= HorizontalIntentThreshold && Math.Abs(dx) > Math.Abs(dy)))
                {
                    bool startedPreview = !_swipePreviewActive;
                    _swipePreviewActive = true;
                    if (startedPreview)
                    {
                        _logger.LogDebug(
                            "Swipe preview started: pointer={PointerId}, dx={Dx:F1}, dy={Dy:F1}, viewport={Viewport:F1}",
                            _swipePointerId,
                            dx,
                            dy,
                            Scroll.ActualWidth);
                    }
                    SwipePreviewProgress?.Invoke(new SwipePreviewEventArgs(dx, Scroll.ActualWidth));
                    e.Handled = true;
                }
            }
        }

        if (!_mouseDragging)
            return;

        double dxMouse = point.Position.X - _mouseDragStart.X;
        double dyMouse = point.Position.Y - _mouseDragStart.Y;

        Scroll.ChangeView(
            _mouseDragStartHorizontalOffset - dxMouse,
            _mouseDragStartVerticalOffset - dyMouse,
            null,
            disableAnimation: true);

        e.Handled = true;
    }

    private void OnScrollPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndMouseDrag(e.Pointer.PointerId);

        if (_swipePointerId == e.Pointer.PointerId)
        {
            ReleaseSwipePointerCapture(e.Pointer.PointerId);

            if (_swipePreviewActive)
            {
                _logger.LogDebug("Swipe preview completed: pointer={PointerId}", _swipePointerId);
                SwipePreviewCompleted?.Invoke();
                _swipePreviewActive = false;
            }
        }

        if (!_swipeTracking || _swipePointerId != e.Pointer.PointerId) return;
        _swipeTracking = false;
        _swipePointerId = null;

        // Only handle swipe when at fit zoom — zoomed-in panning is handled by ScrollViewer.
        if (!IsAtFitZoom)
        {
            _logger.LogDebug(
                "Swipe release ignored because not at fit zoom: zoom={Zoom:F3}, fit={Fit:F3}, slider={Slider}",
                Scroll.ZoomFactor,
                _fitZoom,
                _sliderValue);
            return;
        }

        var end = e.GetCurrentPoint(Scroll).Position;
        double dx = end.X - _swipeStart.X;
        double dy = end.Y - _swipeStart.Y;

        _logger.LogDebug(
            "Swipe released: dx={Dx:F1}, dy={Dy:F1}, minSwipe={MinSwipe}, pointer={PointerId}",
            dx,
            dy,
            60.0,
            e.Pointer.PointerId);

        const double MinSwipe = 60.0;
        if (Math.Abs(dx) < MinSwipe || Math.Abs(dx) < Math.Abs(dy) * 1.5)
        {
            _logger.LogDebug("Swipe rejected: horizontal threshold not met");
            return;
        }

        if (dx < 0)
        {
            _logger.LogDebug("Swipe accepted: trigger next photo");
            SwipeLeft?.Invoke();
        }
        else
        {
            _logger.LogDebug("Swipe accepted: trigger previous photo");
            SwipeRight?.Invoke();
        }
    }

    private void OnScrollPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_swipePointerId == e.Pointer.PointerId)
        {
            if (_swipePreviewActive)
            {
                _logger.LogDebug("Swipe cancelled during preview: pointer={PointerId}", _swipePointerId);
                SwipePreviewCompleted?.Invoke();
                _swipePreviewActive = false;
            }
            ReleaseSwipePointerCapture(e.Pointer.PointerId);
            _swipeTracking = false;
            _swipePointerId = null;
        }

        EndMouseDrag(e.Pointer.PointerId);
    }

    private void ReleaseSwipePointerCapture(uint pointerId)
    {
        if (Scroll.PointerCaptures is null)
            return;

        var captured = Scroll.PointerCaptures.FirstOrDefault(c => c.PointerId == pointerId);
        if (captured is not null)
            Scroll.ReleasePointerCapture(captured);
    }

    private void EndMouseDrag(uint pointerId)
    {
        if (!_mouseDragging)
            return;

        _mouseDragging = false;

        if (Scroll.PointerCaptures is not null && Scroll.PointerCaptures.Any(c => c.PointerId == pointerId))
        {
            var captured = Scroll.PointerCaptures.First(c => c.PointerId == pointerId);
            Scroll.ReleasePointerCapture(captured);
        }
    }

    // ── ScrollViewer ViewChanged → update slider ──────────────────────────────

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Always keep slider display in sync.
        UpdateSliderValue();

        // Skip the final "settled" event for programmatic FitToWindow calls
        // (suppress window is 300 ms after FitToWindow was invoked).
        if (e.IsIntermediate) return;

        bool isProgrammatic = (DateTime.UtcNow - _fitToWindowTime).TotalMilliseconds < 300;
        if (!isProgrammatic)
            ZoomUserChanged?.Invoke();
    }

    // ── Viewport size change ──────────────────────────────────────────────────

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only re-fit when the user is currently at (or near) fit-zoom.
        // If the user has manually zoomed in (e.g. 200%), do NOT reset their zoom on resize.
        if (IsAtFitZoom)
            FitToWindow();
    }

    // ── Loading indicator ─────────────────────────────────────────────────────

    private void ShowLoading()
    {
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive   = true;
    }

    private void HideLoading()
    {
        LoadingRing.IsActive   = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    // ── Image fade-in ─────────────────────────────────────────────────────────

    private void FadeInImage()
    {
        var anim = new DoubleAnimation
        {
            From           = 0.0,
            To             = 1.0,
            Duration       = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, MainImage);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    // ── Zoom slider helpers ───────────────────────────────────────────────────

    private int ComputeZoomPercent()
    {
        if (_fitZoom <= 0) return 100;
        double raw = (double)Scroll.ZoomFactor / _fitZoom * 100.0;
        return ClampZoomPercent((int)Math.Round(raw / 5.0) * 5);
    }

    private int ClampZoomPercent(int v) => Math.Clamp(v, _minPercentage, 1000);

    private float GetMaxAllowedZoomFactor()
        => Math.Clamp(_fitZoom * 10f, 0.1f, 10f);

    private int ZoomOutStep(int current)
    {
        double raw = current / 1.25;
        return ClampZoomPercent((int)Math.Floor(raw / 5.0) * 5);
    }

    private static int ZoomInStep(int current)
    {
        double raw = current * 1.25;
        return Math.Clamp((int)Math.Ceiling(raw / 5.0) * 5, 25, 1000);
    }

    private void ApplyZoomPercent(int pct)
    {
        if (_fitZoom <= 0) return;
        pct = ClampZoomPercent(pct);
        float newZoom = Math.Clamp((float)(_fitZoom * pct / 100.0), 0.1f, GetMaxAllowedZoomFactor());

        Point anchor = _lastPointerPos ?? new(Scroll.ViewportWidth / 2.0, Scroll.ViewportHeight / 2.0);
        ZoomAroundViewportPoint(anchor, newZoom);
    }

    /// <summary>
    /// Core zoom primitive: zooms to <paramref name="newZoom"/> keeping the image content
    /// that is currently under <paramref name="anchor"/> (viewport-space) visually fixed.
    /// Accounts for WinUI 3 auto-centering of content smaller than the viewport.
    /// </summary>
    private void ZoomAroundViewportPoint(Point anchor, float newZoom)
    {
        newZoom = Math.Clamp(newZoom, 0.1f, GetMaxAllowedZoomFactor());

        // Image content under anchor before zoom (centering-corrected)
        var c = GetCenterOffset();
        double imgX = (Scroll.HorizontalOffset + anchor.X - c.X) / Scroll.ZoomFactor;
        double imgY = (Scroll.VerticalOffset   + anchor.Y - c.Y) / Scroll.ZoomFactor;

        // Centering offset that will apply after zoom
        double cxNew = Math.Max(0.0, (Scroll.ViewportWidth  - MainImage.Width  * newZoom) / 2.0);
        double cyNew = Math.Max(0.0, (Scroll.ViewportHeight - MainImage.Height * newZoom) / 2.0);

        // Required scroll offsets so anchor stays on the same image content
        double offX = imgX * newZoom - (anchor.X - cxNew);
        double offY = imgY * newZoom - (anchor.Y - cyNew);

        Scroll.ChangeView(offX, offY, newZoom);
    }

    private void UpdateSliderValue()
    {
        int pct = ComputeZoomPercent();
        _sliderValue = pct;
        _ignoreSliderChange = true;
        ZoomSlider.Value = pct;
        ZoomPercentText.Text = $"{pct}%";
        _ignoreSliderChange = false;
    }

    public void ShowZoomSlider()
    {
        if (_sliderVisible) return;
        _sliderVisible = true;

        var anim = new DoubleAnimation
        {
            To             = 1.0,
            Duration       = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, ZoomSliderContainer);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    public void HideZoomSlider()
    {
        _sliderVisible = false;

        var anim = new DoubleAnimation
        {
            To             = 0.0,
            Duration       = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, ZoomSliderContainer);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    // ── Zoom slider button handlers ───────────────────────────────────────────

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        int next = ZoomOutStep(_sliderValue);
        ApplyZoomPercent(next);
        ZoomUserChanged?.Invoke();
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        int next = ZoomInStep(_sliderValue);
        ApplyZoomPercent(next);
        ZoomUserChanged?.Invoke();
    }

    private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        FitToWindow();
        // Override the programmatic suppression so the slider shows after reset.
        _fitToWindowTime = DateTime.MinValue;
        UpdateSliderValue();
        ZoomUserChanged?.Invoke();
    }

    private void ZoomSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_ignoreSliderChange) return;

        // Round to nearest multiple of 5 (in case the slider lands between ticks).
        int pct = ClampZoomPercent((int)Math.Round(e.NewValue / 5.0) * 5);
        _sliderValue = pct;
        ApplyZoomPercent(pct);
        ZoomUserChanged?.Invoke();
    }
}
