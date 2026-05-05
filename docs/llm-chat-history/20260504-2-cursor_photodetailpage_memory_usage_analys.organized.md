# Cursor Photodetailpage Memory Usage Analysis

> Saved: 2026-05-04

## 现在请你修复占用内存高的问题。

**问题 1：HEIC 预加载缓存占用大量内存**

`DecodedImageData` 存储原始 BGRA8 像素数据。一张 12MP HEIC (4000x3000) = 4000 × 3000 × 4 bytes = **48 MB**。`MaxCacheSize` 默认是 11（由 `PreloadCountBack + PreloadCountForward + 1` 控制），但看看设置：

- 默认 `PreloadCountBack = 2`, `PreloadCountForward = 5` → `MaxCacheSize = 8`
- `_pixelCache` 里的 `DecodedImageData` 是原始像素 buffer，即使被 evict 出去，`byte[] Pixels` 要等 GC 才能释放
- `LoadAsync` 中 `AddToCache` 后，`SoftwareBitmap.CreateCopyFromBuffer` 又会产生拷贝，`SoftwareBitmap.Convert` 可能再产生一次拷贝——所以每张 HEIC 同时在内存中可能有多份 ~48 MB 的数据

**问题 2：返回 PhotoListPage 后内存不释放**

`MagickImageLoader` 和 `WicImageLoader` 是 singleton，`ClearCache()` 清的是 preload cache。但问题是：

- `DecodedImageData` 的 `byte[] Pixels` 是大数组，分配在 LOH (Large Object Heap) 上
- LOH 上的大对象需要 Full GC (Gen2) 才能回收，.NET 不会在短时间内主动做 Full GC

修复要点：

- 避免同时持有 raw pixel buffer 和 `SoftwareBitmapSource`，尽快释放 pixel buffer
- 根据内存预算限制 pixel cache，而不是仅按图片数量
- 在合适时机触发或请求 Gen2 GC，使 LOH 上的大数组能被回收
- 确保 `ZoomableImage` 的 `_pendingDisposable` 及相关 `SoftwareBitmapSource` 能被释放

**建议修复（概要）**

1. 在 `LoadAsync` 消费缓存条目后从 `_pixelCache` 中移除已使用的 entry，避免同时持有 pixel buffer 与 `SoftwareBitmapSource`。
2. 将 preload 缓存改为按内存预算（例如 ~200 MB）而非纯计数，超出时淘汰最旧条目。
3. 在页面导航离开并清除 cache 后，发起 Gen2 GC（例如 `GC.Collect(2, GCCollectionMode.Optimized, blocking: false)`）以加速 LOH 回收。
4. 检查并确保 `ZoomableImage.SetLoading()` 也释放 `_pendingDisposable`，避免在导航或取消加载时留下 `SoftwareBitmapSource` 引用。

**内存改善预期**

- 之前：浏览多张 HEIC 时峰值可能达到数 GB
- 之后：通过限制 preload 并消费/释放 pixel buffer、按内存预算缓存，峰值应大幅下降（示例估算：峰值 ~300 MB，返回后降至 ~100 MB）

---

## 这两个问题都没有解决。你要不要加点 log

我已在关键的内存分配与释放路径加入内存跟踪日志并构建成功（0 个错误，0 个警告）。

请按以下步骤重现并收集日志：

1. 打开包含 HEIC 的相册，进入 `PhotoDetailPage`
2. 浏览若干 HEIC（左右切换）以触发 preload/load
3. 返回 `PhotoListPage`
4. 等几秒钟，然后把日志发给我

日志路径：

```
%LocalAppData%\FluentGallery-Dev\logs
```

日志中所有以 `[MEM]` 开头的行会展示：每张图片 preload/load 时的进程内存、像素缓存的命中/未命中与当前大小、`DeferDispose` 调用情况、`ClearCache` 清理条目和 MB 数、以及 GC 前后的内存差异。这些信息能帮助定位是 pixel buffer 未释放、`SoftwareBitmapSource` 未被 Dispose，还是 LOH 未被 GC 回收等问题。

---

## 好了，你看看

**分析结果（基于你上传的日志）**

### 问题 1：内存持续增长到 ~3.4 GB

关键数据点（摘录）：

| 时间 | 操作 | 进程内存 |
|------|------|----------|
| 18:48:44 | 第1张 HEIC LoadAsync START | 292 MB |
| 18:48:44 | 第1张 HEIC LoadAsync DONE | **471 MB** (+179 MB) |
| 18:48:46 | 第2张 HEIC LoadAsync DONE | **627 MB** (+156 MB) |
| 18:48:49 | 第6张 HEIC LoadAsync DONE | **1020 MB** |
| 18:48:54 | 7个Preload同时启动 | **1870 MB** |
| 18:48:57 | 继续Preload | **2642 MB** |
| 18:49:04 | 最终 | **3397 MB** |

根本原因：虽然 pixel cache 的容量看似被限制在 ~192 MB（4 张 × 48 MB），但 preload 过程中的并发解码会在 LOH 上分配大量中间 `byte[]`（每张 ~48 MB）。并发 7 张的解码瞬间就会产生 ~336 MB 的临时分配，同时旧的被 evict 的 buffer 仍在 LOH 等待 Gen2 GC 回收，导致峰值飙升。

另外日志中 `cacheHit=false` 与 `cacheRemaining=0/4` 的组合表明：`LoadAsync` 可能在消费缓存后，后续的 preload 又在重解码（用户在导航间隙触发了不一致的并发流），产生重复分配。

### 问题 2：返回后内存不释放（3397 → 3398 MB）

日志示例：

```
CleanupLoading START: ProcessMB=3397
ClearCache: cleared 3 entries, 172.9 MB
CleanupLoading after ClearCache: ProcessMB=3397    ← 没变
CleanupLoading after GC: ProcessMB=3398             ← GC后反而略增
```

即使显式触发了 Full GC，也没有回收内存，这表明某些对象仍被引用。最可能的来源是：

- preload 任务在收到取消信号后仍在进行 Magick 解码（Magick.NET 解码不可中断或难以中断），其完成后仍会把 `DecodedImageData` 写入缓存或临时持有，导致对象被重新引用而无法回收。

### 修复建议（优先级）

1. 限制并发 preload 数量（例如从 7 降到 2），避免同时解码过多 HEIC，减少 LOH 临时分配峰值。
2. 在 `LoadAsync` 消费缓存时立即移除对应的 pixel buffer，避免同时持有两份副本。
3. 将 pixel cache 改为按内存预算（例如 ~200 MB）而非单纯按图片数量。
4. 在导航或清理路径上，确保取消 token 能更早停止预解码任务；若不可中断，则在任务完成后不要将结果写回长期缓存。

---

（已包含最后一条用户交互；文件尾部覆盖了所有用户提问）
