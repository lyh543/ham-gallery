using FluentGallery.ViewModels;
using FluentGallery.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    public MainWindow()
    {
        this.InitializeComponent();

        _vm = App.Current.Services.GetRequiredService<MainWindowViewModel>();
        _vm.PinnedAlbums.CollectionChanged += OnPinnedAlbumsChanged;

        // Initial size and min-size enforcement
        AppWindow.Resize(new SizeInt32(1200, 800));
        AppWindow.Changed += OnAppWindowChanged;

        // Navigate to default page
        NavView.SelectedItem = AlbumsNavItem;

        // Load pinned albums; populates dynamic nav items via CollectionChanged
        _ = _vm.LoadPinnedAlbumsAsync();
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
}

