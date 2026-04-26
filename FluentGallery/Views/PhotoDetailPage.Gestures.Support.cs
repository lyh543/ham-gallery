using FluentGallery.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    private void TouchSwipeOverlay_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            _mouseOverlayDragging = false;
            ReleaseTouchOverlayPointer(e.Pointer.PointerId);
            return;
        }

        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
            return;

        _touchPointers.Remove(e.Pointer.PointerId);
        ReleaseTouchOverlayPointer(e.Pointer.PointerId);

        if (_touchSwipePointerId == e.Pointer.PointerId)
            EndTouchSwipeState();

        if (_touchPointers.Count < 2)
        {
            _touchPinching = false;
            _touchPinchStartDistance = 0;
            _touchPinchStartZoom = 1;
        }

        _logger.LogDebug("Touch overlay cancelled: pointer={PointerId}", e.Pointer.PointerId);
        e.Handled = true;
    }

    private void HandleTouchTap(Point point)
    {
        DateTime now = DateTime.UtcNow;
        double dt = (now - _lastTouchTapTime).TotalMilliseconds;
        double dx = point.X - _lastTouchTapPosition.X;
        double dy = point.Y - _lastTouchTapPosition.Y;

        if (dt <= 350 && Math.Sqrt(dx * dx + dy * dy) <= 48)
        {
            _lastTouchTapTime = DateTime.MinValue;
            _logger.LogDebug("Touch overlay double tap: pos=({X:F1},{Y:F1})", point.X, point.Y);
            ZoomImage.ToggleZoomAt(point);
            return;
        }

        _lastTouchTapTime = now;
        _lastTouchTapPosition = point;
    }

    private void HandleMouseTap(Point point)
    {
        DateTime now = DateTime.UtcNow;
        double dt = (now - _lastMouseTapTime).TotalMilliseconds;
        double dx = point.X - _lastMouseTapPosition.X;
        double dy = point.Y - _lastMouseTapPosition.Y;

        if (dt <= 350 && Math.Sqrt(dx * dx + dy * dy) <= 6)
        {
            _lastMouseTapTime = DateTime.MinValue;
            _logger.LogDebug("Mouse overlay double click: pos=({X:F1},{Y:F1})", point.X, point.Y);
            ZoomImage.ToggleZoomAt(point);
            return;
        }

        _lastMouseTapTime = now;
        _lastMouseTapPosition = point;
    }

    private void CancelTouchSwipePreview()
    {
        if (_touchSwipePreviewActive)
            OnZoomImageSwipePreviewCompleted();

        _touchSwipeDragging = false;
        _touchSwipePreviewActive = false;
        _touchSwipePointerId = null;
    }

    private void EndTouchSwipeState()
    {
        CancelTouchSwipePreview();
    }

    private double GetTouchPinchDistance()
    {
        if (_touchPointers.Count < 2) return 0;
        var points = _touchPointers.Values.Take(2).ToArray();
        double dx = points[0].X - points[1].X;
        double dy = points[0].Y - points[1].Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private Point GetTouchPinchCenter()
    {
        var points = _touchPointers.Values.Take(2).ToArray();
        return new Point((points[0].X + points[1].X) / 2.0, (points[0].Y + points[1].Y) / 2.0);
    }

    private void ReleaseTouchOverlayPointer(uint pointerId)
    {
        var captured = TouchSwipeOverlay.PointerCaptures.FirstOrDefault(c => c.PointerId == pointerId);
        if (captured is not null)
            TouchSwipeOverlay.ReleasePointerCapture(captured);
    }

    private void OnZoomImageSwipeLeft()
    {
        _logger.LogDebug("PhotoDetail swipe event: next photo from index {CurrentIndex}", ViewModel.CurrentIndex);
        _ = NavigateRelativeAsync(1);
    }

    private void OnZoomImageSwipeRight()
    {
        _logger.LogDebug("PhotoDetail swipe event: previous photo from index {CurrentIndex}", ViewModel.CurrentIndex);
        _ = NavigateRelativeAsync(-1);
    }

    private async Task NavigateRelativeAsync(int delta)
    {
        int targetIndex = ViewModel.CurrentIndex + delta;
        _logger.LogDebug(
            "NavigateRelativeAsync: current={CurrentIndex}, delta={Delta}, target={TargetIndex}, count={Count}",
            ViewModel.CurrentIndex,
            delta,
            targetIndex,
            ViewModel.FilmStripItems.Count);

        if (targetIndex < 0 || targetIndex >= ViewModel.FilmStripItems.Count)
        {
            _logger.LogDebug("NavigateRelativeAsync blocked by boundary");
            ShowEdgeBoundaryToast(delta < 0);
            return;
        }

        await ViewModel.NavigateToIndexAsync(targetIndex, _cts.Token);
    }

    private void ShowEdgeBoundaryToast(bool isFirst)
    {
        if (_edgeBoundaryThrottleActive)
            return;

        ShowToast(isFirst ? "已经是第一张照片了" : "已经是最后一张照片了", ToastKind.Normal, showUndo: false);
        _edgeBoundaryThrottleActive = true;
        _edgeBoundaryThrottle.Stop();
        _edgeBoundaryThrottle.Start();
    }

    private void OnZoomImageSwipePreviewProgress(SwipePreviewEventArgs args)
    {
        int direction = args.HorizontalOffset < 0 ? 1 : -1;
        int targetIndex = ViewModel.CurrentIndex + direction;
        _logger.LogDebug(
            "Swipe preview progress: current={CurrentIndex}, target={TargetIndex}, offset={Offset:F1}, viewport={Viewport:F1}",
            ViewModel.CurrentIndex,
            targetIndex,
            args.HorizontalOffset,
            args.ViewportWidth);

        if (targetIndex < 0 || targetIndex >= ViewModel.FilmStripItems.Count)
        {
            _logger.LogDebug("Swipe preview blocked by boundary");
            ResetSwipePreviewTransforms();
            return;
        }

        EnsureSwipePreviewImage(targetIndex);

        double viewportWidth = args.ViewportWidth > 0 ? args.ViewportWidth : ImageViewport.ActualWidth;
        if (viewportWidth <= 0)
        {
            _logger.LogDebug("Swipe preview skipped because viewport width is invalid");
            return;
        }

        double offset = Math.Clamp(args.HorizontalOffset, -viewportWidth, viewportWidth);
        ZoomImageTransform.X = offset;
        SwipePreviewTransform.X = offset < 0 ? viewportWidth + offset : -viewportWidth + offset;

        double progress = Math.Clamp(Math.Abs(offset) / viewportWidth, 0.0, 1.0);
        SwipePreviewImage.Opacity = Math.Min(1.0, 0.2 + progress * 0.8);
    }

    private void OnZoomImageSwipePreviewCompleted()
    {
        _logger.LogDebug("Swipe preview completed/reset");
        ResetSwipePreviewTransforms();
    }

    private void EnsureSwipePreviewImage(int targetIndex)
    {
        string targetPath = ViewModel.FilmStripItems[targetIndex].Photo.FilePath;
        if (string.Equals(_swipePreviewPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            SwipePreviewImage.Visibility = Visibility.Visible;
            return;
        }

        _logger.LogDebug("Loading swipe preview image for target index {TargetIndex}: {Path}", targetIndex, targetPath);
        _swipePreviewPath = targetPath;
        SwipePreviewImage.Source = _imageSourceConverter.Convert(targetPath, typeof(ImageSource), string.Empty, string.Empty) as ImageSource;
        SwipePreviewImage.Visibility = SwipePreviewImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        _logger.LogDebug("Swipe preview image source resolved: hasSource={HasSource}", SwipePreviewImage.Source is not null);
    }

    private void ResetSwipePreviewTransforms()
    {
        _swipePreviewPath = null;
        ZoomImageTransform.X = 0;
        SwipePreviewTransform.X = 0;
        SwipePreviewImage.Opacity = 0;
        SwipePreviewImage.Source = null;
        SwipePreviewImage.Visibility = Visibility.Collapsed;
    }

    private T? FindVisualChild<T>(DependencyObject parent, string elementName) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == elementName && child is T foundElement)
                return foundElement;

            var result = FindVisualChild<T>(child, elementName);
            if (result is not null)
                return result;
        }
        return null;
    }
}
