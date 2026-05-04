
**User**

现在请你修复占用内存高的问题。

1. @FluentGallery/Views/PhotoDetailPage.xaml.cs 在缓存jpg 图片的时候内存占用正常，但是缓存几十张 heic 以后内存占用会大于 3GB
2. 从 PhotoDetailPage 返回 PhotoListPage 以后内存并没有被释放，任务管理器看还是占用了 3GB 内存

---

**Cursor**



让我先理解整个内存生命周期，看看 HEIC 图片在哪里被分配、缓存和释放。

Now let me check the DI registration to understand the lifecycle of MagickImageLoader:

The Grep tool is not returning content. Let me search more broadly:

Let me find the DI registration directly:

Now I have the full picture. Let me now look at the `CleanupLoading` and `OnNavigatedFrom` to understand problem 2 (memory not released after navigating back):

Now I have a thorough understanding. Let me analyze both problems:

**问题 1：HEIC 预加载缓存占用大量内存**

`DecodedImageData` 存储原始 BGRA8 像素数据。一张 12MP HEIC (4000x3000) = 4000 × 3000 × 4 bytes = **48 MB**。`MaxCacheSize` 默认是 11（由 `PreloadCountBack + PreloadCountForward + 1` 控制），但看看设置：

- 默认 `PreloadCountBack = 2`, `PreloadCountForward = 5` → `MaxCacheSize = 8`
- 但 `_pixelCache` 里的 `DecodedImageData` 是原始像素 buffer，即使被 evict 出去，`byte[] Pixels` 要等 GC 才能释放
- 更重要的是：`LoadAsync` 里会 `AddToCache`，但之后 `SoftwareBitmap.CreateCopyFromBuffer` 又做了一份 copy，`SoftwareBitmap.Convert` 再做一份——所以每张 HEIC 同时在内存中有 3 份 48MB 的数据

**问题 2：返回 PhotoListPage 后内存不释放**

`MagickImageLoader` 和 `WicImageLoader` 是 **singleton**，`ClearCache()` 清的是它们的 preload cache。但问题是：
- `DecodedImageData` 的 `byte[] Pixels` 是大数组，分配在 LOH (Large Object Heap) 上
- LOH 不会被 Gen0/Gen1 GC 回收，需要 Full GC (Gen2)
- .NET 不会主动做 Full GC，除非内存压力大到系统层面

### Fix 1: Remove pixel cache from MagickImageLoader entirely

The pixel cache is the root cause. Each HEIC decoded to BGRA8 costs ~48 MB. With MaxCacheSize=8, that's **384 MB** just for the pixel buffers, plus intermediate copies during `LoadAsync`. The cache was designed for preloading, but preloading already works via `PreloadAsync` — we should avoid keeping pixel data in memory after it has been converted to `SoftwareBitmapSource`. Instead, let the preload produce the `SoftwareBitmapSource` directly and cache *that*.

Actually, looking at the design more carefully: `PreloadAsync` stores raw pixels in cache, and `LoadAsync` consumes from cache to build `SoftwareBitmapSource`. The problem is that:
1. Decoded pixels stay in cache even after `LoadAsync` has consumed them
2. Each decoded entry is ~48 MB

The fix: **remove the cache entry after `LoadAsync` consumes it**, so we never hold both the pixel buffer AND the SoftwareBitmapSource for the same image.

### Fix 2: Reduce max pixel cache size based on memory budget

The preload cache stores raw pixels. Let's cap it to a memory budget rather than just a count. A 12MP HEIC = 48 MB, so 8 images = 384 MB. Let's cap at ~200 MB total pixel data.

### Fix 3: Force GC after navigating away

The HEIC pixel buffers are 48 MB each, allocated on the LOH. After `ClearCache()` removes the dictionary references, the `byte[]` arrays won't be collected until a Gen2 GC. We need to request a Gen2 GC after clearing caches.

### Fix 4: Ensure ZoomableImage fully releases SoftwareBitmapSource

Let me check if the `DeferDispose` in `ZoomableImage` might be holding references:

The Grep tool is frustrating. Let me search directly:

Let me look at `SetLoading()` in ZoomableImage:

`SetLoading()` does clear `_currentDisposable` via `DeferDispose`, but what about `_pendingDisposable`? Let me check if it's cleaned up:

`SetLoading()` doesn't dispose `_pendingDisposable`. If the page navigates away while a pending image is being loaded, `_pendingDisposable` (a `SoftwareBitmapSource`) will leak.

构建成功，0 个错误，0 个警告。

## 修复总结

### 问题 1：HEIC 缓存占 3GB+ 内存

**根因**：`MagickImageLoader` 的像素缓存（`_pixelCache`）存储原始 BGRA8 像素数据。一张 12MP HEIC = `4000 × 3000 × 4 = 48 MB`。缓存上限是 8-11 张，光像素缓存就 384-528 MB。更严重的是：

- `LoadAsync` 从缓存读取像素后**不会移除缓存条目**，导致像素 buffer 和生成的 `SoftwareBitmapSource` **同时驻留内存**
- 每次 `LoadAsync` 过程中会有 3 份内存副本同时存在：原始 `byte[]`、`SoftwareBitmap (Ignore)`、`SoftwareBitmap (Premultiplied)`

**修复**：
1. **`LoadAsync` 消费缓存条目**：从 `_pixelCache` 中移除已使用的条目，不再同时持有 pixel buffer 和 `SoftwareBitmapSource`
2. **增加内存预算限制**：除了按数量淘汰，还新增 200 MB 总内存预算限制，超出时淘汰最旧条目

### 问题 2：返回 PhotoListPage 后内存不释放

**根因**：
1. HEIC 像素 buffer 是 48 MB+ 的 `byte[]`，分配在 LOH (Large Object Heap)。`ClearCache()` 移除字典引用后，LOH 上的大数组需要 Gen2 GC 才能回收，而 .NET 不会主动触发 Gen2 GC
2. `ZoomableImage.SetLoading()` 只释放了 `_currentDisposable`，没有释放 `_pendingDisposable`，如果在 seamless 导航过程中离开页面，pending 的 `SoftwareBitmapSource` 会泄漏

**修复**：
1. **导航离开后触发非阻塞 Gen2 GC**：`GC.Collect(2, GCCollectionMode.Optimized, blocking: false)` 让 LOH 上的大数组被及时回收
2. **`SetLoading()` 同时释放 `_pendingDisposable`**：确保所有 `SoftwareBitmapSource` 都被 dispose

### 内存改善预期

- **之前**：浏览 20 张 HEIC → 缓存 8 张 × 48 MB pixel + 对应 SoftwareBitmapSource ≈ **768 MB+**，返回后不释放
- **之后**：缓存受 200 MB 预算限制（约 4 张 HEIC），`LoadAsync` 消费后立即释放 pixel buffer，返回后 GC 回收 → **峰值 ~300 MB，返回后降至 ~100 MB**

---

**User**

这两个问题都没有解决。你要不要加点 log

---

**Cursor**



好，我来加日志来定位问题。先在关键的内存分配和释放路径上加 log。

Now let me read the ZoomableImage cleanup code again:

好的。现在让我在所有关键路径上加详细的内存跟踪日志。

Now add logging to `LoadAsync`:

Now add logging to `ClearCache`:

Now add the `GetProcessMemoryMB` helper at the end of the class:

Now add logging to `CleanupLoading` and `ZoomableImage.SetLoading()`:

Now add logging to `ZoomableImage.SetLoading()` and `DeferDispose`:

Also add logging to `SetSource` and `SwapPendingToMain` so we can trace every SoftwareBitmapSource lifecycle:

Now add logging to the `LoadImageIntoMain` method:

Also add log to `SwapPendingToMain`:

Let me just search for the line number:

Now add log to `DeferDisposeSwipePreview` in Gestures.Support.cs:

Now build and test:

构建成功。现在你可以运行应用，按以下步骤测试：

1. **打开一个包含 HEIC 图片的相册**，进入 PhotoDetailPage
2. **浏览几十张 HEIC 图片**（左右键切换），观察任务管理器内存变化
3. **按返回键回到 PhotoListPage**
4. **等几秒钟**

然后把日志文件发给我。日志路径是：

```
%LocalAppData%\FluentGallery-Dev\logs
```

所有 `[MEM]` 开头的日志行会展示：
- 每张图片 preload/load 时的进程内存
- 像素缓存的命中/未命中和当前大小
- `DeferDispose` 是否被调用
- `ClearCache` 清了多少条目和多少 MB
- GC 前后的内存差异

这些日志能帮我定位具体是哪一层在泄漏：是像素缓存没释放、`SoftwareBitmapSource` 没 Dispose、还是 GC 没回收 LOH。

---

**User**

好了，你看看

---

**Cursor**



让我读取最新的日志文件。

Grep isn't returning content. Let me read the file directly:

I see no `[MEM]` lines at the beginning. This is all JPG. Let me look further into the log for HEIC content:

PowerShell dollar sign escaping issues. Let me use a different approach:

Let me just read the end of the log file directly:

非常清晰了！让我再看看导航离开时的日志：

日志非常清楚地揭示了两个核心问题：

## 分析结果

### 问题 1：内存持续增长到 3.4 GB

看关键数据点：

| 时间 | 操作 | 进程内存 |
|------|------|----------|
| 18:48:44 | 第1张 HEIC LoadAsync START | 292 MB |
| 18:48:44 | 第1张 HEIC LoadAsync DONE | **471 MB** (+179 MB) |
| 18:48:46 | 第2张 HEIC LoadAsync DONE | **627 MB** (+156 MB) |
| 18:48:49 | 第6张 HEIC LoadAsync DONE | **1020 MB** |
| 18:48:54 | 7个Preload同时启动 | **1870 MB** |
| 18:48:57 | 继续Preload | **2642 MB** |
| 18:49:04 | 最终 | **3397 MB** |

**根本原因**：虽然像素缓存限制在 192 MB（4张×48MB），但 **preload 过程中解码产生的中间 `byte[]` 数组分配在 LOH 上，被 evict 后 GC 没有及时回收**。每轮 preload 7张并发 = 7×48MB = 336 MB 同时在内存中被解码，加上缓存 evict 的旧 buffer 仍在 LOH 等待 Gen2 GC。

另外 `cacheHit=false` 和 `cacheRemaining=0/4` 说明：`LoadAsync` 消费缓存条目（移除）后，preload 在还未完成时用户已经导航走了，导致反复 miss + 反复全量解码。

### 问题 2：返回后内存不释放（3397→3398 MB）

```
CleanupLoading START: ProcessMB=3397
ClearCache: cleared 3 entries, 172.9 MB
CleanupLoading after ClearCache: ProcessMB=3397    ← 没变
CleanupLoading after GC: ProcessMB=3398             ← GC后反而略增
```

GC 完全没回收内存！即使是 `GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true)` 也没用。这说明有对象仍然在被引用——很可能是 **preload 任务在 CancellationToken 取消后仍然在执行解码，完成后把 `DecodedImageData` 写入了缓存/持有引用**。

看日志中的关键证据：`CleanupLoading` 时间是 18:49:08，但 18:49:04 还有多个 preload 在完成。preload 任务虽然被 cancel，但 `_pipeline.TryDecodeAsync` 已经在运行中，cancel 的 token 不一定能中断 Magick.NET 的解码——它会继续完成整个解码，分配 48-77 MB 的 `byte[]`，然后在 `AddToCache` 或异常处理后才释放。

现在让我修复真正的问题。

### Fix 1: 限制并发 preload 数量，避免同时 7 个解码

7 个 HEIC 同时解码 = 7×48 MB = 336 MB 仅解码中间数据。限制并发数为 2。

Now wrap the preload launch in `UpdatePreloadTasks` with the semaphore:

Now I need to remove the captured variables that were only used in the old lambda, and add the new method. Let me check the context:

