using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using FluentGallery.ViewModels;
using FluentGallery.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace FluentGallery;

public sealed partial class MainWindow : Window
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const int MinLogicalWidth  = 800;
    private const int MinLogicalHeight = 600;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly MainWindowViewModel _vm;

    // ── Dev Warning Toast ─────────────────────────────────────────────────────
#if DEBUG
    private readonly DispatcherTimer _devWarnTimer;
    private bool _devWarnMuted = false;
#endif

    public MainWindow()
    {
        this.InitializeComponent();

        _vm = App.Current.Services.GetRequiredService<MainWindowViewModel>();
        _vm.PinnedAlbums.CollectionChanged += OnPinnedAlbumsChanged;

        // Initial size and min-size enforcement (scale logical pixels to physical pixels)
        var hwndForDpi = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpiForInit = GetDpiForWindow(hwndForDpi);
        double scaleForInit = dpiForInit / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(1200 * scaleForInit),
            (int)Math.Ceiling(800  * scaleForInit)));
        AppWindow.Changed += OnAppWindowChanged;

        Closed += OnWindowClosed;

        Title = AppDataPaths.DisplayName;

        // Navigate to default page
        NavView.SelectedItem = AlbumsNavItem;

        // Load pinned albums; populates dynamic nav items via CollectionChanged
        _ = _vm.LoadPinnedAlbumsAsync();

#if DEBUG
        _devWarnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _devWarnTimer.Tick += (_, _) => HideDevWarnToast();
        DevWarningLoggerProvider.WarningLogged += OnDevWarningLogged;
        Closed += (_, _) => DevWarningLoggerProvider.WarningLogged -= OnDevWarningLogged;
#endif
    }

    // ── Window size persistence ───────────────────────────────────────────────

    /// <summary>
    /// Switches the system backdrop between Mica (default) and Acrylic.
    /// </summary>
    public void ApplyBackdrop(bool useAcrylic)
    {
        SystemBackdrop = useAcrylic
            ? new DesktopAcrylicBackdrop()
            : new MicaBackdrop();
    }

    /// <summary>
    /// Called by <see cref="App"/> before <c>Activate()</c> so the window opens
    /// at the saved geometry with no visible resize flash.
    /// </summary>
    public void RestoreWindowSize(AppSettings settings)
    {
        if (settings.WindowMaximized)
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
                op.Maximize();
            return;
        }

        if (settings.WindowWidthRatio <= 0 || settings.WindowHeightRatio <= 0) return;

        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var monitor = display.OuterBounds;

        int w = (int)Math.Round(settings.WindowWidthRatio  * monitor.Width);
        int h = (int)Math.Round(settings.WindowHeightRatio * monitor.Height);
        int x = monitor.X + (int)Math.Round(settings.WindowLeftRatio * monitor.Width);
        int y = monitor.Y + (int)Math.Round(settings.WindowTopRatio  * monitor.Height);

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private async void OnWindowClosed(object sender, WindowEventArgs e)
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };

        var db       = App.Current.Services.GetRequiredService<DatabaseService>();
        var settings = await db.LoadSettingsAsync().ConfigureAwait(false);

        settings.WindowMaximized = maximized;

        if (!maximized)
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var monitor = display.OuterBounds;

            var size = AppWindow.Size;
            var pos  = AppWindow.Position;

            settings.WindowWidthRatio  = (double)size.Width  / monitor.Width;
            settings.WindowHeightRatio = (double)size.Height / monitor.Height;
            settings.WindowLeftRatio   = (double)(pos.X - monitor.X) / monitor.Width;
            settings.WindowTopRatio    = (double)(pos.Y - monitor.Y) / monitor.Height;
        }

        await db.SaveSettingsAsync(settings).ConfigureAwait(false);
    }

    // ── Minimum window size ───────────────────────────────────────────────────

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;

        var s    = AppWindow.Size;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi  = GetDpiForWindow(hwnd);

        int minW = (int)Math.Ceiling(MinLogicalWidth  * dpi / 96.0);
        int minH = (int)Math.Ceiling(MinLogicalHeight * dpi / 96.0);
        int w    = Math.Max(s.Width,  minW);
        int h    = Math.Max(s.Height, minH);

        if (w != s.Width || h != s.Height)
            AppWindow.Resize(new SizeInt32(w, h));
    }

    // ── Dynamic pinned-album nav items ────────────────────────────────────────

    private void OnPinnedAlbumsChanged(object? _, NotifyCollectionChangedEventArgs e)
        => RebuildPinnedNavItems();

    /// <summary>
    /// Removes all previously-inserted pinned album nav items, then re-inserts them
    /// at index 1 (immediately after the static Albums item, before All Photos).
    /// </summary>
    private void RebuildPinnedNavItems()
    {
        // Remove existing Album:* items
        for (int i = NavView.MenuItems.Count - 1; i >= 0; i--)
        {
            if (NavView.MenuItems[i] is NavigationViewItem nvi &&
                nvi.Tag is string tag &&
                tag.StartsWith("Album:", StringComparison.Ordinal))
            {
                NavView.MenuItems.RemoveAt(i);
            }
        }

        // Re-insert in SortOrder order (already sorted by the query)
        int insertAt = 1; // slot 0 = AlbumsNavItem
        foreach (var album in _vm.PinnedAlbums)
        {
            var unpinItem = new MenuFlyoutItem { Text = "取消固定" };
            long capturedId = album.Id;
            unpinItem.Click += (_, _) => _ = _vm.UnpinAlbumAsync(capturedId);

            var navItem = new NavigationViewItem
            {
                Tag    = $"Album:{album.Id}",
                Content = album.Name,
                Icon   = new FontIcon
                {
                    FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                    Glyph      = "\uE8B7", // Folder
                },
                ContextFlyout = new MenuFlyout { Items = { unpinItem } },
            };

            NavView.MenuItems.Insert(insertAt++, navItem);
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
            SyncNavSelection();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var tag = item.Tag?.ToString() ?? string.Empty;

        // Pinned album item → PhotoListPage with albumId parameter
        if (tag.StartsWith("Album:", StringComparison.Ordinal) &&
            long.TryParse(tag.AsSpan(6), out long albumId))
        {
            ContentFrame.Navigate(typeof(PhotoListPage), albumId);
            NavView.Header = item.Content?.ToString();
            return;
        }

        var pageType = tag switch
        {
            "AlbumList" => typeof(AlbumListPage),
            "AllPhotos" => typeof(AllPhotosPage),
            "Settings"  => typeof(SettingsPage),
            _           => null,
        };

        if (pageType is null || ContentFrame.CurrentSourcePageType == pageType)
            return;

        ContentFrame.Navigate(pageType);
        NavView.Header = item.Content?.ToString();
    }

    /// <summary>
    /// Re-selects the nav item that corresponds to the page currently shown in the Frame
    /// (called after back-navigation).
    /// </summary>
    /// <summary>
    /// Updates the NavigationView header text — called by pages (e.g. PhotoListPage)
    /// that want to display the album name in the title area.
    /// </summary>
    public void SetNavHeader(string header) => NavView.Header = header;

    private void SyncNavSelection()
    {
        var currentType = ContentFrame.CurrentSourcePageType;

        NavigationViewItem? found = currentType?.Name switch
        {
            nameof(AlbumListPage) => AlbumsNavItem,
            nameof(AllPhotosPage) => AllPhotosNavItem,
            nameof(SettingsPage)  => SettingsNavItem,
            _                     => null,
        };

        // For PhotoListPage, try to match by album tag via the page's parameter
        if (found is null && currentType == typeof(PhotoListPage))
        {
            // Back-navigation to a photo list: leave selection on the pinned item if possible.
            // Full parameter tracking is deferred to the PhotoListPage step.
        }

        NavView.SelectedItem = found;
        if (found is not null)
            NavView.Header = found.Content?.ToString();
    }

    // ── Dev Warning Toast ─────────────────────────────────────────────────────

#if DEBUG
    private void OnDevWarningLogged(string category, string message)
    {
        if (_devWarnMuted) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            var shortCategory = category.Contains('.')
                ? category[(category.LastIndexOf('.') + 1)..]
                : category;
            ShowDevWarnToast($"[{shortCategory}] {message}");
        });
    }

    private void ShowDevWarnToast(string message)
    {
        _devWarnTimer.Stop();
        DevWarnToastText.Text             = message;
        DevWarnToastHost.IsHitTestVisible = true;
        AnimateDevWarnOpacity(1.0, durationMs: 180);
        _devWarnTimer.Start();
    }

    private void HideDevWarnToast()
    {
        _devWarnTimer.Stop();
        AnimateDevWarnOpacity(0.0, durationMs: 250);
        DevWarnToastHost.IsHitTestVisible = false;
    }

    private void AnimateDevWarnOpacity(double target, double durationMs = 200)
    {
        var anim = new DoubleAnimation
        {
            To             = target,
            Duration       = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, DevWarnToastHost);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }
#endif

    private void DevWarnClose_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        HideDevWarnToast();
#endif
    }

    private void DevWarnToast_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
#if DEBUG
        _devWarnTimer.Stop();
#endif
    }

    private void DevWarnToast_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
#if DEBUG
        _devWarnTimer.Start();
#endif
    }

    private void DevWarnMute_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        _devWarnMuted = true;
        HideDevWarnToast();
#endif
    }
}

