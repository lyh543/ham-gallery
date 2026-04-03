using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
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

    // ── Rotation ──────────────────────────────────────────────────────────────

    private double _rotationAngle = 0.0;

    // ── Filmstrip selection guard ─────────────────────────────────────────────

    private bool _suppressFilmStripChange = false;

    // ── Fullscreen ────────────────────────────────────────────────────────────

    private bool _isFullscreen = false;

    // ── CancellationToken ─────────────────────────────────────────────────────

    private CancellationTokenSource _cts = new();

    // ── Pending navigation args (set in OnNavigatedTo, consumed in Loaded) ───

    private PhotoDetailArgs? _pendingArgs;

    // ── Image preload cache (LRU-evicted, size = PreloadCount + 1) ────────────

    private readonly Dictionary<string, BitmapImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    // ── Toast ─────────────────────────────────────────────────────────────────

    private readonly DispatcherTimer _toastTimer;
    private enum ToastKind { Normal, Error }

    // ── Constructor ───────────────────────────────────────────────────────────

    public PhotoDetailPage()
    {
        InitializeComponent();

        ViewModel = App.Current.Services.GetRequiredService<PhotoDetailViewModel>();

        // Auto-hide timer
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideTimer.Tick += (_, _) => HideChrome();

        // Toast auto-dismiss timer (3 s)
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => HideToast();

        ZoomImage.SwipeLeft  += () => _ = ViewModel.NavigateToIndexAsync(ViewModel.CurrentIndex + 1, _cts.Token);
        ZoomImage.SwipeRight += () => _ = ViewModel.NavigateToIndexAsync(ViewModel.CurrentIndex - 1, _cts.Token);

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    // ── Page lifecycle ────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not PhotoDetailArgs args) return;

        _cts         = new CancellationTokenSource();
        _pendingArgs = args;

        // Defer all heavy work (DB query, filmstrip build, image decode) until
        // after the first layout pass so the page skeleton renders immediately.
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;

        var args = _pendingArgs;
        if (args is null) return;

        await ViewModel.InitializeAsync(
            args.Photos,
            args.InitialIndex,
            DispatcherQueue,
            _cts.Token);

        // LoadCurrentImageAsync / UpdateCounterText / PreloadAdjacent are already
        // triggered by ViewModel_PropertyChanged when CurrentImagePath is set inside
        // InitializeAsync → NavigateToIndexAsync.
        ShowChrome();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _hideTimer.Stop();
        _toastTimer.Stop();
        _cts.Cancel();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Dispose();
    }

    // ── Image loading ─────────────────────────────────────────────────────────

    private async Task LoadCurrentImageAsync()
    {
        var path = ViewModel.CurrentImagePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (_imageCache.TryGetValue(path, out var cached))
            {
                await ZoomImage.LoadImageFromCacheAsync(cached, _cts.Token);
            }
            else
            {
                await ZoomImage.LoadImageAsync(path, _cts.Token);
                if (ZoomImage.CurrentBitmap is { } bmp)
                    AddToCache(path, bmp);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Image preloading ──────────────────────────────────────────────────────

    private void PreloadAdjacent(int currentIndex)
    {
        var paths = ViewModel.GetPreloadPaths(currentIndex);
        foreach (var path in paths)
        {
            if (_imageCache.ContainsKey(path)) continue;
            try
            {
                var bmp = new BitmapImage(new Uri(path));
                AddToCache(path, bmp);
            }
            catch { /* skip invalid paths */ }
        }
    }

    private void AddToCache(string path, BitmapImage bmp)
    {
        _imageCache[path] = bmp;
        // Keep at most PreloadCount + 1 (current + adjacent)
        int maxCached = ViewModel.PreloadCount + 1;
        while (_imageCache.Count > maxCached)
        {
            var oldest = _imageCache.Keys.First();
            _imageCache.Remove(oldest);
        }
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
                PreloadAdjacent(ViewModel.CurrentIndex);
                break;

            case nameof(PhotoDetailViewModel.InfoFileName):
                InfoFileName.Text    = ViewModel.InfoFileName ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoFilePath):
                InfoFilePath.Text    = ViewModel.InfoFilePath ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoFileSize):
                InfoFileSize.Text    = ViewModel.InfoFileSize ?? "—"; break;
            case nameof(PhotoDetailViewModel.InfoResolution):
                InfoResolution.Text  = ViewModel.InfoResolution ?? "—"; break;
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

            case nameof(PhotoDetailViewModel.CurrentIndex):
                SyncFilmStripSelection();
                break;
        }
    }

    // ── Toolbar chrome show / hide ────────────────────────────────────────────

    private void ShowChrome()
    {
        _hideTimer.Stop();

        if (!_toolbarVisible)
        {
            _toolbarVisible = true;
            AnimateOpacity(Toolbar, 1.0);
            AnimateOpacity(PrevButton, 1.0);
            AnimateOpacity(NextButton, 1.0);
            FilmStripRow.Height = GridLength.Auto;
        }

        _hideTimer.Start();
    }

    private void HideChrome()
    {
        _hideTimer.Stop();
        _toolbarVisible = false;

        AnimateOpacity(Toolbar, 0.0);
        AnimateOpacity(PrevButton, 0.0);
        AnimateOpacity(NextButton, 0.0);
        FilmStripRow.Height = new GridLength(0);
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
        => ShowChrome();

    private void Page_GotFocus(object sender, RoutedEventArgs e)
        => ShowChrome();

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
                    await ViewModel.NavigateToIndexAsync(ViewModel.CurrentIndex - 1, _cts.Token);
                }
                break;

            case VirtualKey.Right:
                if (!isCtrl)
                {
                    e.Handled = true;
                    await ViewModel.NavigateToIndexAsync(ViewModel.CurrentIndex + 1, _cts.Token);
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
        if (Frame.CanGoBack) Frame.GoBack();
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

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        await Windows.System.Launcher.LaunchFileAsync(file);
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
            // Build the checkbox "下次不再提示"
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

            var dialog = new ContentDialog
            {
                Title             = "删除照片",
                Content           = panel,
                PrimaryButtonText = "删除",
                CloseButtonText   = "取消",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            // If user checked "下次不再提示", disable confirmation and notify
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

        // If no photos remain, go back
        if (ViewModel.CurrentImagePath is null)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }

    // ── Undo delete ───────────────────────────────────────────────────────────

    private async Task UndoDeleteAsync()
    {
        if (!ViewModel.CanUndo) return;

        HideToast();

        var restoredName = await ViewModel.UndoDeleteAsync(_cts.Token);

        if (restoredName is null)
        {
            ShowToast("恢复失败，文件可能已被移动或删除", ToastKind.Error, showUndo: false);
        }
        else
        {
            ShowToast($"照片「{restoredName}」已恢复", ToastKind.Normal, showUndo: false);
        }
    }

    // ── Toast undo button handler ─────────────────────────────────────────────

    private async void ToastUndo_Click(object sender, RoutedEventArgs e)
        => await UndoDeleteAsync();

    // ── Toast helpers ─────────────────────────────────────────────────────────

    private void ShowToast(string message, ToastKind kind, bool showUndo)
    {
        _toastTimer.Stop();

        ToastText.Text = message;

        // Visual mode
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

        // Make the host hit-testable only when the undo button is shown
        ToastHost.IsHitTestVisible = showUndo;

        // Fade in
        AnimateOpacity(ToastHost, 1.0, durationMs: 180);

        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        AnimateOpacity(ToastHost, 0.0, durationMs: 250);
        ToastHost.IsHitTestVisible = false;
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
        appWindow?.SetPresenter(AppWindowPresenterKind.FullScreen);
        _isFullscreen = true;
        FullscreenIcon.Glyph = "\uE73F";
    }

    private void ExitFullscreen()
    {
        var appWindow = GetAppWindow();
        appWindow?.SetPresenter(AppWindowPresenterKind.Default);
        _isFullscreen = false;
        FullscreenIcon.Glyph = "\uE740";
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
