using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    private DispatcherTimer _hideTimer = null!;
    private bool _toolbarVisible = false;
    private bool _hideChromeInProgress = false;
    private int _chromePointerCount = 0;
    private Point? _lastPagePointerPosition;

    private bool _isFullscreen = false;
    private bool _wasMaximizedBeforeFullscreen = false;

    private DispatcherTimer _toastTimer = null!;
    private DispatcherTimer _edgeBoundaryThrottle = null!;
    private bool _edgeBoundaryThrottleActive = false;

    private enum ToastKind { Normal, Error }

    private void InitializeChromeState()
    {
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) => HideChrome();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => HideToast();

        _edgeBoundaryThrottle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _edgeBoundaryThrottle.Tick += (_, _) =>
        {
            _edgeBoundaryThrottleActive = false;
            _edgeBoundaryThrottle.Stop();
        };
    }

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
            To = target,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, element);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void Page_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (DebugKeepChromeVisible)
            return;

        Point pos = e.GetCurrentPoint(this).Position;

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

        if (_chromePointerCount == 0)
            RestartHideTimer();
    }

    private void ShowToast(string message, ToastKind kind, bool showUndo)
    {
        _toastTimer.Stop();

        ToastText.Text = message;

        if (kind == ToastKind.Error)
        {
            ToastCard.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(0xE0, 0xC0, 0x20, 0x20));
            ToastIcon.Visibility = Visibility.Visible;
        }
        else
        {
            ToastCard.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x20));
            ToastIcon.Visibility = Visibility.Collapsed;
        }

        ToastUndoButton.Visibility = showUndo ? Visibility.Visible : Visibility.Collapsed;
        ToastHost.IsHitTestVisible = showUndo;

        double toolbarHeight = BottomToolbar.ActualHeight > 0 ? BottomToolbar.ActualHeight : 52;
        double toolbarBottomMargin = 20;
        double filmstripHeight = FilmStripRow.ActualHeight;
        double spacing = 10;
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

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
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
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }
}
