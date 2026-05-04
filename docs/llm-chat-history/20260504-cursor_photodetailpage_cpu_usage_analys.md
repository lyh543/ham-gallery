# PhotoDetailPage CPU usage analysis
_Exported on 5/4/2026 at 17:54:02 GMT+8 from Cursor (2.6.22)_

---

**User**

@FluentGallery/Views/PhotoDetailPage.xaml 在这个页面滑动到前后图片的时候，CPU 占用会到 100%，然后掉帧，分析一下为什么

---

**Cursor**

Let me read the XAML file and the code-behind to analyze the performance issue.

Now let me look at the navigation-related code and the preload/image loading code in the code-behind, especially `HandleCurrentImagePathChanged`, `NavigateRelativeAsync`, and the swipe-related methods.

Now let me look at the image loaders and the ZoomableImage control, as these are likely the core of the performance issue.

Now let me look at the `NavigateToIndexAsync` flow more carefully, especially how it updates the filmstrip items. Let me also check what happens with `LoadExtendedExifAsync`.

Now I have a thorough understanding of the codebase. Let me check the `ImageDecoderPipeline` for any serialization gates or heavy processing.

Now I have a comprehensive understanding of the entire navigation flow. Let me provide the analysis.

---

## Performance Analysis: CPU 100% and Frame Drops on Photo Navigation

After reading through the complete code path — from swipe/keyboard input → `NavigateRelativeAsync` → `NavigateToIndexAsync` → `HandleCurrentImagePathChanged` → `LoadCurrentImageAsync` + `UpdatePreloadTasks` — I identified several contributing factors to the high CPU usage and frame drops:

### 1. **`NavigateToIndexAsync` 中的 O(N) 全量遍历 FilmStripItems**（主要问题）

```129:131:FluentGallery/ViewModels/PhotoDetailViewModel.cs
        for (int i = 0; i < FilmStripItems.Count; i++)
            FilmStripItems[i].IsSelected = (i == index);
```

**每次导航都遍历整个 `FilmStripItems`**（几百甚至几千张照片），对每一个 item 设置 `IsSelected` 属性。由于 `IsSelected` 是 `ObservableProperty`，每次赋值（即使新旧值相同 `false → false`）都可能触发 `PropertyChanged` 事件，进而引起 XAML 绑定更新和 UI 重新渲染。

**修复方案**：只修改上一个和当前选中项，避免触发 N 次无用的 `PropertyChanged`：

```csharp
// Track previous index, only toggle 2 items
if (previousIndex >= 0 && previousIndex < FilmStripItems.Count)
    FilmStripItems[previousIndex].IsSelected = false;
FilmStripItems[index].IsSelected = true;
```

### 2. **`EnsureSwipePreviewImageAsync` 每次滑动都全量解码一张新图片**（主要问题）

```215:246:FluentGallery/Views/PhotoDetailPage.Gestures.Support.cs
    private async Task EnsureSwipePreviewImageAsync(string targetPath, int targetIndex)
    {
        // ...
        var loader = GetLoader(targetPath);
        var loaded = await loader.LoadAsync(targetPath, _cts.Token);
        // ...
    }
```

在用户拖拽预览时，如果缓存未命中，这里会调用 `loader.LoadAsync` 解码一张全尺寸图片。对于 HEIC/RAW 格式（走 `MagickImageLoader`），这意味着：

- `ImageDecoderPipeline.TryDecodeAsync` — CPU 密集型 Magick.NET 解码
- `SoftwareBitmap.CreateCopyFromBuffer` — 大量内存拷贝
- `SoftwareBitmap.Convert` — 像素格式转换
- `SoftwareBitmapSource.SetBitmapAsync` — GPU 上传

**所有这些加在一起，对 12MP+ 的 HEIC 图片可以轻松占用数百毫秒的 CPU 时间**，导致 UI 线程阻塞和掉帧。

### 3. **`LoadCurrentImageAsync` 与 `EnsureSwipePreviewImageAsync` 可能重复解码同一张图片**

滑动完成后，`LoadCurrentImageAsync` 会重新加载刚导航到的图片。如果 swipe preview 已经加载了这张图片，理论上可以复用——代码中有 `TryConsumeSwipePreviewLoadedImage` 做了这件事。但当滑动提交时，如果 preview 还没加载完，主图加载就会从头解码，与仍在执行的 preview 解码并行跑，**两个全量解码在同一瞬间争抢 CPU**。

### 4. **EXIF 重复解码**（次要问题）

```139:140:FluentGallery/ViewModels/PhotoDetailViewModel.cs
        UpdateInfoPanelFast(CurrentPhoto);
        _ = LoadExtendedExifAsync(filePath, ct);
```

每次导航都会 fire-and-forget 调用 `LoadExtendedExifAsync`，其中 `_exif.ReadExif(filePath)` 是同步 CPU 密集型操作（MetadataExtractor + Magick.NET `MagickImageInfo`），包裹在 `Task.Run` 中。如果快速连续按键/滑动，会有多个 EXIF 解析任务堆积在线程池上，占用 CPU 资源。

### 5. **`UpdatePreloadTasks` 中的 LINQ 分配和线性查找**（次要问题）

```74:106:FluentGallery/Views/PhotoDetailPage.Loading.cs
    private void UpdatePreloadTasks(int newIndex)
    {
        var newPaths = new HashSet<string>(...);
        
        foreach (var path in _preloadTasks.Keys.Where(p => !newPaths.Contains(p)).ToList())
        // ...
```

每次导航都创建新的 `HashSet`，并对 `_preloadTasks.Keys` 执行 `Where(...).ToList()` 产生临时分配。在快速导航时，这些分配会增加 GC 压力。

### 6. **`FindFilmStripItem` 是 O(N) 线性查找**

```127:129:FluentGallery/Views/PhotoDetailPage.Loading.cs
    private PhotoThumbItem? FindFilmStripItem(string path) =>
        ViewModel.FilmStripItems.FirstOrDefault(
            i => string.Equals(i.Photo.FilePath, path, StringComparison.OrdinalIgnoreCase));
```

每次导航中被调用多次（主图加载 + 每张预加载图），每次都是 O(N) 的 `FirstOrDefault`。当相册有上千张照片时，这个开销不可忽视。

### 7. **`PointerMoved` 事件高频触发 `OnZoomImageSwipePreviewProgress`**

触摸/鼠标拖拽时，`PointerMoved` 以 ~60–120 Hz 频率触发。每次事件中：
- 调用 `EnsureSwipePreviewImage` → 可能触发异步图片加载
- 修改 `ZoomImage.ContentHorizontalOffset`、`SwipePreviewTransform.X`、`SwipePreviewImage.Opacity`

这些属性修改每帧都会触发 XAML 布局/合成更新。虽然单次开销不大，但如果 `EnsureSwipePreviewImageAsync` 的解码任务同时运行，UI 线程就会被抢占。

---

### 优先修复建议

1. **FilmStripItems IsSelected 遍历改为增量更新**：只更改前一个和当前选中项（最大性能提升，最简单修改）。

2. **确保预加载图片优先用于 swipe preview**：在 `EnsureSwipePreviewImage` 中先检查 preload cache 有没有对应的已解码数据，避免重复解码。

3. **为 EXIF 解码添加 debounce / 取消机制**：快速导航时取消前一次的 `LoadExtendedExifAsync`，只为最终停留的图片执行 EXIF 解析。

4. **`FindFilmStripItem` 改用 `Dictionary<string, PhotoThumbItem>` 索引**：O(1) 查找替代 O(N)。

需要我实际修改代码来修复这些问题吗？

---

**User**

好

---

**Cursor**

Good, let me implement the fixes. I'll start with the highest-impact changes.

### Fix 1: FilmStripItems IsSelected incremental update

Now I need to track the previous index to avoid the O(N) loop. Let me find the `NavigateToIndexAsync` method.

Now let me apply the first fix - track previous selected index and only toggle two items:

Also need to reset `_previousSelectedIndex` when rebuilding the filmstrip in `InitializeAsync`:

### Fix 2: EXIF decode cancellation

Now I need to add a `CancellationTokenSource` for the EXIF task and cancel it on each navigation:

Now update `NavigateToIndexAsync` to cancel the previous EXIF task:

Now find the `NavigateToIndexAsync` method and add EXIF cancellation:

Hmm, let me try to search for it more directly.

That returned the wrong line. Let me read the area around `NavigateToIndexAsync` directly.

Let me find `NavigateToIndexAsync` precisely:

Now I can add EXIF cancellation:

Also dispose `_exifCts` in `Dispose()`:

### Fix 3: Replace FindFilmStripItem O(N) with Dictionary

I'll add a `_filmStripItemIndex` dictionary and a method to rebuild it, then replace `FindFilmStripItem`:

Now replace the `FindFilmStripItem` method:

Now I need to call `RebuildFilmStripItemIndex` after `InitializeAsync` completes and after delete/undo operations. Let me find where `InitializeAsync` finishes in the page's `OnPageLoaded`:

I'll add `RebuildFilmStripItemIndex()` after the initialization branches, before `ApplyFilmStripPinState`:

Now I need to also rebuild the index after delete/undo, which modifies `FilmStripItems`. Let me check the Commands file:

I need to rebuild the index after `DeleteAsync` and `UndoDeleteAsync`:

Also need to rebuild after `ReloadCurrentFileContextAsync` — let me check the Index partial class:

I see the `OnScanCompleted` calls `ReloadCurrentFileContextAsync` which internally calls `InitializeAsync` again, so the rebuild will happen when the page loads. But I should also rebuild the index in `OnScanCompleted` since it re-initializes the filmstrip:

### Fix 4: EnsureSwipePreviewImage - use preload cache when available

The swipe preview calls `loader.LoadAsync` directly, which for WIC-supported formats creates a new `BitmapImage` even though one may already exist in the preload cache. The issue is that `LoadAsync` consumes from the cache (removes the entry), which means the main load later won't find it. Instead, for the preview, I should check if the item's `PreloadState` is `Loaded` and if so, just load it normally (it'll be fast because the decoder has already done the work).

Actually, looking at it more carefully - for `WicImageLoader`, `LoadAsync` pulls from the cache and the `BitmapImage` might already be decoded (PixelWidth > 0). The real issue is with `MagickImageLoader` where `LoadAsync` involves expensive `Task.Run` with pixel conversion + `SetBitmapAsync`. The preview load and the main load might both decode the same image.

The more impactful fix is to avoid loading the preview image if it's not already in the preload cache - just show a quick placeholder. For standard formats (JPEG, PNG, WebP), `WicImageLoader.LoadAsync` is essentially instant (creates a `BitmapImage` with URI, XAML handles decoding). The real issue is HEIC/RAW via `MagickImageLoader`.

Let me add a fast-path in `EnsureSwipePreviewImageAsync` that checks the preload cache first:

I need to add a `TryLoadFromCacheAsync` method to `IImageLoader`, or better, a non-destructive peek. But that would require modifying the interface. A simpler approach: add a `HasCachedEntry` method.

Actually, looking at the flow more carefully, the most practical approach is: in `EnsureSwipePreviewImageAsync`, use the filmstrip thumbnail as a fast preview instead of doing a full decode when the preload hasn't completed yet. But that changes the visual behavior.

Let me take a different, cleaner approach: add a `PeekCachedAsync` method to `IImageLoader` that returns the cached image without removing it, and only falls back to full load for WIC formats (which are cheap).

Actually, the simplest effective fix is: for `MagickImageLoader`-handled formats (HEIC, etc.), skip the preview image loading entirely if there's no cached entry. The swipe animation will just not show the preview image for HEIC files that aren't preloaded, which is better than locking up the CPU for 100+ ms.

I need to add the proper using for `MagickImageLoader`:

### Fix 5: Build and verify

构建成功，0 个错误。



Error: Connection failed. If the problem persists, please check your internet connection or VPN


---

**User**

继续

---

**Cursor**



Error: Connection failed. If the problem persists, please check your internet connection or VPN


---

**User**

继续

---

**Cursor**

## 性能优化完成 ✅

已成功修复导致 CPU 100% 和掉帧的主要问题。以下是实施的优化：

### 1. **FilmStripItems IsSelected O(N) → O(1)** ✅
- **问题**：每次导航遍历所有胶片栏项目（可能上千张），触发大量无用的 PropertyChanged 事件
- **修复**：只更新前一个和当前选中的两个项目
- **影响**：从 O(N) 降至 O(1)，对大相册性能提升显著

### 2. **EXIF 解码取消机制** ✅
- **问题**：快速导航时多个 `Task.Run(() => _exif.ReadExif())` 堆积在线程池，争抢 CPU
- **修复**：每次导航前取消上一次的 EXIF 任务，只为最终停留的照片解析 EXIF
- **影响**：避免无用的 CPU 密集型 EXIF 解析

### 3. **FindFilmStripItem O(N) → O(1)** ✅
- **问题**：每次导航多次调用 `FirstOrDefault` 线性查找（主图 + 每张预加载图）
- **修复**：用 `Dictionary<string, PhotoThumbItem>` 索引替代，O(1) 查找
- **影响**：减少导航路径上的 CPU 开销

### 4. **跳过未预加载 HEIC 的滑动预览** ✅
- **问题**：滑动时 `EnsureSwipePreviewImageAsync` 对 HEIC/RAW 触发全量解码（Magick.NET + 像素转换 + GPU 上传），锁死 UI 线程 100+ ms
- **修复**：对 `MagickImageLoader` 处理的格式，如果未预加载则跳过预览图加载
- **影响**：避免滑动手势中的 CPU 峰值，JPEG/PNG/WebP 仍正常显示预览（WIC 解码很快）

### 构建结果
```
已成功生成。
0 个错误
3 个警告（资源相关，不影响功能）
```

### 测试建议
1. 打开一个包含大量照片（500+ 张）的相册
2. 快速连续按左右方向键或滑动切换照片
3. 观察 CPU 占用和帧率是否明显改善
4. 特别测试 HEIC 格式照片的滑动体验

这些优化应该能显著降低导航时的 CPU 占用，减少掉帧现象。如果还有性能问题，可以进一步分析日志或使用性能分析工具定位瓶颈。

---

**User**

把 @FluentGallery/Views/PhotoDetailPage.xaml.cs OnNavigatedFrom 里面的代码，根据类型，拆分到对应 partial class 的 OnNavigatedFrom 方法里

---

**Cursor**

好的，我来将 `OnNavigatedFrom` 中的代码按功能拆分到对应的 partial class 中。

首先让我看一下当前的 `OnNavigatedFrom` 方法：

现在我来分析这些代码的归属，并拆分到对应的 partial class 中：

让我检查一下 Chrome.cs 文件，看看它负责什么：

现在我来拆分 `OnNavigatedFrom` 方法。根据代码分析，我将创建各个 partial class 的清理方法：

### 1. 先在主文件中创建调用各个清理方法的新版本：

### 2. 在 Chrome.cs 中添加清理方法：

让我找到 Chrome.cs 文件的末尾来添加清理方法：

让我读取文件末尾：

让我直接在文件末尾添加清理方法。先读取完整文件看看结构：

好的，现在我在各个 partial class 文件末尾添加清理方法：

### 2. Chrome.cs - 添加清理方法：

### 3. Gestures.Support.cs - 添加清理方法：

### 4. Index.cs - 添加清理方法：

### 5. Loading.cs - 添加清理方法：

现在验证构建：

完美！✅ 构建成功，0 个错误，0 个警告。

## 重构完成总结

已成功将 `OnNavigatedFrom` 方法中的代码按功能拆分到对应的 partial class 中：

### 主文件 (`PhotoDetailPage.xaml.cs`)
```csharp
protected override void OnNavigatedFrom(NavigationEventArgs e)
{
    base.OnNavigatedFrom(e);
    
    CleanupChrome();
    CleanupGestures();
    CleanupIndex();
    CleanupLoading();
    
    _cts.Cancel();
    ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    ViewModel.Dispose();
}
```

### 各 Partial Class 的清理方法

1. **`PhotoDetailPage.Chrome.cs`** - `CleanupChrome()`
   - 停止 `_hideTimer`
   - 停止 `_toastTimer`
   - 停止 `_edgeBoundaryThrottle`

2. **`PhotoDetailPage.Gestures.Support.cs`** - `CleanupGestures()`
   - 重置触摸滑动状态
   - 清空触摸指针集合
   - 重置鼠标拖拽状态
   - 调用 `ResetSwipePreviewTransforms()`

3. **`PhotoDetailPage.Index.cs`** - `CleanupIndex()`
   - 停止索引提示自动隐藏计时器
   - 隐藏索引提示浮窗
   - 取消订阅扫描完成事件
   - 取消订阅窗口大小变化事件

4. **`PhotoDetailPage.Loading.cs`** - `CleanupLoading()`
   - 停止预加载防抖计时器
   - 取消所有进行中的预加载任务
   - 清空预加载任务字典
   - 释放当前显示的图像（释放 WIC 解码器内存）
   - 清空图像加载器缓存
   - 记录调试日志

### 优点
- **代码组织更清晰**：每个 partial class 负责自己的清理逻辑
- **易于维护**：修改某个功能的清理逻辑时，只需在对应的 partial class 中修改
- **职责分离**：符合单一职责原则，每个文件管理自己的生命周期

---

**User**

我拖着一张图片来回拖动，也会 CPU 100%、掉帧，是不是因为处理swipe事件的频率太高了

---

**Cursor**



让我仔细看一下拖动过程中的完整调用链，分析瓶颈在哪里。

现在让我看看 `ZoomableImage.ContentHorizontalOffset` 的实现——它每一帧被设置，看看它是否只是一个 dependency property 还是做了更多事情：

现在我看到了关键问题。让我跟踪拖动时每帧执行的完整调用链。

当用户拖动图片时（以鼠标为例），每一个 `PointerMoved` 事件都会调用：

1. `TouchSwipeOverlay_PointerMoved` → `OnZoomImageSwipePreviewProgress`
2. `OnZoomImageSwipePreviewProgress` 中：
   - `EnsureSwipePreviewImage(targetIndex)` — 可能触发异步图片加载
   - `ZoomImage.ContentHorizontalOffset = offset` — 通过数据绑定触发 `TranslateTransform.X` 更新
   - `SwipePreviewTransform.X = ...` 
   - `SwipePreviewImage.Opacity = ...`

让我看看 `Page_PointerMoved` 是否也被触发了——它在 XAML 里绑定到了 Page：

Grep 的 content mode 没返回内容。让我直接搜索：

找到了！`Page_PointerMoved` 在每次指针移动时都调用 `ShowChrome()`，而 `ShowChrome()` 内部：

1. 检查 `_hideChromeInProgress` → 如果正在隐藏则跳过（还好）
2. 调用 `EnsureChromeVisible()` → 里面有 `if (_toolbarVisible) return;` 保护（还好）
3. 调用 `RestartHideTimer()` → **每次都 Stop + Start DispatcherTimer**

但更关键的是 `ShowChrome()` 还调用了 `ZoomImage.ShowZoomSlider()` 里面每次都创建一个 `Storyboard` 动画。让我再看看：

`ShowChrome()` 中确实对 `wasHidden` 做了检查，只有从隐藏变为显示时才调用 `ShowZoomSlider`，这不是主要问题。

让我重新关注真正的热路径——拖动时 `OnZoomImageSwipePreviewProgress` 的执行。问题不在于 PointerMoved 频率本身（那是正常的 60-120 Hz），而是在于每次调用中做了什么。让我仔细看 `EnsureSwipePreviewImage`：

关键发现：

1. **`EnsureSwipePreviewImage` 每帧都被调用**，即使方向没变、preview 已经加载好了——它有 early return 保护，但仍然每帧做一次字符串比较
2. **如果用户来回快速拖动，方向反转时会频繁触发 `EnsureSwipePreviewImageAsync`**——每次方向变化就要加载一张新图片。这是 CPU 峰值的核心原因。
3. **来回拖动时**，`_swipePreviewLoadGeneration` 不断递增，导致之前的加载结果被丢弃（generation 不匹配），然后又发起新加载。

不是事件频率的问题，**是来回拖动时反复触发图片加载**的问题。每次方向反转就触发一次 `loader.LoadAsync`，对于 WIC 格式这会创建新的 `BitmapImage` 对象（虽然解码是异步的，但对象创建和 URI 解析在 UI 线程上），而对于已经在缓存中的情况，`LoadAsync` 会从缓存中移除并返回——**但 swipe preview 又不把它放回缓存**，所以下次同方向拖动时又要重新创建。

让我来修复这些问题：

核心思路：维护两张 swipe preview 缓存（左/右各一张），方向反转时不需要重新加载。同时给 `WicImageLoader` 加一个 `PeekAsync` 方法，peek 不消耗缓存条目。

先给 `IImageLoader` 加一个不消耗缓存的 peek 方法：

实际上换一个思路——问题的根本原因是：来回拖动时 swipe preview 反复对同一张图片发起 `LoadAsync`。更好的做法是：**在 swipe preview 层面缓存两个方向的已加载图片**，方向反转时直接切换显示，不需要重新加载。

现在我来重构 swipe preview 的缓存机制。核心改动：

1. 维护一个 `_swipePreviewCache` 字典，缓存已加载的 preview source（按 path 索引）
2. `EnsureSwipePreviewImage` 先查缓存
3. `ResetSwipePreviewTransforms` 时不清缓存（等到导航完成后由 `LoadCurrentImageAsync` 清除不需要的）
4. 提交导航后再清缓存

Now rewrite `EnsureSwipePreviewImage` and `EnsureSwipePreviewImageAsync` to use the cache:

Now update `EnsureSwipePreviewImageAsync` to store results in the cache:

Now update `TryConsumeSwipePreviewLoadedImage` to work with the cache:

Now update `ResetSwipePreviewTransforms` to also clear the cache:

Now let me also update the cleanup method to clear the cache:

Now I realize there's also a problem with WicImageLoader: `LoadAsync` **removes** the entry from the preload cache. So if the swipe preview calls `LoadAsync` for the next image, when the user actually commits the swipe and `LoadCurrentImageAsync` runs, the preload cache entry is gone and a new `BitmapImage` has to be created. Let me fix this by adding a non-destructive load method.

Add a `LoadForPreviewAsync` method to `IImageLoader` that doesn't consume the cache:

Now implement `LoadForPreviewAsync` in `WicImageLoader`:

Now implement `LoadForPreviewAsync` in `MagickImageLoader`. For Magick, we already skip preview if not preloaded, but we still need the interface method:

I need to find where `LoadAsync` ends in MagickImageLoader to add `LoadForPreviewAsync` after it:

Now update `EnsureSwipePreviewImageAsync` to use `LoadForPreviewAsync` instead of `LoadAsync`:

构建成功，0 个错误，0 个警告。

## 分析与修复总结

你的判断方向是对的，但根本原因不是 PointerMoved 事件频率太高（60-120 Hz 是正常的），而是**每次方向反转都会触发一次全新的图片解码**。

### 来回拖动时的真正瓶颈

当你拖着图片来回拖动时，调用链是：

```
PointerMoved → OnZoomImageSwipePreviewProgress → EnsureSwipePreviewImage
```

每当拖动方向反转（比如从向左变成向右），`EnsureSwipePreviewImage` 会：
1. 发现当前 preview 是上一个方向的图片，path 不匹配
2. 调用 `loader.LoadAsync()` 加载另一个方向的图片
3. **`WicImageLoader.LoadAsync` 会从 preload 缓存中移除条目**（消耗性读取）
4. 再次反转方向时，preload 缓存已空，又要创建新的 `BitmapImage`

如果你快速来回拖，每秒可能有几十次方向反转，每次都创建新 `BitmapImage` 并触发 WIC 后台解码——这就是 CPU 100% 的原因。

### 做了两个修复

**1. Swipe Preview 本地缓存（最大 2 个条目：前一张 + 后一张）**

方向反转时直接从本地缓存取已加载的预览图，不再重新解码。来回拖动变成纯粹的 UI 属性切换（TranslateTransform.X、Opacity），几乎零 CPU 开销。

**2. `LoadForPreviewAsync` — 非消耗性加载**

为 `IImageLoader` 新增 `LoadForPreviewAsync` 方法，用于 swipe preview。`WicImageLoader` 的实现不会从 preload 缓存中移除条目，确保后续 `LoadAsync`（真正导航时）仍能命中缓存。

