using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.System;

namespace FluentGallery.Views;

public sealed partial class PhotoListPage : Page
{
    // ── ViewModel ─────────────────────────────────────────────────────────────

    public PhotoListViewModel ViewModel { get; }

    // ── Page-level cancellation ───────────────────────────────────────────────

    private CancellationTokenSource _pageCts = new();

    // ── Pinch-gesture tracking ────────────────────────────────────────────────

    private double _cumulativeScale = 1.0;

    // ── Toast state ───────────────────────────────────────────────────────────

    private CancellationTokenSource? _toastCts;

    // ── Construction ──────────────────────────────────────────────────────────

    public PhotoListPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<PhotoListViewModel>();
        this.InitializeComponent();

        // Update GridView item size when card width changes
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhotoListViewModel.PhotoCardWidth))
            {
                UpdateItemSize();
                if (ViewModel.ShowCardSizeToast)
                    _ = ShowCardSizeToastAsync($"{ViewModel.PhotoCardWidth} px");
            }
            else if (e.PropertyName == nameof(PhotoListViewModel.IsMultiSelectMode))
                ApplySelectionMode();
            else if (e.PropertyName == nameof(PhotoListViewModel.Photos))
                UpdateEmptyState();
            else if (e.PropertyName == nameof(PhotoListViewModel.AlbumName))
                SyncNavHeader();
        };

        ViewModel.Photos.CollectionChanged += (_, _) => UpdateEmptyState();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new CancellationTokenSource();
        _cumulativeScale = 1.0;

        if (e.Parameter is long albumId)
        {
            Loaded += async (_, _) =>
            {
                ElasticScrollHelper.Attach(PhotoGridView);
                await ViewModel.LoadAsync(albumId, _pageCts.Token);
                UpdateEmptyState();
                SyncNavHeader();
                UpdateItemSize();
            };
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _pageCts.Cancel();
        _pageCts.Dispose();
    }

    // ── GridView: lazy thumbnail loading ─────────────────────────────────────

    private void PhotoGridView_ContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            // Free thumbnail memory when the container is recycled
            if (args.Item is PhotoItemViewModel vm)
                vm.ClearThumbnail();
            return;
        }

        // Phase 0: request phase 1 to load thumbnail asynchronously
        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(PhotoGridView_ContainerContentChanging);
            return;
        }

        // Phase 1: trigger thumbnail load
        if (args.Item is PhotoItemViewModel photoVm)
            _ = photoVm.LoadThumbnailAsync(
                    App.Current.Services.GetRequiredService<ThumbnailService>(),
                    _pageCts.Token);
    }

    // ── GridView: item click (navigate to PhotoDetailPage) ───────────────────

    private void PhotoGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.IsMultiSelectMode) return; // clicks = selection in multi-select mode
        if (e.ClickedItem is PhotoItemViewModel photo)
        {
            // Pass album photos + clicked index to PhotoDetailPage (shown as full-window overlay)
            var index = ViewModel.Photos.IndexOf(photo);
            if (App.Current.MainWindow is MainWindow mw)
                mw.ShowPhotoDetail(new PhotoDetailArgs(ViewModel.Photos.Select(p => p.GetPhoto()).ToList(), index));
        }
    }

    // ── GridView: size change → update item size ──────────────────────────────

    private void PhotoGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateItemSize();

    private void UpdateItemSize()
    {
        if (PhotoGridView.ItemsPanelRoot is not ItemsWrapGrid wg) return;
        double size = ViewModel.PhotoCardWidth;
        wg.ItemWidth  = size;
        wg.ItemHeight = size;
    }

    // ── GridView: pinch gesture → adjust card width ───────────────────────────

    private void PhotoGridView_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        _cumulativeScale *= e.Delta.Scale;

        if (_cumulativeScale < 0.75)
        {
            ViewModel.ZoomOut();
            _cumulativeScale = 1.0;
        }
        else if (_cumulativeScale > 1.35)
        {
            ViewModel.ZoomIn();
            _cumulativeScale = 1.0;
        }
    }

    // ── Ctrl + scroll wheel ───────────────────────────────────────────────────

    private void PhotoGridView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        if (!props.IsHorizontalMouseWheel &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            if (props.MouseWheelDelta > 0) ViewModel.ZoomIn();
            else                           ViewModel.ZoomOut();
            e.Handled = true;
        }
    }

    // ── Zoom buttons ──────────────────────────────────────────────────────────

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.ZoomInCommand.Execute(null);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.ZoomOutCommand.Execute(null);

    // ── Multi-select mode ─────────────────────────────────────────────────────

    private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleMultiSelectModeCommand.Execute(null);
    }

    private void ApplySelectionMode()
    {
        if (ViewModel.IsMultiSelectMode)
        {
            PhotoGridView.SelectionMode      = ListViewSelectionMode.Multiple;
            PhotoGridView.IsItemClickEnabled = false;
        }
        else
        {
            PhotoGridView.SelectedItems.Clear();
            PhotoGridView.SelectionMode      = ListViewSelectionMode.None;
            PhotoGridView.IsItemClickEnabled = true;
        }
    }

    // ── Add photos ────────────────────────────────────────────────────────────

    private async void AddPhotos_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode            = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };

        // Register supported formats
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".heic", ".heif" })
            picker.FileTypeFilter.Add(ext);

        // WinUI 3: associate picker with the window handle
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        await ViewModel.AddPhotosAsync(files, _pageCts.Token);
        UpdateEmptyState();
    }

    // ── Delete photos ─────────────────────────────────────────────────────────

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = PhotoGridView.SelectedItems
            .OfType<PhotoItemViewModel>()
            .ToList();

        if (selected.Count == 0) return;

        if (ViewModel.ConfirmBeforeDelete)
        {
            if (!await ConfirmDialogHelper.ShowAsync(
                    XamlRoot,
                    L10n.Get("PhotoList_DeleteConfirm_Title"),
                    L10n.Format("PhotoList_DeleteConfirm_Content", selected.Count),
                    L10n.Get("PhotoList_DeleteConfirm_Confirm"),
                    confirmStyle: DialogButtonStyle.Danger)) return;
        }

        await ViewModel.DeletePhotosAsync(selected, _pageCts.Token);
        PhotoGridView.SelectedItems.Clear();
        UpdateEmptyState();
    }

    // ── Move to album ─────────────────────────────────────────────────────────

    private async void MoveToAlbum_Click(object sender, RoutedEventArgs e)
    {
        var selected = PhotoGridView.SelectedItems
            .OfType<PhotoItemViewModel>()
            .ToList();

        if (selected.Count == 0) return;

        var albums = await ViewModel.GetAlbumsAsync(_pageCts.Token);

        // Build a flyout with one item per album (excluding the current one)
        var flyout = new MenuFlyout();
        foreach (var album in albums)
        {
            if (album.Id == selected[0].AlbumId && selected.All(p => p.AlbumId == album.Id))
                continue; // skip current album

            var item = new MenuFlyoutItem { Text = album.Name };
            long targetId = album.Id;
            item.Click += async (_, _) =>
            {
                await ViewModel.MoveToAlbumAsync(selected, targetId, _pageCts.Token);
                PhotoGridView.SelectedItems.Clear();
                UpdateEmptyState();
            };
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
            flyout.Items.Add(new MenuFlyoutItem { Text = L10n.Get("PhotoList_MoveToAlbum_Empty"), IsEnabled = false });

        flyout.ShowAt(MoveToAlbumButton);
    }

    // ── Sort ──────────────────────────────────────────────────────────────────

    private void SortFlyout_Opening(object? sender, object e)
    {
        // Sync RadioMenuFlyoutItem checked state to the current ViewModel sort field.
        SortByName.IsChecked     = ViewModel.SortField == PhotoSortField.Name;
        SortBySize.IsChecked     = ViewModel.SortField == PhotoSortField.Size;
        SortByCreated.IsChecked  = ViewModel.SortField == PhotoSortField.CreatedAt;
        SortByModified.IsChecked = ViewModel.SortField == PhotoSortField.ModifiedAt;
        SortByTakenAt.IsChecked  = ViewModel.SortField == PhotoSortField.TakenAt;
        // Toggle shows "升序"; checked = ascending (non-default), unchecked = descending (default)
        SortDescToggle.IsChecked = ViewModel.SortDirection == SortDirection.Ascending;
    }

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        ViewModel.SortField = item.Tag?.ToString() switch
        {
            "Size"       => PhotoSortField.Size,
            "CreatedAt"  => PhotoSortField.CreatedAt,
            "ModifiedAt" => PhotoSortField.ModifiedAt,
            "TakenAt"    => PhotoSortField.TakenAt,
            _            => PhotoSortField.Name,
        };
    }

    private void SortDescToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem t) return;
        // Checked = ascending, unchecked = descending
        ViewModel.SortDirection = t.IsChecked ? SortDirection.Ascending : SortDirection.Descending;
    }

    // ── Nav header sync ───────────────────────────────────────────────────────

    private void SyncNavHeader()
    {
        if (App.Current.MainWindow is MainWindow mw)
            mw.SetNavHeader(ViewModel.AlbumName);
    }

    // ── Album inline rename ───────────────────────────────────────────────────

    private void RenameAlbum_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginRenameAlbum();
        DispatcherQueue.TryEnqueue(() =>
        {
            AlbumRenameBox.Focus(FocusState.Programmatic);
            AlbumRenameBox.SelectAll();
        });
    }

    private void AlbumRenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = ViewModel.CommitRenameAlbumAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.CancelRenameAlbum();
        }
    }

    private async void AlbumRenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRenamingAlbum)
            await ViewModel.CommitRenameAlbumAsync();
    }

    // ── Search within album ───────────────────────────────────────────────────

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(
            typeof(SearchPage),
            new SearchArgs(AlbumId: ViewModel.AlbumId, AlbumName: ViewModel.AlbumName));
    }

    // ── Empty state ───────────────────────────────────────────────────────────

    private void UpdateEmptyState()
    {
        EmptyStatePanel.Visibility = !ViewModel.IsLoading && ViewModel.Photos.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Card size toast ───────────────────────────────────────────────────────

    private async Task ShowCardSizeToastAsync(string text)
    {
        // Cancel any previous auto-dismiss so only one timer runs at a time
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var ct = _toastCts.Token;

        CardSizeToastText.Text = text;
        CardSizeToast.Opacity  = 0.85;

        try
        {
            await Task.Delay(1000, ct);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer toast — leave it visible
        }

        // Fade out over 200 ms
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            From     = 0.85,
            To       = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
        };
        Storyboard.SetTarget(fade, CardSizeToast);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Begin();
    }
}
