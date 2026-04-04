using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.ViewModels;
using FluentGallery.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace FluentGallery.Views;

public sealed partial class AlbumListPage : Page
{
    public AlbumListViewModel ViewModel { get; }

    private readonly DatabaseService  _db;
    private readonly ThumbnailService _thumbnails;

    // Cancellation token scoped to the page's active lifetime
    private CancellationTokenSource _pageCts = new();

    public AlbumListPage()
    {
        ViewModel   = App.Current.Services.GetRequiredService<AlbumListViewModel>();
        _db         = App.Current.Services.GetRequiredService<DatabaseService>();
        _thumbnails = App.Current.Services.GetRequiredService<ThumbnailService>();
        this.InitializeComponent();
        ApplyItemTemplate();

        ViewModel.Albums.CollectionChanged += (_, _) => UpdateEmptyState();
    }

    // ── Page lifecycle ────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new CancellationTokenSource();
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
        await ViewModel.LoadAsync();
        UpdateEmptyState();
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

    // ── View toggle ───────────────────────────────────────────────────────────

    private void ToggleViewButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleViewCommand.Execute(null);
        ApplyItemTemplate();
    }

    private void ApplyItemTemplate()
    {
        AlbumGridView.ItemTemplate = ViewModel.IsLargeView
            ? (DataTemplate)Resources["AlbumCardLargeTemplate"]
            : (DataTemplate)Resources["AlbumCardSmallTemplate"];
    }

    // ── Sort ──────────────────────────────────────────────────────────────────

    private void SortButton_Click(object sender, RoutedEventArgs e) { /* flyout opens automatically */ }

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        ViewModel.SortField = item.Tag?.ToString() switch
        {
            "CreatedAt"  => AlbumSortField.CreatedAt,
            "ModifiedAt" => AlbumSortField.ModifiedAt,
            "PhotoCount" => AlbumSortField.PhotoCount,
            _            => AlbumSortField.Name,
        };
    }

    private void SortDescToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem t) return;
        ViewModel.SortDirection = t.IsChecked ? SortDirection.Descending : SortDirection.Ascending;
    }

    // ── Navigate ──────────────────────────────────────────────────────────────

    private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumItemViewModel album)
            Frame.Navigate(typeof(PhotoListPage), album.Id);
    }

    // ── Create album ──────────────────────────────────────────────────────────

    private async void CreateAlbumButton_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "相册名称", MinWidth = 280 };

        if (!await ConfirmDialogHelper.ShowAsync(
                XamlRoot, "新建相册", nameBox, "创建")) return;
        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        await ViewModel.CreateAlbumAsync(name);
        UpdateEmptyState();
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
        ShowContextMenu(vm, src, e.GetPosition(src));
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

    private void ShowContextMenu(AlbumItemViewModel vm, FrameworkElement anchor, Windows.Foundation.Point point)
    {
        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += (_, _) =>
        {
            vm.BeginEdit();
            DispatcherQueue.TryEnqueue(() =>
            {
                var container = AlbumGridView.ContainerFromItem(vm) as GridViewItem;
                var box       = FindChild<TextBox>(container);
                box?.Focus(FocusState.Programmatic);
                box?.SelectAll();
            });
        };

        var pinText = vm.IsPinned ? "取消固定" : "固定到导航栏";
        var pinItem = new MenuFlyoutItem { Text = pinText };
        pinItem.Click += async (_, _) => await ViewModel.SetPinnedAsync(vm, !vm.IsPinned);

        var delete = new MenuFlyoutItem
        {
            Text       = "删除",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        delete.Click += async (_, _) => await ConfirmDeleteAsync(vm);

        var flyout = new MenuFlyout();
        flyout.Items.Add(rename);
        flyout.Items.Add(pinItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(delete);
        flyout.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position  = point,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    private async Task ConfirmDeleteAsync(AlbumItemViewModel vm)
    {
        if (!await ConfirmDialogHelper.ShowAsync(
                XamlRoot,
                "删除相册",
                "确定要删除这个相册吗？相册内的照片不会被删除。",
                "删除",
                confirmStyle: DialogButtonStyle.Danger)) return;
        await ViewModel.DeleteAlbumAsync(vm);
        UpdateEmptyState();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateEmptyState()
    {
        if (Content is not Grid root || root.Children.Count < 3) return;
        var emptyPanel = root.Children[2] as FrameworkElement;
        if (emptyPanel is null) return;
        emptyPanel.Visibility = !ViewModel.IsLoading && ViewModel.Albums.Count == 0
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
}
