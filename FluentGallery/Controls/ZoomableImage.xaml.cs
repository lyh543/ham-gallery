using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FluentGallery.Controls;

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

    private float _fitZoom        = 1f;
    private bool  _isAt100Percent = false;

    // Tracks the disposable source currently displayed (SoftwareBitmapSource).
    // BitmapImage (GIF) is not IDisposable so _currentDisposable stays null for those.
    private IDisposable? _currentDisposable;

    // ── Swipe-to-navigate events (fired when at fit-zoom + horizontal swipe) ─

    /// <summary>Raised when the user swipes left (towards next photo) at fit zoom.</summary>
    public event Action? SwipeLeft;

    /// <summary>Raised when the user swipes right (towards previous photo) at fit zoom.</summary>
    public event Action? SwipeRight;

    // ── Zoom slider state ─────────────────────────────────────────────────────

    // Slider value: percentage relative to fit zoom (100 = fit). Always multiple of 5, 25–1000.
    private int  _sliderValue        = 100;
    private bool _sliderVisible      = false;
    private bool _ignoreSliderChange = false;

    // Timestamp of last programmatic FitToWindow call.
    // ViewChanged within 300 ms of this is considered programmatic and won’t notify ZoomUserChanged.
    private DateTime _fitToWindowTime = DateTime.MinValue;

    /// <summary>Raised when the user actively changes the zoom (pinch, wheel, button, slider).
    /// PhotoDetailPage subscribes and calls ShowChrome(), which in turn calls ShowZoomSlider().</summary>
    public event Action? ZoomUserChanged;

    // Swipe tracking (AddHandler on ScrollViewer to receive already-handled events)
    private Point _swipeStart;
    private bool  _swipeTracking = false;

    /// <summary>True when the image is at or very close to its fit-to-window zoom level.</summary>
    public bool IsAtFitZoom =>
        Math.Abs(Scroll.ZoomFactor - _fitZoom) < 0.05f;

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

        // Use AddHandler(handledEventsToo: true) so we receive pointer events
        // even when the inner ScrollViewer marks them as handled.
        Scroll.AddHandler(PointerPressedEvent,
            new PointerEventHandler(OnScrollPointerPressed), handledEventsToo: true);
        Scroll.AddHandler(PointerReleasedEvent,
            new PointerEventHandler(OnScrollPointerReleased), handledEventsToo: true);
        Scroll.AddHandler(PointerCanceledEvent,
            new PointerEventHandler(OnScrollPointerCanceled), handledEventsToo: true);
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
            // SoftwareBitmapSource is already decoded — show immediately.
            MainImage.Source  = image.Source;
            MainImage.Width   = image.PixelWidth;
            MainImage.Height  = image.PixelHeight;
            _isAt100Percent   = false;
            FitToWindow();
            HideLoading();
            FadeInImage();
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
            _isAt100Percent  = false;
            FitToWindow();
            HideLoading();
            FadeInImage();
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

    /// <summary>Scales the image so it fits entirely within the current viewport.</summary>
    public void FitToWindow()
    {
        double imgW = MainImage.Width;
        double imgH = MainImage.Height;

        if (!double.IsFinite(imgW) || imgW <= 0 || !double.IsFinite(imgH) || imgH <= 0) return;

        double vpW = Scroll.ViewportWidth;
        double vpH = Scroll.ViewportHeight;
        if (vpW <= 0 || vpH <= 0) return;

        _fitZoom        = Math.Clamp((float)Math.Min(vpW / imgW, vpH / imgH), 0.1f, 10f);
        _isAt100Percent = false;

        // Record time so ViewChanged events shortly after are treated as programmatic.
        _fitToWindowTime = DateTime.UtcNow;

        Scroll.ChangeView(null, null, _fitZoom, disableAnimation: true);
        CentreViewport(imgW * _fitZoom, imgH * _fitZoom);
    }

    // ── Double-tap to toggle zoom ─────────────────────────────────────────────

    private void MainImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_isAt100Percent)
        {
            FitToWindow();
        }
        else
        {
            // Zoom to 100 % and centre on the tapped point
            var pt = e.GetPosition(Scroll);
            double offX = pt.X / Scroll.ZoomFactor - Scroll.ViewportWidth  / 2.0;
            double offY = pt.Y / Scroll.ZoomFactor - Scroll.ViewportHeight / 2.0;
            Scroll.ChangeView(offX, offY, 1.0f);
            _isAt100Percent = true;
        }
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
        float newZoom  = Math.Clamp(Scroll.ZoomFactor * factor, 0.1f, 10f);

        // Zoom around the pointer position so the point under the cursor stays fixed.
        var pos = e.GetCurrentPoint(Scroll).Position;
        double offX = (Scroll.HorizontalOffset + pos.X) / Scroll.ZoomFactor * newZoom - pos.X;
        double offY = (Scroll.VerticalOffset   + pos.Y) / Scroll.ZoomFactor * newZoom - pos.Y;

        Scroll.ChangeView(offX, offY, newZoom);
        _isAt100Percent = Math.Abs(newZoom - 1.0f) < 0.01f;
    }

    // ── Touch swipe-to-navigate ───────────────────────────────────────────────

    private void OnScrollPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch) return;
        _swipeStart    = e.GetCurrentPoint(Scroll).Position;
        _swipeTracking = true;
    }

    private void OnScrollPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_swipeTracking) return;
        _swipeTracking = false;

        // Only handle swipe when at fit zoom — zoomed-in panning is handled by ScrollViewer.
        if (!IsAtFitZoom) return;

        var end = e.GetCurrentPoint(Scroll).Position;
        double dx = end.X - _swipeStart.X;
        double dy = end.Y - _swipeStart.Y;

        const double MinSwipe = 60.0;
        if (Math.Abs(dx) < MinSwipe || Math.Abs(dx) < Math.Abs(dy) * 1.5) return;

        if (dx < 0) SwipeLeft?.Invoke();
        else         SwipeRight?.Invoke();
    }

    private void OnScrollPointerCanceled(object sender, PointerRoutedEventArgs e)
        => _swipeTracking = false;

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
        if (!_isAt100Percent)
            FitToWindow();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void CentreViewport(double contentW, double contentH)
    {
        double offX = Math.Max(0, (contentW  - Scroll.ViewportWidth)  / 2.0);
        double offY = Math.Max(0, (contentH - Scroll.ViewportHeight) / 2.0);
        Scroll.ChangeView(offX, offY, null, disableAnimation: true);
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

    private static int ClampZoomPercent(int v) => Math.Clamp(v, 25, 1000);

    private static int ZoomOutStep(int current)
    {
        double raw = current / 1.25;
        return ClampZoomPercent((int)Math.Floor(raw / 5.0) * 5);
    }

    private static int ZoomInStep(int current)
    {
        double raw = current * 1.25;
        return ClampZoomPercent((int)Math.Ceiling(raw / 5.0) * 5);
    }

    private void ApplyZoomPercent(int pct)
    {
        if (_fitZoom <= 0) return;
        float newZoom = Math.Clamp((float)(_fitZoom * pct / 100.0), 0.1f, 10f);

        double cx  = Scroll.HorizontalOffset + Scroll.ViewportWidth  / 2.0;
        double cy  = Scroll.VerticalOffset   + Scroll.ViewportHeight / 2.0;
        double offX = cx / Scroll.ZoomFactor * newZoom - Scroll.ViewportWidth  / 2.0;
        double offY = cy / Scroll.ZoomFactor * newZoom - Scroll.ViewportHeight / 2.0;

        Scroll.ChangeView(offX, offY, newZoom);
        _isAt100Percent = false;
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
