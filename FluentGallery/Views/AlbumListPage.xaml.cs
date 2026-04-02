using FluentGallery.ViewModels;
using FluentGallery.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentGallery.Views;

public sealed partial class AlbumListPage : Page
{
    public AlbumListViewModel ViewModel { get; }

    public AlbumListPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<AlbumListViewModel>();
        this.InitializeComponent();
        ApplyItemTemplate();
        Loaded += async (_, _) => await ViewModel.LoadAsync();

        // Refresh empty-state panel when collection changes
        ViewModel.Albums.CollectionChanged += (_, _) => UpdateEmptyState();
    }

    // ── View toggle ───────────────────────────────────────────────────────

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

    // ── Sort ──────────────────────────────────────────────────────────────

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

    // ── Navigate ─────────────────────────────────────────────────────────

    private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumItemViewModel album)
            Frame.Navigate(typeof(PhotoListPage), album.Id);
    }

    // ── Create album ─────────────────────────────────────────────────────

    private async void CreateAlbumButton_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "相册名称", MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title             = "新建相册",
            Content           = nameBox,
            PrimaryButtonText = "创建",
            CloseButtonText   = "取消",
            XamlRoot          = XamlRoot,
            DefaultButton     = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        await ViewModel.CreateAlbumAsync(name);
        UpdateEmptyState();
    }

    // ── Inline rename ─────────────────────────────────────────────────────

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

    // ── Right-click context menu ─────────────────────────────────────────

    // Context menus are attached in XAML via code-behind: we override OnRightTapped
    // on the GridView items' container. Because WinUI 3 DataTemplates cannot easily
    // bind flyouts to per-item commands, we handle it at the GridView level.

    protected override void OnRightTapped(RightTappedRoutedEventArgs e)
    {
        base.OnRightTapped(e);

        // Walk up to find the item container
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
            // Focus the TextBox after UI refresh
            DispatcherQueue.TryEnqueue(() =>
            {
                var container = AlbumGridView.ContainerFromItem(vm) as GridViewItem;
                var box = FindChild<TextBox>(container);
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
        var dialog = new ContentDialog
        {
            Title             = "删除相册",
            Content           = "确定要删除这个相册吗？相册内的照片不会被删除。",
            PrimaryButtonText = "删除",
            CloseButtonText   = "取消",
            XamlRoot          = XamlRoot,
            DefaultButton     = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.DeleteAlbumAsync(vm);
        UpdateEmptyState();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void UpdateEmptyState()
    {
        // The empty-state StackPanel is the third child of the root Grid (index 2).
        // Show it only when not loading AND collection is empty.
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

