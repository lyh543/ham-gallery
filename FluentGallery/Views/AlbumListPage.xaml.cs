using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.ViewModels;
using FluentGallery.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.System;

namespace FluentGallery.Views;

public sealed partial class AlbumListPage : Page
{
    public AlbumListViewModel ViewModel { get; }

    private readonly DatabaseService  _db;
    private readonly ThumbnailService _thumbnails;

    // Cancellation token scoped to the page's active lifetime
    private CancellationTokenSource _pageCts = new();

    // Pinch-gesture tracking
    private double _cumulativeScale = 1.0;

    // Toast state
    private CancellationTokenSource? _toastCts;

    // Coalesce rapid multi-select toggles so GridView mode switches and toolbar relayouts
    // don't repeatedly execute in the same input burst.
    private bool _interactionModeApplyQueued;

    public AlbumListPage()
    {
        ViewModel   = App.Current.Services.GetRequiredService<AlbumListViewModel>();
        _db         = App.Current.Services.GetRequiredService<DatabaseService>();
        _thumbnails = App.Current.Services.GetRequiredService<ThumbnailService>();
        this.InitializeComponent();

        ViewModel.Albums.CollectionChanged += (_, _) => UpdateEmptyState();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AlbumListViewModel.AlbumCardWidth))
            {
                UpdateItemSize();
                if (ViewModel.ShowCardSizeToast)
                    _ = ShowCardSizeToastAsync($"{ViewModel.AlbumCardWidth} px");
            }
            else if (e.PropertyName == nameof(AlbumListViewModel.IsMultiSelectMode))
            {
                QueueInteractionModeApply();
            }
        };

        ApplyToolbarMode();
    }

    // ── Page lifecycle ────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new CancellationTokenSource();
        _cumulativeScale = 1.0;
        ViewModel.ActivatePage();

        Loaded += OnPageLoaded;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Loaded -= OnPageLoaded;
        _pageCts.Cancel();
        _pageCts.Dispose();
        ViewModel.DeactivatePage();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ElasticScrollHelper.Attach(AlbumScrollViewer);
        await ViewModel.LoadAsync();
        ApplySelectionMode();
        UpdateEmptyState();
        UpdateItemSize();
    }

    // ── Lazy cover loading (ContainerContentChanging) ─────────────────────────

    private void AlbumGridView_ContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (args.Item is AlbumItemViewModel vm)
                vm.ClearCover();
            return;
        }

        // Phase 0 → request phase 1 callback
        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(AlbumGridView_ContainerContentChanging);
            return;
        }

        // Phase 1 → trigger cover load
        if (args.Item is AlbumItemViewModel albumVm)
            _ = albumVm.LoadCoverAsync(_db, _thumbnails, ct: _pageCts.Token);
    }

    // ── Zoom buttons ──────────────────────────────────────────────────────────

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.ZoomInCommand.Execute(null);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.ZoomOutCommand.Execute(null);

    // ── GridView: size change → update item size ──────────────────────────────

    private void AlbumGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateItemSize();

    private void UpdateItemSize()
    {
        if (AlbumGridView.ItemsPanelRoot is not ItemsWrapGrid wg) return;
        int w = ViewModel.AlbumCardWidth;
        wg.ItemWidth  = w;
        wg.ItemHeight = w + 40; // info area height
    }

    // ── Pinch gesture ─────────────────────────────────────────────────────────

    private void AlbumGridView_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
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

    private void AlbumScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
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

    // ── Sort ──────────────────────────────────────────────────────────────────

    private void SortButton_Click(object sender, RoutedEventArgs e) { /* flyout opens automatically */ }

    private void SortFlyout_Opening(object? sender, object e)
    {
        SortByName.IsChecked       = ViewModel.SortField == AlbumSortField.Name;
        SortByCreated.IsChecked    = ViewModel.SortField == AlbumSortField.CreatedAt;
        SortByModified.IsChecked   = ViewModel.SortField == AlbumSortField.ModifiedAt;
        SortByPhotoCount.IsChecked = ViewModel.SortField == AlbumSortField.PhotoCount;
        SortByTakenAt.IsChecked    = ViewModel.SortField == AlbumSortField.TakenAt;
        // Toggle shows "升序"; checked = ascending (non-default), unchecked = descending (default)
        SortDescToggle.IsChecked   = ViewModel.SortDirection == SortDirection.Ascending;
    }

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        ViewModel.SortField = item.Tag?.ToString() switch
        {
            "CreatedAt"  => AlbumSortField.CreatedAt,
            "ModifiedAt" => AlbumSortField.ModifiedAt,
            "PhotoCount" => AlbumSortField.PhotoCount,
            "TakenAt"    => AlbumSortField.TakenAt,
            _            => AlbumSortField.Name,
        };
    }

    private void SortDescToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem t) return;
        // Checked = ascending, unchecked = descending
        ViewModel.SortDirection = t.IsChecked ? SortDirection.Ascending : SortDirection.Descending;
    }

    // ── Multi-select ─────────────────────────────────────────────────────────

    private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
        => ViewModel.ToggleMultiSelectModeCommand.Execute(null);

    private void ApplySelectionMode()
    {
        if (ViewModel.IsMultiSelectMode)
        {
            if (AlbumGridView.SelectionMode == ListViewSelectionMode.Multiple && !AlbumGridView.IsItemClickEnabled)
                return;

            AlbumGridView.SelectionMode = ListViewSelectionMode.Multiple;
            AlbumGridView.IsItemClickEnabled = false;
        }
        else
        {
            if (AlbumGridView.SelectionMode == ListViewSelectionMode.None && AlbumGridView.IsItemClickEnabled)
                return;

            ClearSelectionSafely();
            AlbumGridView.SelectionMode = ListViewSelectionMode.None;
            AlbumGridView.IsItemClickEnabled = true;
        }
    }

    private void QueueInteractionModeApply()
    {
        if (_interactionModeApplyQueued) return;
        _interactionModeApplyQueued = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            _interactionModeApplyQueued = false;
            ApplySelectionMode();
            ApplyToolbarMode();
        });
    }

    private void ApplyToolbarMode()
    {
        BrowseCommandsPanel.Visibility = ViewModel.IsMultiSelectMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        BatchCommandsPanel.Visibility = ViewModel.IsMultiSelectMode
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Albums.Count > 0)
            AlbumGridView.SelectAll();
    }

    // ── Navigate ──────────────────────────────────────────────────────────────

    private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.IsMultiSelectMode) return;
        if (e.ClickedItem is AlbumItemViewModel album)
            Frame.Navigate(typeof(PhotoListPage), album.Id);
    }

    // ── Add folders ──────────────────────────────────────────────────────────

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        var paths = await MultiFolderPicker.PickAsync(hwnd);
        if (paths.Count == 0) return;

        await ViewModel.AddScanDirectoriesAsync(paths, _pageCts.Token);
    }

    // ── Inline rename ─────────────────────────────────────────────────────────

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var vm = (AlbumItemViewModel)((FrameworkElement)box).DataContext;

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitRename(vm);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            vm.CancelEdit();
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var vm = (AlbumItemViewModel)((FrameworkElement)box).DataContext;
        if (vm.IsEditing) CommitRename(vm);
    }

    private async void CommitRename(AlbumItemViewModel vm)
    {
        var newName = vm.CommitEdit();
        if (!string.IsNullOrEmpty(newName))
            await ViewModel.RenameAlbumAsync(vm, newName);
    }

    // ── Right-click context menu ──────────────────────────────────────────────

    protected override void OnRightTapped(RightTappedRoutedEventArgs e)
    {
        base.OnRightTapped(e);

        if (e.OriginalSource is not FrameworkElement src) return;
        var vm = FindAlbumVm(src);
        if (vm is null) return;

        e.Handled = true;
        _ = ShowContextMenuAsync(vm, src, e.GetPosition(src));
    }

    private void AlbumGridView_Holding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != Microsoft.UI.Input.HoldingState.Started) return;
        if (e.OriginalSource is not FrameworkElement src) return;

        var vm = FindAlbumVm(src);
        if (vm is null) return;

        e.Handled = true;
        _ = ShowContextMenuAsync(vm, src, e.GetPosition(src));
    }

    private static AlbumItemViewModel? FindAlbumVm(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is FrameworkElement fe && fe.DataContext is AlbumItemViewModel vm)
                return vm;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async Task ShowContextMenuAsync(
        AlbumItemViewModel vm,
        FrameworkElement anchor,
        Windows.Foundation.Point point)
    {
        var flyout = new MenuFlyout();

        var rename = new MenuFlyoutItem
        {
            Text = L10n.Get("AlbumList_Context_Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" },
        };
        rename.Click += async (_, _) => await ShowRenameDialogAsync(vm);

        var pinText = vm.IsPinned ? L10n.Get("AlbumList_Context_Unpin") : L10n.Get("AlbumList_Context_Pin");
        var pinGlyph = vm.IsPinned ? "\uE77A" : "\uE840";
        var pinItem = new MenuFlyoutItem
        {
            Text = pinText,
            Icon = new FontIcon { Glyph = pinGlyph },
        };
        pinItem.Click += async (_, _) =>
        {
            await ViewModel.SetPinnedAsync(vm, !vm.IsPinned);
            await RefreshPinnedAlbumsAsync();
        };

        var directories = await ViewModel.GetAlbumDirectoriesAsync(_pageCts.Token);
        var moveItem = BuildDirectorySubMenu(vm, anchor, point, isMove: true, directories);
        var copyItem = BuildDirectorySubMenu(vm, anchor, point, isMove: false, directories);

        var openInExplorer = new MenuFlyoutItem
        {
            Text = L10n.Get("AlbumList_Context_OpenInExplorer"),
            Icon = new FontIcon { Glyph = "\uE838" },
            IsEnabled = !string.IsNullOrWhiteSpace(vm.DirectoryPath),
        };
        openInExplorer.Click += (_, _) => ViewModel.OpenAlbumInExplorer(vm);

        var excludeItem = new MenuFlyoutItem
        {
            Text = L10n.Get("AlbumList_Context_Exclude"),
            Icon = new FontIcon { Glyph = "\uE738" },
            IsEnabled = !string.IsNullOrWhiteSpace(vm.DirectoryPath),
        };
        excludeItem.Click += async (_, _) => await ExcludeAlbumsWithConfirmAsync([vm]);

        var delete = new MenuFlyoutItem
        {
            Text       = L10n.Get("AlbumList_Context_Delete"),
            Icon       = new FontIcon { Glyph = "\uE74D" },
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        delete.Click += async (_, _) => await DeleteAlbumsWithConfirmAsync([vm]);

        flyout.Items.Add(rename);
        flyout.Items.Add(pinItem);
        flyout.Items.Add(moveItem);
        flyout.Items.Add(copyItem);
        flyout.Items.Add(openInExplorer);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(excludeItem);
        flyout.Items.Add(delete);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateInfoItem(vm));
        flyout.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position  = point,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
        => await DeleteAlbumsWithConfirmAsync(GetSelectedAlbums());

    private async void BatchExclude_Click(object sender, RoutedEventArgs e)
        => await ExcludeAlbumsWithConfirmAsync(GetSelectedAlbums());

    private async void BatchMove_Click(object sender, RoutedEventArgs e)
        => await ShowBatchDirectoryFlyoutAsync(BatchMoveButton, isMove: true);

    private async void BatchCopy_Click(object sender, RoutedEventArgs e)
        => await ShowBatchDirectoryFlyoutAsync(BatchCopyButton, isMove: false);

    private async Task DeleteAlbumsWithConfirmAsync(IReadOnlyList<AlbumItemViewModel> items)
    {
        if (items.Count == 0) return;

        var totalPhotos = items.Sum(item => item.PhotoCount);
        var content = items.Count == 1
            ? L10n.Format("AlbumList_DeleteConfirm_Content_WithCount", items[0].Name, totalPhotos)
            : L10n.Format("AlbumList_BatchDeleteConfirm_Content", items.Count, totalPhotos);

        if (!await ConfirmDialogHelper.ShowAsync(
                XamlRoot,
            L10n.Get("AlbumList_DeleteConfirm_Title"),
            content,
            L10n.Get("AlbumList_DeleteConfirm_Confirm"),
                confirmStyle: DialogButtonStyle.Danger)) return;

        int deletedCount = await ViewModel.DeleteAlbumsAsync(items, _pageCts.Token);
        ClearSelectionSafely();
        if (deletedCount > 0)
            await ShowTransientToastAsync(L10n.Get("AlbumList_Toast_Deleted"));
        UpdateEmptyState();
        await RefreshPinnedAlbumsAsync();
    }

    private async Task ExcludeAlbumsWithConfirmAsync(IReadOnlyList<AlbumItemViewModel> items)
    {
        if (items.Count == 0) return;

        var content = items.Count == 1
            ? L10n.Format("AlbumList_ExcludeConfirm_Content", items[0].Name)
            : L10n.Format("AlbumList_BatchExcludeConfirm_Content", items.Count);

        if (!await ConfirmDialogHelper.ShowAsync(
                XamlRoot,
                L10n.Get("AlbumList_ExcludeConfirm_Title"),
                content,
                L10n.Get("AlbumList_ExcludeConfirm_Confirm"),
                confirmStyle: DialogButtonStyle.Primary)) return;

        await ViewModel.ExcludeAlbumsAsync(items, _pageCts.Token);
        ClearSelectionSafely();
        await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Excluded", items.Count));
        UpdateEmptyState();
        await RefreshPinnedAlbumsAsync();
    }

    private async Task ShowBatchDirectoryFlyoutAsync(AppBarButton anchor, bool isMove)
    {
        var selected = GetSelectedAlbums();
        if (selected.Count == 0) return;

        var directories = await ViewModel.GetAlbumDirectoriesAsync(_pageCts.Token);
        var excluded = selected
            .Select(item => item.DirectoryPath)
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyList<AlbumItemViewModel> GetSelectedAlbums()
        => AlbumGridView.SelectedItems.OfType<AlbumItemViewModel>().ToList();

    private void ClearSelectionSafely()
    {
        if (AlbumGridView.SelectionMode == ListViewSelectionMode.None) return;
        if (AlbumGridView.SelectedItems.Count == 0) return;

        try
        {
            AlbumGridView.SelectedItems.Clear();
        }
        catch (COMException)
        {
            // WinUI may throw while the backing selection vector is changing after item removal.
        }
    }

    private MenuFlyoutSubItem BuildDirectorySubMenu(
        AlbumItemViewModel vm,
        FrameworkElement anchor,
        Windows.Foundation.Point point,
        bool isMove,
        IReadOnlyList<(string Name, string DirectoryPath)> directories)
    {
        var subItem = new MenuFlyoutSubItem
        {
            Text = L10n.Get(isMove ? "AlbumList_Context_Move" : "AlbumList_Context_Copy"),
            Icon = new FontIcon { Glyph = isMove ? "\uE8DE" : "\uE8C8" },
        };

        foreach (var directory in directories.Where(d => !string.Equals(d.DirectoryPath, vm.DirectoryPath, StringComparison.OrdinalIgnoreCase)))
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

            if (IsSameDirectory(vm.DirectoryPath, targetDir))
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

    private async Task ExecuteSingleDirectoryActionAsync(AlbumItemViewModel vm, string targetDir, bool isMove)
    {
        if (IsSameDirectory(vm.DirectoryPath, targetDir))
        {
            await ShowTransientToastAsync(L10n.Get("AlbumList_Toast_SameDirectory"));
            return;
        }

        var targetName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool confirmed = await ConfirmDirectoryActionAsync([vm], targetName, isMove);
        if (!confirmed) return;

        if (isMove)
        {
            await ViewModel.MoveAlbumPhotosAsync(vm, targetDir, _pageCts.Token);
            await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Moved", vm.PhotoCount, targetName));
            await RefreshPinnedAlbumsAsync();
        }
        else
        {
            await ViewModel.CopyAlbumPhotosAsync(vm, targetDir, _pageCts.Token);
            await ShowTransientToastAsync(L10n.Format("AlbumList_Toast_Copied", vm.PhotoCount, targetName));
        }

        UpdateEmptyState();
    }

    private async Task ExecuteBatchDirectoryActionAsync(
        IReadOnlyList<AlbumItemViewModel> items,
        string targetDir,
        bool isMove)
    {
        if (items.Count == 0) return;

        if (items.Any(item => IsSameDirectory(item.DirectoryPath, targetDir)))
        {
            await ShowTransientToastAsync(L10n.Get("AlbumList_Toast_SameDirectory"));
            return;
        }

        var targetName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool confirmed = await ConfirmDirectoryActionAsync(items, targetName, isMove);
        if (!confirmed) return;

        int totalPhotos = items.Sum(item => item.PhotoCount);
        foreach (var item in items)
        {
            if (isMove)
                await ViewModel.MoveAlbumPhotosAsync(item, targetDir, _pageCts.Token);
            else
                await ViewModel.CopyAlbumPhotosAsync(item, targetDir, _pageCts.Token);
        }

        ClearSelectionSafely();
        await ShowTransientToastAsync(L10n.Format(
            isMove ? "AlbumList_Toast_Moved" : "AlbumList_Toast_Copied",
            totalPhotos,
            targetName));
        UpdateEmptyState();
        if (isMove)
            await RefreshPinnedAlbumsAsync();
    }

    private async Task<bool> ConfirmDirectoryActionAsync(
        IReadOnlyList<AlbumItemViewModel> items,
        string targetName,
        bool isMove)
    {
        int totalPhotos = items.Sum(item => item.PhotoCount);
        string titleKey = isMove ? "AlbumList_MoveConfirm_Title" : "AlbumList_CopyConfirm_Title";
        string contentKey = items.Count == 1
            ? (isMove ? "AlbumList_MoveConfirm_Content" : "AlbumList_CopyConfirm_Content")
            : (isMove ? "AlbumList_BatchMoveConfirm_Content" : "AlbumList_BatchCopyConfirm_Content");
        string confirmKey = isMove ? "AlbumList_MoveConfirm_Confirm" : "AlbumList_CopyConfirm_Confirm";

        string content = items.Count == 1
            ? L10n.Format(contentKey, items[0].Name, totalPhotos, targetName)
            : L10n.Format(contentKey, items.Count, totalPhotos, targetName);

        return await ConfirmDialogHelper.ShowAsync(
            XamlRoot,
            L10n.Get(titleKey),
            content,
            L10n.Get(confirmKey),
            confirmStyle: DialogButtonStyle.Primary);
    }

    private static MenuFlyoutItem CreateDirectoryMenuItem(string name, string directoryPath)
    {
        var item = new MenuFlyoutItem { Text = name };
        ToolTipService.SetToolTip(item, directoryPath);
        return item;
    }

    private async Task ShowRenameDialogAsync(AlbumItemViewModel vm)
    {
        var input = new TextBox
        {
            Text = vm.Name,
            PlaceholderText = L10n.Get("AlbumList_RenameDialog_Placeholder"),
            MinWidth = 320,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L10n.Get("AlbumList_RenameDialog_Title"),
            Content = input,
            PrimaryButtonText = L10n.Get("AlbumList_RenameDialog_Confirm"),
            CloseButtonText = L10n.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(vm.Name),
        };

        input.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, vm.Name, StringComparison.CurrentCulture))
            return;

        try
        {
            await ViewModel.RenameAlbumAsync(vm, newName, _pageCts.Token);
            if (vm.IsPinned)
                await RefreshPinnedAlbumsAsync();
        }
        catch
        {
            await ShowTransientToastAsync(L10n.Get("AlbumList_Toast_RenameFailed"));
        }
    }

    private MenuFlyoutItem CreateInfoItem(AlbumItemViewModel vm)
    {
        return new MenuFlyoutItem
        {
            Text = $"{vm.PhotoCountFormatted}  ·  {vm.TotalSizeFormatted}  ·  {vm.CreatedAtFormatted}",
            IsEnabled = false,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
    }

    private static bool IsSameDirectory(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                         right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                         StringComparison.OrdinalIgnoreCase);

    private static Task RefreshPinnedAlbumsAsync(CancellationToken ct = default)
        => App.Current.MainWindow is global::FluentGallery.MainWindow window
            ? window.RefreshPinnedAlbumsAsync(ct)
            : Task.CompletedTask;

    private static async Task<string?> PickSingleFolderAsync()
    {
        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        var paths = await MultiFolderPicker.PickAsync(hwnd);
        return paths.FirstOrDefault();
    }

    private void UpdateEmptyState()
    {
        EmptyStatePanel.Visibility = !ViewModel.IsLoading && ViewModel.Albums.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindChild<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    // ── Card size toast ───────────────────────────────────────────────────────

    private async Task ShowCardSizeToastAsync(string text)
        => await ShowTransientToastAsync(text);

    private async Task ShowTransientToastAsync(string text)
    {
        // Cancel any previous auto-dismiss so only one timer runs at a time
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var ct = _toastCts.Token;

        CardSizeToastText.Text = text;

        // Snap opacity to 0.85 immediately (no fade-in delay)
        CardSizeToast.Opacity = 0.85;

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
