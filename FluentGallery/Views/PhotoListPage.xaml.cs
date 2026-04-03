using FluentGallery.Data;
using FluentGallery.Models;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace FluentGallery.Views;

public sealed partial class PhotoListPage : Page
{
    // ── ViewModel ─────────────────────────────────────────────────────────────

    public PhotoListViewModel ViewModel { get; }

    // ── Page-level cancellation ───────────────────────────────────────────────

    private CancellationTokenSource _pageCts = new();

    // ── Pinch-gesture tracking ────────────────────────────────────────────────

    private double _cumulativeScale = 1.0;

    // ── Construction ──────────────────────────────────────────────────────────

    public PhotoListPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<PhotoListViewModel>();
        this.InitializeComponent();

        // Update GridView item size when column count changes
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhotoListViewModel.ColumnCount))
                UpdateItemSize();
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
                await ViewModel.LoadAsync(albumId, _pageCts.Token);
                UpdateEmptyState();
                SyncNavHeader();
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
            // Pass album photos + clicked index to PhotoDetailPage
            var index = ViewModel.Photos.IndexOf(photo);
            Frame.Navigate(
                typeof(PhotoDetailPage),
                new PhotoDetailArgs(ViewModel.Photos.Select(p => p.GetPhoto()).ToList(), index));
        }
    }

    // ── GridView: size change → update item size ──────────────────────────────

    private void PhotoGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateItemSize();

    private void UpdateItemSize()
    {
        if (PhotoGridView.ItemsPanelRoot is not ItemsWrapGrid wg) return;
        double available = Math.Max(1, PhotoGridView.ActualWidth - 8);
        double size      = Math.Floor(available / ViewModel.ColumnCount);
        wg.ItemWidth  = size;
        wg.ItemHeight = size;
    }

    // ── GridView: pinch gesture → adjust column count ─────────────────────────

    private void PhotoGridView_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        _cumulativeScale *= e.Delta.Scale;

        if (_cumulativeScale < 0.75)
        {
            ViewModel.AdjustColumnCount(+1); // pinch in → more (smaller) columns
            _cumulativeScale = 1.0;
        }
        else if (_cumulativeScale > 1.35)
        {
            ViewModel.AdjustColumnCount(-1); // expand → fewer (larger) columns
            _cumulativeScale = 1.0;
        }
    }

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
            var dialog = new ContentDialog
            {
                Title             = "删除照片",
                Content           = $"确定要将选中的 {selected.Count} 张照片移入回收站吗？",
                PrimaryButtonText = "移入回收站",
                CloseButtonText   = "取消",
                XamlRoot          = XamlRoot,
                DefaultButton     = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
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
            flyout.Items.Add(new MenuFlyoutItem { Text = "没有其他相册", IsEnabled = false });

        flyout.ShowAt(MoveToAlbumButton);
    }

    // ── Sort ──────────────────────────────────────────────────────────────────

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        ViewModel.SortField = item.Tag?.ToString() switch
        {
            "Size"       => PhotoSortField.Size,
            "CreatedAt"  => PhotoSortField.CreatedAt,
            "ModifiedAt" => PhotoSortField.ModifiedAt,
            "TakenAt"    => PhotoSortField.TakenAt,
            "Natural"    => PhotoSortField.Natural,
            _            => PhotoSortField.Name,
        };
    }

    private void SortDescToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem t) return;
        ViewModel.SortDirection = t.IsChecked ? SortDirection.Descending : SortDirection.Ascending;
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
}
