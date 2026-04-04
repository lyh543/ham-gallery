using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentGallery.Helpers;

/// <summary>
/// 为 ScrollViewer、GridView、ListView 等可滚动控件添加 iOS 风格的弹性回弹效果。
/// 仅对触摸和触控笔输入生效，鼠标（含滚轮）不触发。
/// 使用 Composition SpringVector3Animation 在滚动到边界后产生物理弹簧回弹动画。
///
/// 用法：在控件 Loaded 之后调用 ElasticScrollHelper.Attach(myControl);
///       横向滚动（如 FilmStrip）：ElasticScrollHelper.Attach(myList, ScrollAxis.Horizontal);
/// </summary>
public sealed class ElasticScrollHelper
{
    public enum ScrollAxis { Vertical, Horizontal }

    // 最大视觉偏移（像素）
    private const float MaxBounce = 80f;
    // 触摸/触控笔拖动时的阻尼系数
    private const float TouchDamp = 0.32f;

    private readonly FrameworkElement _host;
    private readonly ScrollAxis       _axis;

    private ScrollViewer? _sv;
    private UIElement?    _presenter;
    private Visual?       _visual;
    private Compositor?   _compositor;

    private float  _extra;       // 当前额外视觉偏移量（Y 或 X）
    private bool   _pointerDown;
    private double _lastPos;

    // ── 工厂方法 ─────────────────────────────────────────────────────────────

    private ElasticScrollHelper(FrameworkElement host, ScrollAxis axis)
    {
        _host = host;
        _axis = axis;
        if (host.IsLoaded) Initialize();
        else host.Loaded += (_, _) => Initialize();
    }

    /// <summary>
    /// 将弹性滚动附加到 <paramref name="host"/>。
    /// 若 host 本身是 ScrollViewer 则直接使用，否则在其后代中查找第一个 ScrollViewer。
    /// </summary>
    public static ElasticScrollHelper Attach(
        FrameworkElement host,
        ScrollAxis axis = ScrollAxis.Vertical) => new(host, axis);

    // ── 初始化 ───────────────────────────────────────────────────────────────

    private void Initialize()
    {
        _sv = _host is ScrollViewer sv ? sv : FindDescendant<ScrollViewer>(_host);
        if (_sv == null) return;

        _presenter = FindDescendant<ScrollContentPresenter>(_sv);
        if (_presenter == null) return;

        _compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
        _visual     = ElementCompositionPreview.GetElementVisual(_presenter);
        ElementCompositionPreview.SetIsTranslationEnabled(_presenter, true);

        _host.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        _host.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        _host.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnPointerReleased), handledEventsToo: true);
        _host.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnPointerReleased), handledEventsToo: true);
    }

    // ── 边界检测 ─────────────────────────────────────────────────────────────

    private bool HasScrollableContent() => _axis == ScrollAxis.Vertical
        ? _sv!.ScrollableHeight > 0
        : _sv!.ScrollableWidth  > 0;

    private bool AtStart() => _axis == ScrollAxis.Vertical
        ? _sv!.VerticalOffset   <= 0.5
        : _sv!.HorizontalOffset <= 0.5;

    private bool AtEnd() => _axis == ScrollAxis.Vertical
        ? _sv!.VerticalOffset   >= _sv.ScrollableHeight - 0.5
        : _sv!.HorizontalOffset >= _sv.ScrollableWidth  - 0.5;

    // ── 触摸 / 触控笔拖动处理 ────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 仅响应触摸和触控笔，忽略鼠标
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        _pointerDown = true;
        var pt = e.GetCurrentPoint(_host).Position;
        _lastPos = _axis == ScrollAxis.Vertical ? pt.Y : pt.X;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown || _sv == null || _visual == null) return;
        if (!HasScrollableContent()) return;

        var cp = e.GetCurrentPoint(_host);
        if (cp.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;

        double cur   = _axis == ScrollAxis.Vertical ? cp.Position.Y : cp.Position.X;
        double delta = cur - _lastPos;
        _lastPos = cur;

        if (delta > 0 && AtStart())
        {
            float add = (float)(delta * TouchDamp * (1f - Math.Abs(_extra) / MaxBounce));
            _extra    = Math.Clamp(_extra + add, -MaxBounce, MaxBounce);
            ApplyOffset(_extra);
        }
        else if (delta < 0 && AtEnd())
        {
            float add = (float)(delta * TouchDamp * (1f - Math.Abs(_extra) / MaxBounce));
            _extra    = Math.Clamp(_extra + add, -MaxBounce, MaxBounce);
            ApplyOffset(_extra);
        }
        else if (_extra != 0)
        {
            SpringBack();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        _pointerDown = false;
        if (_extra != 0) SpringBack();
    }

    // ── Composition 动画 ─────────────────────────────────────────────────────

    private void ApplyOffset(float value)
    {
        if (_compositor == null || _visual == null) return;
        var target = _axis == ScrollAxis.Vertical
            ? new Vector3(0, value, 0)
            : new Vector3(value, 0, 0);

        var kf = _compositor.CreateVector3KeyFrameAnimation();
        kf.Duration = TimeSpan.FromMilliseconds(16);
        kf.InsertKeyFrame(1f, target);
        _visual.StartAnimation("Translation", kf);
    }

    private void SpringBack()
    {
        if (_compositor == null || _visual == null || _extra == 0) return;
        _extra = 0;

        var spring = _compositor.CreateSpringVector3Animation();
        spring.Target       = "Translation";
        spring.FinalValue   = Vector3.Zero;
        spring.DampingRatio = 0.65f;
        spring.Period       = TimeSpan.FromMilliseconds(55);
        _visual.StartAnimation("Translation", spring);
    }

    // ── 可视树搜索工具 ────────────────────────────────────────────────────────

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
