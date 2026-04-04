# Photo detail page image loading issue
_Exported on 4/4/2026 at 17:17:51 GMT+8 from Cursor (2.6.22)_

---

**User**

照片详情页从前面的图片往后翻的时候，有时候加载不出来图片了，转圈超过5s。但是重新从照片列表页打开照片又可以了。

---

**Cursor**

让我仔细读一下关键文件来找出 bug。

找到 bug 了。让我仔细分析问题所在。

**Bug 找到了！**

问题在 `ZoomableImage.LoadImageFromCacheAsync` 的 `else` 分支（第 244 行）。

**根本原因：**

在 `PreloadAdjacent` 里，预加载图片的方式是：
```csharp
var bmp = new BitmapImage(new Uri(path));
```

这个 `BitmapImage` **从未被赋值给任何 `Image.Source`**。在 WinUI 3 中，`BitmapImage` 的实际解码可能不会在设置 `UriSource` 时立即发生，而是等到它被赋值给 `Image.Source` 才真正启动解码并最终触发 `ImageOpened` 事件。

当用户翻页命中缓存但图片还没解码完（`PixelWidth == 0`）时，代码执行：
```csharp
MainImage.Source = null;   // ← BUG：保持 null，解码永远不会被触发
```

由于 `MainImage.Source` 始终是 `null`，这个 `BitmapImage` 的解码从未被系统调度，`ImageOpened` 永远不会触发，转圈就卡死了。

而"从列表页重新打开"会走 `LoadViaBitmapImageAsync`，那里会 `MainImage.Source = bmp`，解码正常触发。

另外还有一个次要问题：每次经过 else 分支都会往 `ImageOpened` 上 `+=` handler，从不取消订阅，同一个 `BitmapImage` 若被反复访问会积累多个 handler。

**修复：**

现在检查一下 lint：

确认一下修改后的代码：

代码无误。下面做一次编译确认：

编译成功，0 错误 0 警告。

---

**修复总结**

**Bug 根本原因：**

`PreloadAdjacent` 预加载时，创建的 `BitmapImage` 从未被赋值给任何 `Image.Source`：

```csharp
var bmp = new BitmapImage(new Uri(path));  // 只存入缓存，不赋给 MainImage.Source
AddToCache(path, bmp);
```

在 WinUI 3 中，`BitmapImage` 的实际解码只有在它被赋给 `Image.Source` 后才会可靠地启动和触发 `ImageOpened` 事件。

当用户翻页命中缓存但还没解码完（`PixelWidth == 0`）时，旧代码执行了：

```csharp
MainImage.Source = null;   // ← BUG：让 BitmapImage 始终脱离 Image 控件
```

导致解码永远无法被调度，`ImageOpened` 永远不触发，loading ring 就一直转。

而"从列表页重新打开"走的是 `LoadViaBitmapImageAsync`，那里明确 `MainImage.Source = bmp`，所以总能正常触发解码。

**两处修复：**

1. **主要修复**：`MainImage.Source = null` → `MainImage.Source = cached`，确保 BitmapImage 附着在 Image 控件上，WinUI 才能正确调度解码并触发 `ImageOpened`。

2. **顺带修复**：把匿名 lambda 改成可取消订阅的具名本地 handler（onOpened / onFailed 互相取消），避免同一个 `BitmapImage` 被多次加载时 handler 持续堆积。

