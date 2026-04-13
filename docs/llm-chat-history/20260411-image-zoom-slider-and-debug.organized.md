# Image zoom slider and debug

> Saved: 2026-04-13

## 图片详情页，图片默认应该放大到撑满窗口的宽或高。双击图片时，可以放大图片，如果图片已经是放大状态则还原到刚才那个大小。
- 右下角添加一个缩放比的 slider，放大到撑满窗口的宽或高定义为 100%。可选范围是 25-1000%，value 使用 int 存储避免小数问题，且保证为 5 的倍数。slider 左边是缩小按钮（/1.25 后向下取整到5 的倍数），右边是放大按钮（*1.25 后向上取整到5的倍数），在右边是还原到 100% 的按钮。类似于 Windows 的相册 app。
- 支持触屏左右滑动、滚轮滑动切到下一张图片

实现要点与结果：
- 新增 `ZoomSliderContainer` 覆盖层（右下角），包含：缩小按钮、`Slider`、放大按钮、`100%` 重置按钮；`Slider` 范围 25–1000，值为整数且为 5 的倍数。
- 按钮使用指定图标并将 `Padding` 调为 `0`、增大 `FontSize`，按钮与 slider 的高度统一为 36px，保留合适的间距。
- 放大/缩小、重置、鼠标滚轮与触控手势均联动到统一的缩放逻辑；`ShowZoomSlider()`/`HideZoomSlider()` 可被页面复用以同步底部工具栏的显隐。
- 双击与 Ctrl+滚轮支持“以指针所在位置为锚点放大/缩小”。
- 构建通过：`make build` / `dotnet build` 均为 0 错误。

## 打开 PhotoDetailPage 时闪退（运行时 XAML 异常）

诊断与修复：
- 问题由 `Slider` 在 XAML 中使用了不兼容/不恰当的属性（例如 `StepFrequency`）以及 `Minimum`/`Maximum`/`Value` 在 XBF 编译/增量构建下顺序导致约束异常。XAML 解析在赋值顺序上触发 RangeBase 验证失败，导致运行时抛出 `Failed to assign to property 'RangeBase.Minimum'`。
- 解决方法：移除 `Slider` 在 XAML 中对 `Minimum`/`Maximum`/`Value` 的显式设置，改在控件的构造函数（code-behind）中于 `InitializeComponent()` 后设置这些值；并把 `StepFrequency` 替换为 `SmallChange` + `SnapsTo` / 以保证与 WinUI 兼容。
- 额外：避免在同一帧内对 `ScrollViewer.ChangeView` 连续调用两次（会互相覆盖），并在必要时使用 `--no-incremental` 强制全量构建以确认修复。最终把需要的范围/初始值放到后台初始化，彻底避免增量 XBF 相关问题。

## 一些交互与 UI 微调请求（复制 Info 值、图标、尺寸、间距等）

变更清单（要点）：
- 将照片信息中的 value 文本设为可复制（`IsTextSelectionEnabled=true`），key 保持不可复制。
- 把 slider 的显隐逻辑改为与底部工具栏一致（页面统一控制 show/hide），移除控件内独立的自动隐藏 timer。
- 图标替换为：缩小 `` (E71F)、放大 `` (E8A3)、还原 `` (E777)（按用户要求），并调大 `FontSize`，按钮 `Padding=0`，保证图标在 36×36 的按钮中正确显示。
- 调整 slider 轨道高度与按钮高度一致，保留左右间距，去掉强制 `Height` 使 track 居中显示。
- 在放大和还原按钮之间显示当前百分比文本，`UpdateSliderValue()` 实时更新该文本。

## 放大以指针为中心、双击/滚轮行为、精度问题与中心偏移修复

问题与修复摘要：
- 发现放大时锚点计算有误，主要因为 ScrollViewer 会在某个轴上将内容居中（当内容小于视口时），而之前的公式未扣除这个“居中偏移”。
- 新增并使用 `GetCenterOffset()`、`ViewportToContent()`、`ZoomAroundViewportPoint()` 等统一的坐标转换/缩放辅助方法，所有缩放逻辑（按钮、双击、Ctrl+滚轮、Slider）都统一调用该方法，保证以屏幕上指针对应的图片像素为缩放锚点。
- 记录并使用最近一次指针位置（`_lastPointerPos`），用于按钮/命令触发的放大操作；若无指针则退化为视口中心缩放。
- 修复公式细节，确保 newOffset 计算遵循：newOffset = (currentOffset + anchor) / currentZoom * newZoom - anchor。

注意：当图片在某轴上比视口窄时（例如竖版在横向），该轴不可滚动，此轴上的“以点为锚”在视觉上受限，但纵轴上仍精确。

## 双击切换逻辑（80–125% 范围）与异步 ChangeView 时序问题

问题回顾与解决：
- 最初使用精确等于 100 的判断导致抖动与时序问题；改为使用屏幕上显示的 `sliderValue`（四舍五入整数）并把“判为 fit 状态”的范围改为 80–125%。
- 发现 `ScrollViewer.ChangeView` 为异步，且同帧里多次调用 `ChangeView` 会覆盖/丢弃先前调用（例如 zoom 与 offset 的连续修改互相覆盖），导致 FitToWindow 无效或需要多次双击。
- 解决措施：
  - 在 `FitToWindow()` 中只调用一次 `ChangeView`（设置 zoom），移除冗余的 offset 调用；让 WinUI 的自动居中处理 offset。
  - 在调用 `ChangeView` 后立即同步更新 `_sliderValue`，避免等待 `ViewChanged` 异步回调造成的短时不同步，从而保证快速连续双击时行为一致。
  - 将双击事件绑定到 `Scroll`（使用 `AddHandler(..., handledEventsToo: true)`），避免被内部控件先行消费导致丢失手势。

这些修改解决了“100%→200% 再双击不还原而是偏移”的问题。

## 调试日志与多次尝试的清理

- 为诊断坐标与锚点问题，临时添加了 debug 日志与点击记录（`ZoomDbg`）。基于这些日志定位并修正了居中偏移、ChangeView 覆盖与时序问题。
- 最后按用户要求移除了不再需要的调试代码与字段，包括：
  - 删除用于调试的 `_logger`、`OnMainImageTapped`、`ViewportToContent`、`CentreViewport` 等辅助/日志方法；
  - 删除未被实际使用的 `_isAt100Percent` 字段及其赋值点；
  - 移除额外的 using/imports 以及临时注册的事件处理器。

构建验证：
- 修改完成并清理后进行构建（`make build` / `dotnet build`），构建成功，0 错误 0 警告。

---

最后一条用户请求（移除调试尝试的多余代码）已完成：
- 已删除所有临时调试注册与日志方法，并整理了构造函数与事件挂钩点，保留必要的公开方法（如 `ShowZoomSlider()` / `HideZoomSlider()`）以便页面统一控制 UI 显隐。

文件保存路径： docs/llm-chat-history/20260411-image-zoom-slider-and-debug.organized.md
