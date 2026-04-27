using FluentGallery.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading.Tasks;
using Windows.Foundation;

namespace FluentGallery.Views;

public sealed partial class PhotoDetailPage
{
    private bool _touchSwipeDragging = false;
    private uint? _touchSwipePointerId;
    private Point _touchSwipeStart;
    private bool _touchSwipePreviewActive = false;
    private readonly Dictionary<uint, Point> _touchPointers = new();
    private bool _touchPinching = false;
    private double _touchPinchStartDistance = 0.0;
    private double _touchPinchStartZoom = 1.0;
    private bool _mouseOverlayDragging = false;
    private bool _mouseOverlayMoved = false;
    private bool _mouseOverlayPreviewActive = false;
    private Point _mouseOverlayStart;
    private Point _mouseOverlayLastPoint;

    private void AttachZoomSliderPointerEvents()
    {
        var zoomSliderContainer = FindVisualChild<Border>(ZoomImage, "ZoomSliderContainer");
        if (zoomSliderContainer is not null)
        {
            zoomSliderContainer.AddHandler(UIElement.PointerEnteredEvent,
                new PointerEventHandler(ChromeElement_PointerEntered), handledEventsToo: true);
            zoomSliderContainer.AddHandler(UIElement.PointerExitedEvent,
                new PointerEventHandler(ChromeElement_PointerExited), handledEventsToo: true);
        }
    }

    private void TouchSwipeOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageViewport).Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            if (!e.GetCurrentPoint(ImageViewport).Properties.IsLeftButtonPressed)
                return;

            _mouseOverlayDragging = true;
            _mouseOverlayMoved = false;
            _mouseOverlayPreviewActive = false;
            _mouseOverlayStart = point;
            _mouseOverlayLastPoint = point;
            TouchSwipeOverlay.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            return;

        _touchPointers[e.Pointer.PointerId] = point;
        TouchSwipeOverlay.CapturePointer(e.Pointer);

        if (_touchPointers.Count == 1)
        {
            _touchSwipeDragging = ZoomImage.IsAtFitZoom;
            _touchSwipePreviewActive = false;
            _touchSwipePointerId = e.Pointer.PointerId;
            _touchSwipeStart = point;
            _logger.LogDebug("Touch overlay pressed: pointer={PointerId}, pos=({X:F1},{Y:F1}), fit={IsAtFitZoom}", _touchSwipePointerId, point.X, point.Y, ZoomImage.IsAtFitZoom);
        }
        else if (_touchPointers.Count == 2)
        {
            CancelTouchSwipePreview();
            _touchPinching = true;
            _touchPinchStartDistance = GetTouchPinchDistance();
            _touchPinchStartZoom = ZoomImage.CurrentZoomFactor;
            _logger.LogDebug("Touch overlay pinch started: distance={Distance:F1}, zoom={Zoom:F3}", _touchPinchStartDistance, _touchPinchStartZoom);
        }

        e.Handled = true;
    }

    private void TouchSwipeOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageViewport).Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && _mouseOverlayDragging)
        {
            double mouseDx = point.X - _mouseOverlayLastPoint.X;
            double mouseDy = point.Y - _mouseOverlayLastPoint.Y;
            double totalDx = point.X - _mouseOverlayStart.X;
            double totalDy = point.Y - _mouseOverlayStart.Y;

            if (ZoomImage.IsAtFitZoom)
            {
                const double MouseHorizontalIntentThreshold = 12.0;
                if (!_mouseOverlayPreviewActive)
                {
                    if (Math.Abs(totalDx) < MouseHorizontalIntentThreshold || Math.Abs(totalDx) <= Math.Abs(totalDy))
                        return;

                    _mouseOverlayPreviewActive = true;
                    _mouseOverlayMoved = true;
                    _logger.LogDebug("Mouse overlay preview started: dx={Dx:F1}, dy={Dy:F1}", totalDx, totalDy);
                }

                OnZoomImageSwipePreviewProgress(new SwipePreviewEventArgs(totalDx, ImageViewport.ActualWidth));
            }
            else
            {
                if (Math.Abs(mouseDx) > 1 || Math.Abs(mouseDy) > 1)
                    _mouseOverlayMoved = true;
                ZoomImage.PanBy(mouseDx, mouseDy);
            }

            _mouseOverlayLastPoint = point;
            e.Handled = true;
            return;
        }

        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch || !_touchPointers.ContainsKey(e.Pointer.PointerId))
            return;

        _touchPointers[e.Pointer.PointerId] = point;

        if (_touchPinching && _touchPointers.Count >= 2)
        {
            double distance = GetTouchPinchDistance();
            if (_touchPinchStartDistance > 0 && distance > 0)
            {
                double scale = distance / _touchPinchStartDistance;
                ZoomImage.ZoomToFactorAt(_touchPinchStartZoom * scale, GetTouchPinchCenter());
            }
            e.Handled = true;
            return;
        }

        if (!_touchSwipeDragging || _touchSwipePointerId != e.Pointer.PointerId || !ZoomImage.IsAtFitZoom)
            return;

        double dx = point.X - _touchSwipeStart.X;
        double dy = point.Y - _touchSwipeStart.Y;

        const double HorizontalIntentThreshold = 12.0;
        if (!_touchSwipePreviewActive)
        {
            if (Math.Abs(dx) < HorizontalIntentThreshold || Math.Abs(dx) <= Math.Abs(dy))
                return;

            _touchSwipePreviewActive = true;
            _logger.LogDebug("Touch overlay preview started: dx={Dx:F1}, dy={Dy:F1}", dx, dy);
        }

        OnZoomImageSwipePreviewProgress(new SwipePreviewEventArgs(dx, ImageViewport.ActualWidth));
        e.Handled = true;
    }

    private async void TouchSwipeOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageViewport).Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            if (_mouseOverlayDragging)
            {
                double totalDx = point.X - _mouseOverlayStart.X;
                double totalDy = point.Y - _mouseOverlayStart.Y;
                bool mouseHadPreview = _mouseOverlayPreviewActive;

                _mouseOverlayDragging = false;
                _mouseOverlayPreviewActive = false;
                ReleaseTouchOverlayPointer(e.Pointer.PointerId);

                if (mouseHadPreview)
                {
                    const double MouseMinSwipe = 60.0;
                    if (Math.Abs(totalDx) >= MouseMinSwipe && Math.Abs(totalDx) >= Math.Abs(totalDy) * 1.5)
                    {
                        TryPrepareSwipeCommitVisual(totalDx);

                        if (totalDx < 0)
                            await NavigateRelativeAsync(1);
                        else
                            await NavigateRelativeAsync(-1);
                    }
                    else
                    {
                        OnZoomImageSwipePreviewCompleted();
                    }
                }
                else if (!_mouseOverlayMoved)
                {
                    HandleMouseTap(point);
                }

                e.Handled = true;
            }
            return;
        }

        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            return;

        bool wasSwipePointer = _touchSwipePointerId == e.Pointer.PointerId;
        _touchPointers.Remove(e.Pointer.PointerId);
        ReleaseTouchOverlayPointer(e.Pointer.PointerId);

        if (_touchPinching)
        {
            if (_touchPointers.Count < 2)
            {
                _touchPinching = false;
                _touchPinchStartDistance = 0;
                _touchPinchStartZoom = 1;
                _logger.LogDebug("Touch overlay pinch ended");
            }
            e.Handled = true;
            return;
        }

        if (!wasSwipePointer)
            return;

        double dx = point.X - _touchSwipeStart.X;
        double dy = point.Y - _touchSwipeStart.Y;
        bool hadPreview = _touchSwipePreviewActive;
        _logger.LogDebug("Touch overlay released: dx={Dx:F1}, dy={Dy:F1}, hadPreview={HadPreview}", dx, dy, hadPreview);

        EndTouchSwipeState();

        if (!hadPreview)
        {
            HandleTouchTap(point);
            e.Handled = true;
            return;
        }

        const double MinSwipe = 60.0;
        if (Math.Abs(dx) < MinSwipe || Math.Abs(dx) < Math.Abs(dy) * 1.5)
        {
            _logger.LogDebug("Touch overlay swipe rejected by threshold");
            OnZoomImageSwipePreviewCompleted();
            e.Handled = true;
            return;
        }

        TryPrepareSwipeCommitVisual(dx);

        if (dx < 0)
            await NavigateRelativeAsync(1);
        else
            await NavigateRelativeAsync(-1);

        e.Handled = true;
    }
}
