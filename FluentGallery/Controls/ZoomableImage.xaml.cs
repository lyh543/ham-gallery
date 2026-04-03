using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
///   <item>Asynchronous image loading from a local file path</item>
///   <item>Visual rotation via the <see cref="RotationAngle"/> dependency property</item>
/// </list>
/// </summary>
public sealed partial class ZoomableImage : UserControl
{
    // ── State ────────────────────────────────────────────────────────────────

    private float _fitZoom        = 1f;
    private bool  _isAt100Percent = false;

    // ── Swipe-to-navigate events (fired when at fit-zoom + horizontal swipe) ─

    /// <summary>Raised when the user swipes left (towards next photo) at fit zoom.</summary>
    public event Action? SwipeLeft;

    /// <summary>Raised when the user swipes right (towards previous photo) at fit zoom.</summary>
    public event Action? SwipeRight;

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

        Scroll.PointerWheelChanged += OnPointerWheelChanged;
        Scroll.SizeChanged         += OnScrollSizeChanged;

        // Use AddHandler(handledEventsToo: true) so we receive pointer events
        // even when the inner ScrollViewer marks them as handled.
        Scroll.AddHandler(PointerPressedEvent,
            new PointerEventHandler(OnScrollPointerPressed), handledEventsToo: true);
        Scroll.AddHandler(PointerReleasedEvent,
            new PointerEventHandler(OnScrollPointerReleased), handledEventsToo: true);
        Scroll.AddHandler(PointerCanceledEvent,
            new PointerEventHandler(OnScrollPointerCanceled), handledEventsToo: true);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>The <see cref="BitmapImage"/> currently displayed (null if no image is loaded).</summary>
    public BitmapImage? CurrentBitmap { get; private set; }

    /// <summary>
    /// Loads the image at <paramref name="filePath"/> and fits it to the viewport.
    /// Decoding is performed in the background by WinUI (via UriSource); this method
    /// returns immediately so the page transition is not blocked by the codec.
    /// A <see cref="ProgressRing"/> is shown while decoding and hidden on completion.
    /// </summary>
    public Task LoadImageAsync(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        MainImage.Opacity = 0;
        MainImage.Source  = null;
        CurrentBitmap     = null;
        ShowLoading();

        try
        {
            var bmp = new BitmapImage();
            bmp.ImageOpened += (_, _) =>
            {
                // Set explicit size so the ScrollViewer knows the scrollable extent.
                MainImage.Width  = bmp.PixelWidth;
                MainImage.Height = bmp.PixelHeight;
                _isAt100Percent  = false;
                FitToWindow();
                HideLoading();
                FadeInImage();
            };
            bmp.ImageFailed += (_, _) => HideLoading();
            // UriSource triggers background decode without blocking the UI thread,
            // unlike SetSourceAsync which awaits full decode before returning.
            bmp.UriSource    = new Uri(filePath);
            MainImage.Source = bmp;
            CurrentBitmap    = bmp;
        }
        catch (OperationCanceledException) { HideLoading(); throw; }
        catch
        {
            MainImage.Source = null;
            HideLoading();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Displays a pre-loaded <see cref="BitmapImage"/> from the caller's cache.
    /// Falls back to path-based loading if the cached bitmap has no pixel data yet.
    /// </summary>
    public Task LoadImageFromCacheAsync(BitmapImage cached, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (cached.PixelWidth > 0)
        {
            // Already decoded — display immediately with a quick fade-in.
            MainImage.Opacity = 0;
            MainImage.Source  = cached;
            CurrentBitmap     = cached;
            MainImage.Width   = cached.PixelWidth;
            MainImage.Height  = cached.PixelHeight;
            _isAt100Percent   = false;
            FitToWindow();
            HideLoading();
            FadeInImage();
        }
        else
        {
            // Still decoding in background — show ring until ImageOpened fires.
            MainImage.Opacity = 0;
            MainImage.Source  = null;
            CurrentBitmap     = cached;
            ShowLoading();
            cached.ImageOpened += (_, _) =>
            {
                MainImage.Source = cached;
                MainImage.Width  = cached.PixelWidth;
                MainImage.Height = cached.PixelHeight;
                _isAt100Percent  = false;
                FitToWindow();
                HideLoading();
                FadeInImage();
            };
            cached.ImageFailed += (_, _) => HideLoading();
        }

        return Task.CompletedTask;
    }

    /// <summary>Scales the image so it fits entirely within the current viewport.</summary>
    public void FitToWindow()
    {
        if (MainImage.Source is not BitmapImage bmp || bmp.PixelWidth == 0) return;

        double vpW = Scroll.ViewportWidth;
        double vpH = Scroll.ViewportHeight;
        if (vpW <= 0 || vpH <= 0) return;

        float scaleX = (float)(vpW / bmp.PixelWidth);
        float scaleY = (float)(vpH / bmp.PixelHeight);
        _fitZoom        = Math.Clamp(Math.Min(scaleX, scaleY), 0.1f, 10f);
        _isAt100Percent = false;

        Scroll.ChangeView(null, null, _fitZoom, disableAnimation: true);
        // Centre the image
        CentreViewport(bmp.PixelWidth * _fitZoom, bmp.PixelHeight * _fitZoom);
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

    // ── Mouse-wheel zoom (Ctrl + scroll, per spec) ───────────────────────────

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Only intercept when Ctrl is held; otherwise let the ScrollViewer scroll normally.
        var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (!ctrlState.HasFlag(CoreVirtualKeyStates.Down))
            return;

        e.Handled = true;

        var props     = e.GetCurrentPoint(Scroll).Properties;
        float factor  = props.MouseWheelDelta > 0 ? 1.15f : 1f / 1.15f;
        float newZoom = Math.Clamp(Scroll.ZoomFactor * factor, 0.1f, 10f);

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

    // ── Viewport size change ──────────────────────────────────────────────────

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Re-fit whenever the viewport resizes (e.g. window resize, panel open/close)
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
}
