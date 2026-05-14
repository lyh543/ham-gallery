using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using FluentGallery.Services;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;

namespace FluentGallery.Views;

public sealed partial class PhotoListPage : Page
{
    private const double ContextMenuMaxWidth = ContextMenuHelper.DefaultMaxWidth;

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public PhotoListViewModel ViewModel { get; }

    // ── Page-level cancellation ───────────────────────────────────────────────

    private CancellationTokenSource _pageCts = new();

    // ── Pinch-gesture tracking ────────────────────────────────────────────────

    private double _cumulativeScale = 1.0;

    // ── Toast state ───────────────────────────────────────────────────────────

    private CancellationTokenSource? _toastCts;
    private readonly ThumbnailRefreshService _thumbnailRefreshService;
    private bool _thumbnailRefreshSubscribed;

    // ── Construction ──────────────────────────────────────────────────────────

    public PhotoListPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<PhotoListViewModel>();
        _thumbnailRefreshService = App.Current.Services.GetRequiredService<ThumbnailRefreshService>();
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
            {
                ApplySelectionMode();
                ApplyToolbarMode();
            }
            else if (e.PropertyName == nameof(PhotoListViewModel.Photos))
                UpdateEmptyState();
            else if (e.PropertyName == nameof(PhotoListViewModel.AlbumName))
                SyncNavHeader();
        };

        ViewModel.Photos.CollectionChanged += (_, _) => UpdateEmptyState();
        ApplyToolbarMode();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new CancellationTokenSource();
        _cumulativeScale = 1.0;

        if (!_thumbnailRefreshSubscribed)
        {
            _thumbnailRefreshService.ThumbnailRefreshed += OnThumbnailRefreshed;
            _thumbnailRefreshSubscribed = true;
        }

        if (e.Parameter is long albumId)
        {
            async void OnPageLoaded(object sender, RoutedEventArgs args)
            {
                Loaded -= OnPageLoaded;

                try
                {
                    ElasticScrollHelper.Attach(PhotoGridView);
                    await ViewModel.LoadAsync(albumId, _pageCts.Token);
                    UpdateEmptyState();
                    SyncNavHeader();
                    UpdateItemSize();
                }
                catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
                {
                }
            }

            Loaded += OnPageLoaded;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_thumbnailRefreshSubscribed)
        {
            _thumbnailRefreshService.ThumbnailRefreshed -= OnThumbnailRefreshed;
            _thumbnailRefreshSubscribed = false;
        }

        _pageCts.Cancel();
        _pageCts.Dispose();
    }

    private void OnThumbnailRefreshed(object? sender, ThumbnailRefreshEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var item = ViewModel.Photos.FirstOrDefault(photo => photo.Id == e.PhotoId);
            item?.RefreshThumbnail(e.ThumbPath, e.ModifiedAt);
        });
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

    private void ApplyToolbarMode()
    {
        var batchVisibility = ViewModel.IsMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        var browseVisibility = ViewModel.IsMultiSelectMode ? Visibility.Collapsed : Visibility.Visible;

        SelectAllButton.Visibility = batchVisibility;
        DeleteButton.Visibility = batchVisibility;
        MoveButton.Visibility = batchVisibility;
        CopyButton.Visibility = batchVisibility;
        BatchCommandSeparator.Visibility = batchVisibility;
        SortButton.Visibility = browseVisibility;
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

    // ── Delete photos ─────────────────────────────────────────────────────────

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedPhotos();
        if (!await ConfirmDeleteAsync(selected, isPhotoListPage: true)) return;

        await ViewModel.DeletePhotosAsync(selected, _pageCts.Token);
        ClearSelectionSafely();
        ExitMultiSelectMode();
        await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Deleted"));
        UpdateEmptyState();
    }

    // ── Move / copy ──────────────────────────────────────────────────────────

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Photos.Count > 0)
            PhotoGridView.SelectAll();
    }

    private async void MovePhotos_Click(object sender, RoutedEventArgs e)
        => await ShowBatchDirectoryFlyoutAsync(MoveButton, isMove: true);

    private async void CopyPhotos_Click(object sender, RoutedEventArgs e)
        => await ShowBatchDirectoryFlyoutAsync(CopyButton, isMove: false);

    private async Task ShowBatchDirectoryFlyoutAsync(AppBarButton anchor, bool isMove)
    {
        var selected = GetSelectedPhotos();
        if (selected.Count == 0) return;

        var directories = await ViewModel.GetAlbumDirectoriesAsync(_pageCts.Token);
        var excluded = selected
            .Select(photo => Path.GetDirectoryName(photo.FilePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var flyout = new MenuFlyout();
        foreach (var directory in directories.Where(d => !excluded.Contains(d.DirectoryPath)))
        {
            var item = CreateDirectoryMenuItem(directory.Name, directory.DirectoryPath);
            item.Click += async (_, _) => await ExecuteBatchDirectoryActionAsync(selected, directory.DirectoryPath, isMove);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count > 0)
            flyout.Items.Add(new MenuFlyoutSeparator());

        var otherItem = new MenuFlyoutItem
        {
            Text = L10n.Get("AlbumList_Context_Other"),
            Icon = new FontIcon { Glyph = "\uE8F4" },
        };
        otherItem.Click += async (_, _) =>
        {
            var targetDir = await PickSingleFolderAsync();
            if (string.IsNullOrWhiteSpace(targetDir)) return;
            await ExecuteBatchDirectoryActionAsync(selected, targetDir, isMove);
        };
        flyout.Items.Add(otherItem);
        flyout.ShowAt(anchor);
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
        SortAscendingItem.IsChecked  = ViewModel.SortDirection == SortDirection.Ascending;
        SortDescendingItem.IsChecked = ViewModel.SortDirection == SortDirection.Descending;
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

    private void SortDirectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        ViewModel.SortDirection = string.Equals(item.Tag?.ToString(), "Ascending", StringComparison.Ordinal)
            ? SortDirection.Ascending
            : SortDirection.Descending;
    }

    // ── Nav header sync ───────────────────────────────────────────────────────

    private void SyncNavHeader()
    {
        if (App.Current.MainWindow is MainWindow mw)
            mw.SyncPhotoListNavigation(ViewModel.AlbumId, ViewModel.AlbumName);
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
        => await ShowTransientToastAsync(text);

    protected override void OnRightTapped(RightTappedRoutedEventArgs e)
    {
        base.OnRightTapped(e);

        if (e.OriginalSource is not FrameworkElement src) return;
        var vm = FindPhotoVm(src);
        if (vm is null) return;

        e.Handled = true;
        _ = ShowContextMenuAsync(vm, src, e.GetPosition(src));
    }

    private void PhotoGridView_Holding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != Microsoft.UI.Input.HoldingState.Started) return;
        if (e.OriginalSource is not FrameworkElement src) return;

        var vm = FindPhotoVm(src);
        if (vm is null) return;

        e.Handled = true;
        _ = ShowContextMenuAsync(vm, src, e.GetPosition(src));
    }

    private static PhotoItemViewModel? FindPhotoVm(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is FrameworkElement fe && fe.DataContext is PhotoItemViewModel vm)
                return vm;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async Task ShowContextMenuAsync(PhotoItemViewModel vm, FrameworkElement anchor, Point point)
    {
        var directories = await ViewModel.GetAlbumDirectoriesAsync(_pageCts.Token);

        var flyout = new MenuFlyout();
        flyout.Items.Add(BuildDirectorySubMenu(vm, anchor, point, isMove: true, directories));
        flyout.Items.Add(BuildDirectorySubMenu(vm, anchor, point, isMove: false, directories));

        var openInExplorer = new MenuFlyoutItem
        {
            Text = L10n.Get("AlbumList_Context_OpenInExplorer"),
            Icon = new FontIcon { Glyph = "\uE838" },
            IsEnabled = File.Exists(vm.FilePath),
        };
        openInExplorer.Click += (_, _) => ViewModel.OpenPhotoInExplorer(vm);
        flyout.Items.Add(openInExplorer);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem
        {
            Text = L10n.Get("AlbumList_Context_Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" },
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        delete.Click += async (_, _) =>
        {
            if (!await ConfirmDeleteAsync([vm], isPhotoListPage: true)) return;
            await ViewModel.DeletePhotosAsync([vm], _pageCts.Token);
            await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Deleted"));
            UpdateEmptyState();
        };
        flyout.Items.Add(delete);

        flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var item in CreateInfoItems(vm))
            flyout.Items.Add(item);
        flyout.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = point,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    private MenuFlyoutSubItem BuildDirectorySubMenu(
        PhotoItemViewModel vm,
        FrameworkElement anchor,
        Point point,
        bool isMove,
        IReadOnlyList<(string Name, string DirectoryPath)> directories)
    {
        var subItem = new MenuFlyoutSubItem
        {
            Text = L10n.Get(isMove ? "AlbumList_Context_Move" : "AlbumList_Context_Copy"),
            Icon = new FontIcon { Glyph = isMove ? "\uE8DE" : "\uE8C8" },
        };

        var sourceDir = Path.GetDirectoryName(vm.FilePath);
        foreach (var directory in directories.Where(d => !IsSameDirectory(d.DirectoryPath, sourceDir)))
        {
            var item = CreateDirectoryMenuItem(directory.Name, directory.DirectoryPath);
            item.Click += async (_, _) => await ExecuteSingleDirectoryActionAsync(vm, directory.DirectoryPath, isMove);
            subItem.Items.Add(item);
        }

        if (subItem.Items.Count > 0)
            subItem.Items.Add(new MenuFlyoutSeparator());

        var otherItem = new MenuFlyoutItem
        {
            Text = L10n.Get("AlbumList_Context_Other"),
            Icon = new FontIcon { Glyph = "\uE8F4" },
        };
        otherItem.Click += async (_, _) =>
        {
            var targetDir = await PickSingleFolderAsync();
            if (string.IsNullOrWhiteSpace(targetDir)) return;

            if (IsSameDirectory(sourceDir, targetDir))
            {
                await ShowTransientToastAsync(L10n.Get("AlbumList_Toast_SameDirectory"));
                await ShowContextMenuAsync(vm, anchor, point);
                return;
            }

            await ExecuteSingleDirectoryActionAsync(vm, targetDir, isMove);
        };
        subItem.Items.Add(otherItem);

        return subItem;
    }

    private async Task ExecuteSingleDirectoryActionAsync(PhotoItemViewModel vm, string targetDir, bool isMove)
    {
        var sourceDir = Path.GetDirectoryName(vm.FilePath);
        if (IsSameDirectory(sourceDir, targetDir))
        {
            await ShowTransientToastAsync(L10n.Get("AlbumList_Toast_SameDirectory"));
            return;
        }

        var targetName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (isMove)
        {
            await ViewModel.MovePhotosToDirectoryAsync([vm], targetDir, _pageCts.Token);
            await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Moved", 1, targetName));
        }
        else
        {
            await ViewModel.CopyPhotosToDirectoryAsync([vm], targetDir, _pageCts.Token);
            await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Copied", 1, targetName));
        }

        UpdateEmptyState();
    }

    private async Task ExecuteBatchDirectoryActionAsync(
        IReadOnlyList<PhotoItemViewModel> items,
        string targetDir,
        bool isMove)
    {
        if (items.Count == 0) return;
        if (items.Any(item => IsSameDirectory(Path.GetDirectoryName(item.FilePath), targetDir)))
        {
            await ShowTransientToastAsync(L10n.Get("AlbumList_Toast_SameDirectory"));
            return;
        }

        var targetName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!await ConfirmDirectoryActionAsync(items, targetName, isMove, isPhotoListPage: true)) return;

        if (isMove)
        {
            await ViewModel.MovePhotosToDirectoryAsync(items, targetDir, _pageCts.Token);
            await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Moved", items.Count, targetName));
        }
        else
        {
            await ViewModel.CopyPhotosToDirectoryAsync(items, targetDir, _pageCts.Token);
            await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Copied", items.Count, targetName));
        }

        ClearSelectionSafely();
        ExitMultiSelectMode();
        UpdateEmptyState();
    }

    private async Task<bool> ConfirmDeleteAsync(IReadOnlyList<PhotoItemViewModel> items, bool isPhotoListPage)
    {
        if (items.Count == 0) return false;
        if (!ViewModel.ConfirmBeforeDelete) return true;

        var titleKey = isPhotoListPage ? "PhotoList_DeleteConfirm_Title" : "AllPhotos_DeleteConfirm_Title";
        var confirmKey = isPhotoListPage ? "PhotoList_DeleteConfirm_Confirm" : "AllPhotos_DeleteConfirm_Confirm";
        var content = items.Count == 1
            ? L10n.Format("Photo_DeleteConfirm_Single", items[0].FileName)
            : L10n.Format("Photo_DeleteConfirm_Multi", items[0].FileName, items.Count);

        return await ConfirmDialogHelper.ShowAsync(
            XamlRoot,
            L10n.Get(titleKey),
            content,
            L10n.Get(confirmKey),
            confirmStyle: DialogButtonStyle.Danger);
    }

    private async Task<bool> ConfirmDirectoryActionAsync(
        IReadOnlyList<PhotoItemViewModel> items,
        string targetName,
        bool isMove,
        bool isPhotoListPage)
    {
        string contentKey = items.Count == 1
            ? (isMove ? "Photo_MoveConfirm_Single" : "Photo_CopyConfirm_Single")
            : (isMove ? "Photo_MoveConfirm_Multi" : "Photo_CopyConfirm_Multi");

        var content = items.Count == 1
            ? L10n.Format(contentKey, items[0].FileName, targetName)
            : L10n.Format(contentKey, items[0].FileName, items.Count, targetName);

        return await ConfirmDialogHelper.ShowAsync(
            XamlRoot,
            L10n.Get(isMove ? "AlbumList_MoveConfirm_Title" : "AlbumList_CopyConfirm_Title"),
            content,
            L10n.Get(isMove ? "AlbumList_MoveConfirm_Confirm" : "AlbumList_CopyConfirm_Confirm"),
            confirmStyle: DialogButtonStyle.Primary);
    }

    private static MenuFlyoutItem CreateDirectoryMenuItem(string name, string directoryPath)
        => ContextMenuHelper.CreateDirectoryMenuItem(name, directoryPath, ContextMenuMaxWidth);

    private IReadOnlyList<MenuFlyoutItem> CreateInfoItems(PhotoItemViewModel vm)
        => ContextMenuHelper.CreateInfoItems(
            vm.FileName,
            [vm.TakenAtTooltipText, vm.FileSizeFormatted, vm.ResolutionFormatted],
            ContextMenuMaxWidth);

    private IReadOnlyList<PhotoItemViewModel> GetSelectedPhotos()
        => PhotoGridView.SelectedItems.OfType<PhotoItemViewModel>().ToList();

    private void ClearSelectionSafely()
    {
        if (PhotoGridView.SelectionMode == ListViewSelectionMode.None) return;
        if (PhotoGridView.SelectedItems.Count == 0) return;

        PhotoGridView.SelectedItems.Clear();
    }

    private void ExitMultiSelectMode()
    {
        if (ViewModel.IsMultiSelectMode)
            ViewModel.ToggleMultiSelectModeCommand.Execute(null);
    }

    private static bool IsSameDirectory(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> PickSingleFolderAsync()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        var paths = await MultiFolderPicker.PickAsync(hwnd);
        return paths.FirstOrDefault();
    }

    private async Task ShowTransientToastAsync(string text)
    {
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
