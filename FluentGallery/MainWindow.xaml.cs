using FluentGallery.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace FluentGallery;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        // Set initial window size (800×600 minimum per spec)
        AppWindow.Resize(new SizeInt32(1200, 800));

        // Enforce minimum window size via the OverlappedPresenter
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        // Navigate to the default page (Albums) by programmatically selecting the nav item.
        // SelectionChanged handler will perform the actual navigation.
        NavView.SelectedItem = AlbumsNavItem;
    }

    /// <summary>
    /// Handles navigation-bar back button: go back in the Frame stack and sync selection.
    /// </summary>
    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
            SyncNavSelection();
        }
    }

    /// <summary>
    /// Navigates to the page corresponding to the selected NavigationViewItem.
    /// </summary>
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        var pageType = item.Tag?.ToString() switch
        {
            "AlbumList" => typeof(AlbumListPage),
            "AllPhotos" => typeof(AllPhotosPage),
            "Settings"  => typeof(SettingsPage),
            _           => null
        };

        if (pageType is null || ContentFrame.CurrentSourcePageType == pageType)
            return;

        ContentFrame.Navigate(pageType);

        // Update the NavigationView header to reflect the current page title.
        NavView.Header = item.Content?.ToString();
    }

    /// <summary>
    /// After a back-navigation, re-select the nav item that matches the current page.
    /// </summary>
    private void SyncNavSelection()
    {
        NavView.SelectedItem = ContentFrame.CurrentSourcePageType?.Name switch
        {
            nameof(AlbumListPage) => AlbumsNavItem,
            nameof(AllPhotosPage)  => AllPhotosNavItem,
            nameof(SettingsPage)   => SettingsNavItem,
            _                      => null
        };

        if (NavView.SelectedItem is NavigationViewItem selected)
            NavView.Header = selected.Content?.ToString();
    }
}
