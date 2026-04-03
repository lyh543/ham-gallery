using FluentGallery.Data;
using FluentGallery.Models;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace FluentGallery.Views;

public sealed partial class SearchPage : Page
{
    // ── ViewModel ─────────────────────────────────────────────────────────────

    public SearchViewModel ViewModel { get; }

    // ── Page-level cancellation ───────────────────────────────────────────────

    private CancellationTokenSource _pageCts = new();

    // ── Pinch-gesture tracking ────────────────────────────────────────────────

    private double _cumulativeScale = 1.0;

    // ── Construction ──────────────────────────────────────────────────────────

    public SearchPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<SearchViewModel>();
        this.InitializeComponent();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchViewModel.ColumnCount))
                UpdateItemSize();
            else if (e.PropertyName == nameof(SearchViewModel.Results))
                UpdateStates();
            else if (e.PropertyName == nameof(SearchViewModel.HasSearched))
                UpdateStates();
            else if (e.PropertyName == nameof(SearchViewModel.IsLoading))
                UpdateStates();
        };

        ViewModel.Results.CollectionChanged += (_, _) => UpdateStates();

        // Default date-field selector to first item (TakenAt)
        DateFieldCombo.SelectedIndex = 0;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts       = new CancellationTokenSource();
        _cumulativeScale = 1.0;

        // Reset state so each navigation starts fresh
        ViewModel.AlbumId    = null;
        ViewModel.AlbumName  = null;
        ViewModel.Keyword    = string.Empty;
        ViewModel.DateFrom   = null;
        ViewModel.DateTo     = null;
        ViewModel.HasSearched = false;
        ViewModel.Results.Clear();
        DateFromPicker.Date  = null;
        DateToPicker.Date    = null;
        DateFieldCombo.SelectedIndex = 0;

        if (e.Parameter is SearchArgs args)
        {
            ViewModel.AlbumId   = args.AlbumId;
            ViewModel.AlbumName = args.AlbumName;
        }

        // Show/hide scope hint
        if (ViewModel.AlbumId.HasValue && !string.IsNullOrEmpty(ViewModel.AlbumName))
        {
            ScopeHintText.Text       = $"搜索范围：相册「{ViewModel.AlbumName}」";
            ScopeHintText.Visibility = Visibility.Visible;
        }
        else
        {
            ScopeHintText.Visibility = Visibility.Collapsed;
        }

        // Sync nav header
        UpdateNavHeader();
        UpdateStates();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _pageCts.Cancel();
        _pageCts.Dispose();
    }

    // ── Filter controls ───────────────────────────────────────────────────────

    private void DateFieldCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DateFieldCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            ViewModel.DateField = tag;
    }

    private void DateFromPicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        => ViewModel.DateFrom = args.NewDate;

    private void DateToPicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        => ViewModel.DateTo = args.NewDate;

    private void ClearDates_Click(object sender, RoutedEventArgs e)
    {
        DateFromPicker.Date = null;
        DateToPicker.Date   = null;
        ViewModel.DateFrom  = null;
        ViewModel.DateTo    = null;
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void KeywordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
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

        await ViewModel.SearchAsync(_pageCts.Token);
        UpdateStates();
    }

    // ── GridView: lazy thumbnail loading ─────────────────────────────────────

    private void ResultsGridView_ContainerContentChanging(
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
            args.RegisterUpdateCallback(ResultsGridView_ContainerContentChanging);
            return;
        }

        if (args.Item is PhotoItemViewModel photoVm)
            _ = photoVm.LoadThumbnailAsync(
                    App.Current.Services.GetRequiredService<ThumbnailService>(),
                    _pageCts.Token);
    }

    // ── GridView: item click ──────────────────────────────────────────────────

    private void ResultsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PhotoItemViewModel photo)
        {
            var index = ViewModel.Results.IndexOf(photo);
            Frame.Navigate(
                typeof(PhotoDetailPage),
                new PhotoDetailArgs(ViewModel.Results.Select(p => p.GetPhoto()).ToList(), index));
        }
    }

    // ── GridView: size → item size ────────────────────────────────────────────

    private void ResultsGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateItemSize();

    private void UpdateItemSize()
    {
        if (ResultsGridView.ItemsPanelRoot is not ItemsWrapGrid wg) return;
        double available = Math.Max(1, ResultsGridView.ActualWidth - 8);
        double size      = Math.Floor(available / ViewModel.ColumnCount);
        wg.ItemWidth  = size;
        wg.ItemHeight = size;
    }

    // ── Pinch zoom ────────────────────────────────────────────────────────────

    private void ResultsGridView_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        _cumulativeScale *= e.Delta.Scale;

        if (_cumulativeScale < 0.75)
        {
            ViewModel.AdjustColumnCount(+1);
            _cumulativeScale = 1.0;
        }
        else if (_cumulativeScale > 1.35)
        {
            ViewModel.AdjustColumnCount(-1);
            _cumulativeScale = 1.0;
        }
    }

    // ── State helpers ─────────────────────────────────────────────────────────

    private void UpdateStates()
    {
        bool loading   = ViewModel.IsLoading;
        bool searched  = ViewModel.HasSearched;
        int  count     = ViewModel.Results.Count;

        // Prompt: before any search
        PromptPanel.Visibility = (!searched && !loading)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Empty: searched but no results
        EmptyStatePanel.Visibility = (searched && !loading && count == 0)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Result count bar text
        if (searched && !loading)
        {
            var scope = ViewModel.AlbumId.HasValue
                ? $"「{ViewModel.AlbumName}」相册中"
                : "全库";
            ResultCountText.Text = $"在{scope}找到 {count} 张照片";
        }
    }

    private void UpdateNavHeader()
    {
        var header = ViewModel.AlbumId.HasValue
            ? $"搜索 · {ViewModel.AlbumName}"
            : "搜索";

        if (App.Current.MainWindow is MainWindow mw)
            mw.SetNavHeader(header);
    }
}
