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
    private string? _swipePreviewPath;
    private string? _swipePreviewRequestedPath;
    private int _swipePreviewLoadGeneration = 0;
    private IDisposable? _swipePreviewDisposable;
    private bool _swipeCommitPending = false;
    private string? _swipeCommitTargetPath;
    private DateTime _lastTouchTapTime = DateTime.MinValue;
    private Point _lastTouchTapPosition;
    private DateTime _lastMouseTapTime = DateTime.MinValue;
    private Point _lastMouseTapPosition;

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
        _swipePreviewRequestedPath = targetPath;

        if (string.Equals(_swipePreviewPath, targetPath, StringComparison.OrdinalIgnoreCase)
            && SwipePreviewImage.Source is not null)
        {
            SwipePreviewImage.Visibility = Visibility.Visible;
            return;
        }

        _ = EnsureSwipePreviewImageAsync(targetPath, targetIndex);
    }

    private async Task EnsureSwipePreviewImageAsync(string targetPath, int targetIndex)
    {
        int generation = ++_swipePreviewLoadGeneration;

        try
        {
            var loader = GetLoader(targetPath);
            var loaded = await loader.LoadAsync(targetPath, _cts.Token);
            if (loaded is null)
            {
                if (generation == _swipePreviewLoadGeneration &&
                    string.Equals(_swipePreviewRequestedPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    SwipePreviewImage.Source = null;
                    SwipePreviewImage.Visibility = Visibility.Collapsed;
                }
                _logger.LogDebug("Swipe preview image load returned null: target index {TargetIndex}, path={Path}", targetIndex, targetPath);
                return;
            }

            bool staleRequest = generation != _swipePreviewLoadGeneration ||
                                !string.Equals(_swipePreviewRequestedPath, targetPath, StringComparison.OrdinalIgnoreCase);
            if (staleRequest)
            {
                DeferDisposeSwipePreview(loaded.Source as IDisposable);
                return;
            }

            ReplaceSwipePreviewSource(loaded.Source, targetPath);
            _logger.LogDebug("Swipe preview image source resolved via loader: target index {TargetIndex}, hasSource={HasSource}, path={Path}",
                targetIndex,
                SwipePreviewImage.Source is not null,
                targetPath);
        }
        catch (OperationCanceledException)
        {
            // Ignore navigation cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load swipe preview image: {Path}", targetPath);
        }
    }

    private void ReplaceSwipePreviewSource(ImageSource source, string path)
    {
        // Detach old source before disposing to avoid compositor using freed native surface.
        SwipePreviewImage.Source = null;
        DeferDisposeSwipePreview(_swipePreviewDisposable);

        _swipePreviewDisposable = source as IDisposable;
        _swipePreviewPath = path;

        SwipePreviewImage.Source = source;
        SwipePreviewImage.Visibility = Visibility.Visible;
    }

    private void DeferDisposeSwipePreview(IDisposable? disposable)
    {
        if (disposable is null) return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try { disposable.Dispose(); }
                catch { }
            }));
    }

    private bool TryPrepareSwipeCommitVisual(double dx)
    {
        if (Math.Abs(dx) < double.Epsilon)
            return false;

        int direction = dx < 0 ? 1 : -1;
        int targetIndex = ViewModel.CurrentIndex + direction;
        if (targetIndex < 0 || targetIndex >= ViewModel.FilmStripItems.Count)
            return false;

        string targetPath = ViewModel.FilmStripItems[targetIndex].Photo.FilePath;
        if (SwipePreviewImage.Source is null ||
            !string.Equals(_swipePreviewPath, targetPath, StringComparison.OrdinalIgnoreCase))
            return false;

        double viewportWidth = ImageViewport.ActualWidth;
        if (viewportWidth <= 0)
            return false;

        _swipeCommitPending = true;
        _swipeCommitTargetPath = targetPath;

        SwipePreviewImage.Opacity = 1.0;
        SwipePreviewTransform.X = 0;
        ZoomImageTransform.X = dx < 0 ? -viewportWidth : viewportWidth;

        _logger.LogDebug("Swipe commit visual prepared: target={TargetPath}, direction={Direction}", targetPath, direction);
        return true;
    }

    private bool ConsumeSwipeCommitForPath(string path)
    {
        if (!_swipeCommitPending ||
            !string.Equals(_swipeCommitTargetPath, path, StringComparison.OrdinalIgnoreCase))
            return false;

        _swipeCommitPending = false;
        _swipeCommitTargetPath = null;
        return true;
    }

    private void CancelSwipeCommit()
    {
        _swipeCommitPending = false;
        _swipeCommitTargetPath = null;
    }

    private void ResetSwipePreviewTransforms()
    {
        CancelSwipeCommit();
        _swipePreviewRequestedPath = null;
        _swipePreviewPath = null;
        ZoomImageTransform.X = 0;
        SwipePreviewTransform.X = 0;
        SwipePreviewImage.Opacity = 0;
        SwipePreviewImage.Source = null;
        SwipePreviewImage.Visibility = Visibility.Collapsed;
        DeferDisposeSwipePreview(_swipePreviewDisposable);
        _swipePreviewDisposable = null;
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
