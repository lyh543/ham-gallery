using FluentGallery.Helpers;
using FluentGallery.Loaders;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

    // ── CancellationTokens ────────────────────────────────────────────────────

    private CancellationTokenSource _cts = new();

    // Cancelled and recreated on every intra-page navigation to abort stale preloads.
    private CancellationTokenSource _preloadCts = new();

    // Incremented on every photo navigation; stale LoadCurrentImageAsync completions
    // check this and discard their result if a newer load has started.
    private int _loadGeneration = 0;

    // ── Pending navigation args (set in OnNavigatedTo, consumed in Loaded) ───

    private PhotoDetailArgs? _pendingArgs;

    // ── Image loaders ─────────────────────────────────────────────────────────

    private readonly WicImageLoader  _wicLoader;
    private readonly HeicImageLoader _heicLoader;

    // ── Toast ─────────────────────────────────────────────────────────────────

    private readonly DispatcherTimer _toastTimer;
    private enum ToastKind { Normal, Error }

    private readonly ILogger<PhotoDetailPage> _logger =
        App.Current.Services.GetRequiredService<ILogger<PhotoDetailPage>>();

    // ── Constructor ───────────────────────────────────────────────────────────

    public PhotoDetailPage()
    {
        InitializeComponent();

        ViewModel = App.Current.Services.GetRequiredService<PhotoDetailViewModel>();

        _wicLoader  = App.Current.Services.GetRequiredService<WicImageLoader>();
        _heicLoader = App.Current.Services.GetRequiredService<HeicImageLoader>();
        ApplyPreloadCount(ViewModel.PreloadCountBack, ViewModel.PreloadCountForward);

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

        ElasticScrollHelper.Attach(FilmStrip, ElasticScrollHelper.ScrollAxis.Horizontal);
        ElasticScrollHelper.Attach(InfoScrollViewer);

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
        ApplyFilmStripPinState();
        ApplyShowPreloadStatus();
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

        // Release the currently displayed BitmapImage so the WIC decoder surface
        // (~48 MB per 12 MP HEIC in BGRA8) is freed as soon as GC runs.
        // Without this, the page stays in Frame's BackStack with MainImage.Source
        // still set, keeping the COM reference count alive and locking WIC memory.
        ZoomImage.SetLoading();

        // Loaders are singletons; clear their caches when leaving the page so
        // BitmapImage objects (and their GPU/WIC memory) are released promptly.
        _wicLoader.ClearCache();
        _heicLoader.ClearCache();

        _logger.LogDebug("OnNavigatedFrom: image caches cleared");
    }

    // ── Image loading ─────────────────────────────────────────────────────────

    private async Task LoadCurrentImageAsync()
    {
        int gen  = ++_loadGeneration;
        var path = ViewModel.CurrentImagePath;
        if (string.IsNullOrEmpty(path)) return;

        _logger.LogDebug("LoadCurrentImage: {Path}", path);
        ZoomImage.SetLoading();
        try
        {
            var loader = GetLoader(path);
            var loaded = await loader.LoadAsync(path, _cts.Token);
            if (loaded is null) return;

            // Another navigation started while we were loading — discard this result.
            if (gen != _loadGeneration)
            {
                if (loaded.Source is IDisposable d)
                    DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => { try { d.Dispose(); } catch { } });
                return;
            }

            ZoomImage.SetSource(loaded, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "LoadCurrentImage failed: {Path}", path); }
    }

    // ── Image preloading ──────────────────────────────────────────────────────

    private void PreloadAdjacent(int currentIndex)
    {
        var paths = ViewModel.GetPreloadPaths(currentIndex);
        foreach (var path in paths)
        {
            var item = ViewModel.FilmStripItems.FirstOrDefault(
                i => string.Equals(i.Photo.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (item is not null && item.PreloadState == PreloadState.NotLoaded)
            {
                item.PreloadState = PreloadState.Loading;
                var captured = item;
                var token    = _preloadCts.Token;
                _ = GetLoader(path).PreloadAsync(path, token).ContinueWith(_ =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!token.IsCancellationRequested)
                            captured.PreloadState = PreloadState.Loaded;
                    });
                }, TaskScheduler.Default);
            }
        }
    }

    private IImageLoader GetLoader(string path) =>
        _heicLoader.IsSupported(Path.GetExtension(path)) ? _heicLoader : _wicLoader;

    // ── FilmStrip pin toggle ──────────────────────────────────────────────────

    private void FilmStripPin_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.ToggleFilmStripPinnedAsync(_cts.Token);
        ApplyFilmStripPinState();
    }

    private void ApplyFilmStripPinState()
    {
        bool pinned = ViewModel.FilmStripPinned;
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
                _preloadCts.Cancel();
                _preloadCts = new CancellationTokenSource();
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
        _wicLoader.MaxCacheSize  = cacheSize;
        _heicLoader.MaxCacheSize = cacheSize;
    }

    // ── Toolbar chrome show / hide ────────────────────────────────────────────

    // ShowChrome/HideChrome only control the Toolbar and nav arrows.
    // FilmStrip visibility is controlled exclusively by the pin button via ApplyFilmStripPinState.
    private void ShowChrome()
    {
        _hideTimer.Stop();

        if (!_toolbarVisible)
        {
            _toolbarVisible = true;
            AnimateOpacity(Toolbar, 1.0);
            AnimateOpacity(PrevButton, 1.0);
            AnimateOpacity(NextButton, 1.0);
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
