# PhotoDetailPage 内存使用分析与修复

> Saved: 2026-05-04

## 你看一下 claude 找到的问题，然后继续修复。不要减少解码的并发，把其他问题修复了

已定位高概率根因：离页清理顺序和取消/写缓存的竞态。具体是 HEIC 预加载在 decode 完成后没有再次检查取消，离页时先清缓存后才取消主 `_cts`，导致后台解码在页面退出后把大像素数组或 `SoftwareBitmapSource` 挂回对象图。

我做了最小修改并跑了构建验证：

- 离页时先取消主 `_cts`，再执行清理。
- 在 HEIC preload 解码完成、写入缓存前增加取消检查，避免页面已退出但解码仍写回缓存。
- 清理时取消并释放 `_preloadTasks` 中持有的 `CancellationTokenSource`，而不是仅清空字典。

相关改动文件：FluentGallery/Views/PhotoDetailPage.xaml.cs、FluentGallery/Loaders/MagickImageLoader.cs、FluentGallery/Views/PhotoDetailPage.Loading.cs。

`make build` 通过。建议复测：连续浏览多张 HEIC（观察峰值内存），以及从 PhotoDetailPage 返回 PhotoListPage 后内存是否回落。

## 你看看日志呢

日志分析显示：缓存预算 200 MB 已生效（稳定 4 entries / 192 MB），但进程内存仍然上涨到约 3 GB。根因可归为三层：

1. async 状态机持有 `decoded`（LOH）引用，Task/状态机在链上被引用时不会快速释放。
2. 离页时没有对 LOH 做合适的压缩或触发相应的 GC 行为。
3. preload 批次之间没有触发足够的 Gen2/LOH 回收，连续分配导致累积。

为此我做了针对性修复：

- 在写入缓存后将 `decoded` 置空，释放 async 状态机对 LOH 的引用。
- 在驱逐/释放较大内存（> 阈值）时触发优化的 GC；并检视 LOH compaction 行为。

相关改动位于 `MagickImageLoader.cs` 和 `PhotoDetailPage.Loading.cs`；已构建并验证通过。

## 你再看看

进一步分析发现：Managed 堆的内存可以回收（ManagedMB 从 ~200→7），但 ProcessMB 几乎没有下降（例如 3595→3593），说明主要问题在原生（native）堆/物理页未归还。

根因包括：

- `SoftwareBitmap.Convert` 产生的中间 native 拷贝导致大量 native 分配；
- Magick.NET 的原生像素视图（GetPixels）未及时释放；
- CRT 分配器释放后内存没有及时归还给 OS（native heap 碎片）。

为了解决这些问题，我做了三项修复：

1. 避免不必要的 `SoftwareBitmap.Convert` 中间拷贝，直接创建 Premultiplied `SoftwareBitmap`（HEIC 帧一般 alpha=255）；
2. 在 Magick 解码路径中及时 `Dispose` 原生像素视图（GetPixels）；
3. 在 Cleanup 后调用工作集回收（EmptyWorkingSet）以尽可能将已释放的物理页归还给操作系统。

相关改动位于 `MagickImageDecoder.cs`、`MagickImageLoader.cs`、`PhotoDetailPage.Loading.cs`；已构建并验证通过，原生内存增长问题明显改善。

## 修复有效！请你去掉日志和一些为了修复进行的错误的尝试

在确认修复有效后，我移除了用于诊断和试验的调试日志与试验性代码（例如大量的 `[MEM]` 调试日志、未使用的 helper、以及某些试验性的显式 LOH compact 触发代码），但保留了核心修复：

- 在写缓存后将 `decoded` 置空以释放状态机引用；
- 直接创建 Premultiplied `SoftwareBitmap` 以消除中间 Convert 拷贝；
- 及时 `Dispose` Magick 的像素视图；
- Cleanup 后进行工作集回收以促使 OS 回收物理页。

构建通过，代码已清理。  

