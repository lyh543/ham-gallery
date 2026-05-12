using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.Models;
using FluentGallery.Services;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;

namespace FluentGallery.Views;

public sealed partial class AllPhotosPage : Page
{
    // ── ViewModel ─────────────────────────────────────────────────────────────

    public AllPhotosViewModel ViewModel { get; }

    // ── Page-level cancellation ───────────────────────────────────────────────

    private CancellationTokenSource _pageCts = new();

    // ── Pinch-gesture tracking ────────────────────────────────────────────────

    private double _cumulativeScale = 1.0;

    // ── Toast state ───────────────────────────────────────────────────────────

    private CancellationTokenSource? _toastCts;
    private readonly ThumbnailRefreshService _thumbnailRefreshService;
    private bool _thumbnailRefreshSubscribed;

    // ── Construction ──────────────────────────────────────────────────────────

    public AllPhotosPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<AllPhotosViewModel>();
        _thumbnailRefreshService = App.Current.Services.GetRequiredService<ThumbnailRefreshService>();
        this.InitializeComponent();

        // Update GridView item size when card width changes
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AllPhotosViewModel.AllPhotosCardWidth))
            {
                UpdateItemSize();
                if (ViewModel.ShowCardSizeToast)
                    _ = ShowCardSizeToastAsync($"{ViewModel.AllPhotosCardWidth} px");
            }
            else if (e.PropertyName == nameof(AllPhotosViewModel.IsMultiSelectMode))
                ApplySelectionMode();
        };
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

        Loaded += async (_, _) =>
        {
            ElasticScrollHelper.Attach(PhotoGridView);
            await ViewModel.LoadAsync(_pageCts.Token);
            UpdateEmptyState();
            UpdateItemSize();
        };
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
            var item = ViewModel.AllPhotoItems.FirstOrDefault(photo => photo.Id == e.PhotoId);
            item?.RefreshThumbnail(e.ThumbPath, e.ModifiedAt);
        });
    }

    // ── GridView: lazy thumbnail loading ─────────────────────────────────────

    private void PhotoGridView_ContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (args.Item is PhotoItemViewModel vm)
                vm.ClearThumbnail();
            return;
        }

        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(PhotoGridView_ContainerContentChanging);
            return;
        }

        if (args.Item is PhotoItemViewModel photoVm)
            _ = photoVm.LoadThumbnailAsync(
                    App.Current.Services.GetRequiredService<ThumbnailService>(),
                    _pageCts.Token);
    }

    // ── GridView: item click (navigate to PhotoDetailPage) ───────────────────

    private void PhotoGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.IsMultiSelectMode) return;
        if (e.ClickedItem is PhotoItemViewModel photo && sender is GridView gridview)
        {
            var allPhotos = ViewModel.GetAllPhotosForDetail();
            var index = allPhotos.FindIndex(p => p.Id == photo.Id);
            if (index >= 0 && App.Current.MainWindow is MainWindow mw)
                mw.ShowPhotoDetail(new PhotoDetailArgs(allPhotos, index));
        }
    }

    // ── GridView: size change → update item size ──────────────────────────────

    private void PhotoGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateItemSize();

    private void UpdateItemSize()
    {
        if (PhotoGridView.ItemsPanelRoot is not ItemsWrapGrid wg) return;
        double size = ViewModel.AllPhotosCardWidth;
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
                    L10n.Get("AllPhotos_DeleteConfirm_Title"),
                    L10n.Format("AllPhotos_DeleteConfirm_Content", selected.Count),
                    L10n.Get("AllPhotos_DeleteConfirm_Confirm"),
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

        // Build a flyout with one item per album
        var flyout = new MenuFlyout();
        foreach (var album in albums)
        {
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
            flyout.Items.Add(new MenuFlyoutItem { Text = L10n.Get("AllPhotos_MoveToAlbum_Empty"), IsEnabled = false });

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

    // ── Search ────────────────────────────────────────────────────────────────

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = SearchToggle.IsChecked;
        SearchPanel.Visibility = (isChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        if (isChecked == true)
            KeywordBox.Focus(FocusState.Programmatic);
    }

    private void KeywordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = RunSearchAsync();
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
        => _ = RunSearchAsync();

    private async Task RunSearchAsync()
    {
        if (ViewModel.IsLoading) return;

        _pageCts.Cancel();
        _pageCts.Dispose();
        _pageCts = new CancellationTokenSource();

        ViewModel.SearchDateFrom = DateFromPicker.Date?.ToString("yyyy-MM-dd");
        ViewModel.SearchDateTo   = DateToPicker.Date?.ToString("yyyy-MM-dd");

        await ViewModel.SearchAsync(_pageCts.Token);
        UpdateEmptyState();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _ = ClearSearchAsync();
    }

    private async Task ClearSearchAsync()
    {
        KeywordBox.Text = string.Empty;
        DateFromPicker.Date = null;
        DateToPicker.Date   = null;
        SearchToggle.IsChecked = false;
        SearchPanel.Visibility = Visibility.Collapsed;

        await ViewModel.ClearSearchAsync(_pageCts.Token);
        UpdateEmptyState();
    }

    private void DateFromPicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        // Just track the date; actual search triggered by Search button
    }

    private void DateToPicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        // Just track the date; actual search triggered by Search button
    }

    // ── Empty state ───────────────────────────────────────────────────────────

    private void UpdateEmptyState()
    {
        bool isEmpty = !ViewModel.IsLoading && ViewModel.Groups.Count == 0;
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
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
