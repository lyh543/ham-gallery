using FluentGallery.Data;
using FluentGallery.Helpers;
using FluentGallery.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    private bool _suppressFilmStripChange = false;
    private readonly HashSet<int> _loadedFilmstripIndices = new();
    private readonly object _filmstripLoadLock = new();

    private bool _filmstripDragging = false;
    private bool _filmstripPointerCaptured = false;
    private double _filmstripLastX = 0;
    private Point _filmstripDragStart = default;

    private void FilmStripPin_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.ToggleFilmStripPinnedAsync(_cts.Token);
        ApplyFilmStripPinState();
    }

    private void ApplyFilmStripPinState()
    {
        bool available = ViewModel.IsFilmStripAvailable;
        bool pinned = available && ViewModel.FilmStripPinned;
        FilmStripPinButton.IsChecked = pinned;
        FilmStripRow.Height = pinned ? GridLength.Auto : new GridLength(0);
    }

    private void FilmStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filmstripDragging) return;
        if (_suppressFilmStripChange) return;
        if (FilmStrip.SelectedIndex < 0) return;
        _ = ViewModel.NavigateToIndexAsync(FilmStrip.SelectedIndex, _cts.Token);
    }

    private void SyncFilmStripSelection()
    {
        _suppressFilmStripChange = true;
        try
        {
            FilmStrip.SelectedIndex = ViewModel.CurrentIndex;
            FilmStrip.ScrollIntoView(FilmStrip.SelectedItem);
        }
        finally
        {
            _suppressFilmStripChange = false;
        }
    }

    private void FilmStrip_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
            return;

        int index = args.ItemIndex;
        if (index < 0 || index >= ViewModel.FilmStripItems.Count)
            return;

        lock (_filmstripLoadLock)
        {
            if (_loadedFilmstripIndices.Contains(index))
                return;
            _loadedFilmstripIndices.Add(index);
        }

        _ = LoadFilmstripThumbnailAsync(index);
    }

    private async Task LoadFilmstripThumbnailAsync(int index)
    {
        try
        {
            if (index < 0 || index >= ViewModel.FilmStripItems.Count)
                return;

            var item = ViewModel.FilmStripItems[index];
            if (!string.IsNullOrEmpty(item.ThumbPath))
                return;

            var photo = item.Photo;
            if (photo == null || string.IsNullOrEmpty(photo.FilePath))
                return;

            if (!File.Exists(photo.FilePath))
                return;

            if (photo.Id == 0)
                return;

            var thumbnail = App.Current.Services.GetRequiredService<ThumbnailService>();
            var thumbPath = await Task.Run(
                () => thumbnail.GetOrCreateThumbnailAsync(photo, _cts.Token),
                _cts.Token);

            if (thumbPath is not null && index < ViewModel.FilmStripItems.Count)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (index < ViewModel.FilmStripItems.Count)
                    {
                        ViewModel.FilmStripItems[index].ThumbPath = PhotoThumbItem.CreateDisplayThumbPath(thumbPath);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Filmstrip thumb load failed for index {Index}", index);
        }
    }

    private void FilmStrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
            return;

        var pt = e.GetCurrentPoint(FilmStrip);
        if (!pt.Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != PointerDeviceType.Pen)
            return;

        _filmstripDragStart = pt.Position;
        _filmstripLastX = _filmstripDragStart.X;
        _filmstripDragging = false;
        _filmstripPointerCaptured = false;
    }

    private void FilmStrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
            return;

        var pt = e.GetCurrentPoint(FilmStrip);
        if (!pt.Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != PointerDeviceType.Pen)
        {
            if (_filmstripDragging)
            {
                _filmstripDragging = false;
                if (_filmstripPointerCaptured)
                {
                    FilmStrip.ReleasePointerCapture(e.Pointer);
                    _filmstripPointerCaptured = false;
                }
            }
            return;
        }

        double currentX = pt.Position.X;
        double delta = currentX - _filmstripLastX;

        if (!_filmstripDragging && Math.Abs(currentX - _filmstripDragStart.X) > 5)
        {
            _filmstripDragging = true;
            _filmstripPointerCaptured = FilmStrip.CapturePointer(e.Pointer);
        }

        if (_filmstripDragging && Math.Abs(delta) > 0.5)
        {
            var scrollViewer = FindScrollViewer(FilmStrip);
            if (scrollViewer != null)
            {
                double newOffset = scrollViewer.HorizontalOffset - delta;
                newOffset = Math.Max(0, Math.Min(newOffset, scrollViewer.ScrollableWidth));
                scrollViewer.ChangeView(newOffset, null, null, disableAnimation: true);
            }
            e.Handled = true;
        }

        _filmstripLastX = currentX;
    }

    private void FilmStrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_filmstripDragging)
        {
            e.Handled = true;
        }

        _filmstripDragging = false;
        if (_filmstripPointerCaptured)
        {
            FilmStrip.ReleasePointerCapture(e.Pointer);
            _filmstripPointerCaptured = false;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }
}
