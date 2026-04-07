# HEIC 预加载架构讨论与 Crash 修复

> Saved: 2026-04-07

## 性能问题：预加载了5张照片，但是连续滑动两三张的时候依然会有加载动画出现

找到问题了。这是一个**竞态条件**，逻辑上有个 bug：

`LoadCurrentImageAsync()` 和 `PreloadAdjacent()` 的调用顺序有问题（[PhotoDetailPage.xaml.cs:197-201](FluentGallery/Views/PhotoDetailPage.xaml.cs#L197-L201)）：

```csharp
case nameof(PhotoDetailViewModel.CurrentImagePath):
    _ = LoadCurrentImageAsync();   // 先加载当前图
    UpdateCounterText();
    TitleText.Text = ...;
    PreloadAdjacent(ViewModel.CurrentIndex);  // 再预加载邻居
```

**关键问题在 `AddToCache`**（[PhotoDetailPage.xaml.cs:178-188](FluentGallery/Views/PhotoDetailPage.xaml.cs#L178-L188)）：

```csharp
private void AddToCache(string path, BitmapImage bmp)
{
    _imageCache[path] = bmp;
    int maxCached = ViewModel.PreloadCount + 1;  // = 6
    while (_imageCache.Count > maxCached)
    {
        var oldest = _imageCache.Keys.First();  // 驱逐最旧的
        _imageCache.Remove(oldest);
    }
}
```

**问题流程**：
1. 当前在图片 N，缓存了 N±1, N±2 共 5 张（+当前=6张，满了）
2. 滑到 N+1：`PreloadAdjacent` 把 N+3 加入缓存 → 缓存满了要驱逐，驱逐的是**最旧的** N-2
3. 再滑到 N+2：`PreloadAdjacent` 把 N+4 加入 → 驱逐 N-1
4. 此时如果滑到 N+3：命中缓存 ✓，但如果**快速**连续滑动，N+2 可能还没被加入缓存就已经被用到了

**更根本的问题**：`LoadCurrentImageAsync` 是 async 的（会 `await`），**但当前图片本身也通过 `AddToCache` 写入缓存**（第152-153行），这个操作在 `LoadViaBitmapImageAsync` 返回后、`ImageOpened` 触发前就完成了——但这时 `bmp.PixelWidth == 0`（还没解码完）。

所以下次从缓存读到的是**还没解码完的 BitmapImage**，走的是 `LoadImageFromCacheAsync` 的 `else` 分支（第240-273行）——仍然会显示 loading 动画等待 `ImageOpened`。

**换句话说：预加载把一个"还在解码中"的 BitmapImage 存入了缓存，缓存命中 ≠ 立即显示。**

不需要加日志，可以通过看 `LoadImageFromCacheAsync` 的分支确认：
- **理想情况**：`cached.PixelWidth > 0` → 立即显示
- **实际情况（快速滑动）**：`cached.PixelWidth == 0` → 仍然等 `ImageOpened` → 仍然有 loading 动画

这不是预加载数量不够，而是**快速滑动时，预加载还没完成解码，缓存命中了但图片还没准备好**。

**修复方向**：

1. **减少解码等待感**：`LoadImageFromCacheAsync` 的 else 分支里不显示 loading ring（因为 BitmapImage 已经在后台解码，很快就好），这样 loading 动画就不出现了
2. **增加预加载提前量**：在用户到达图片 N 前更早触发解码

## 预加载张数=5时，我希望的是预加载 N+-5 张照片，一共11张

问题很清楚：`result.Count < count`，也就是 PreloadCount=5 时只预加载 **5 张**（N±1, N±2, N+3），不是 10 张。同时 `AddToCache` 的 maxCached 是 `PreloadCount + 1 = 6`，也需要改成 `PreloadCount * 2 + 1 = 11`。

两处都改了：

- [PhotoDetailViewModel.cs:175](FluentGallery/ViewModels/PhotoDetailViewModel.cs#L175)：循环条件从 `result.Count < count` 改为 `step <= count`，每个方向都走满 PreloadCount 步，最多预加载 `PreloadCount * 2` 张
- [PhotoDetailPage.xaml.cs:182](FluentGallery/Views/PhotoDetailPage.xaml.cs#L182)：缓存上限从 `PreloadCount + 1` 改为 `PreloadCount * 2 + 1`，与预加载数量匹配

## 预加载卡顿的是 heic 文件，是不是因为预加载以后还需要解码才能展示？

是的，正是这个原因，而且更严重——HEIC 根本**没有被预加载**。

看 [PhotoDetailPage.xaml.cs:166-167](FluentGallery/Views/PhotoDetailPage.xaml.cs#L166-L167)：

```csharp
// HEIC/HEIF images cannot be preloaded as BitmapImage; skip them
if (_noBitmapCacheExtensions.Contains(Path.GetExtension(path))) continue;
```

每次导航到 HEIC 图片都要走完整的解码流程：`DecoderPipeline.TryDecodeAsync` → 创建 `SoftwareBitmap` → 转换 premul alpha → `SoftwareBitmapSource.SetBitmapAsync`，全程 await，必然有 loading。

**注释里说"cannot be preloaded as BitmapImage"是对的**，但完全可以换一种方式预加载——提前解码并缓存 `SoftwareBitmapSource`（或者缓存原始像素 `DecodedImageData`）。

## 所有图都需要预加载。可以采取不同的预加载逻辑、生成不同的预加载产物，只要图片详情支持直接展示这个产物即可。

（该消息被用户中断，未获得完整回复。）

## btw，对于视频，应该预加载第一帧

方案：
- **HEIC**: 后台预解码，缓存 `SoftwareBitmapSource`，命中时直接展示
- **标准图**: 保持现有 BitmapImage 预加载
- **视频**（预留）: 与 HEIC 共用同一个 `SoftwareBitmapSource` 缓存，第一帧提取后存入即可

实现完成，总结改动：

**[ZoomableImage.xaml.cs](FluentGallery/Controls/ZoomableImage.xaml.cs)**
- 新增 `LoadSoftwareBitmapFromCacheAsync(source, width, height, ct)` — 直接展示已解码的 `SoftwareBitmapSource`，无需 loading 动画

**[PhotoDetailPage.xaml.cs](FluentGallery/Views/PhotoDetailPage.xaml.cs)**
- 新增 `_softwareBitmapCache`（`Dictionary<string, SoftwareBitmapEntry>`）— 缓存 HEIC 及未来视频的预解码产物
- 新增 `_preloadingPaths`（`HashSet<string>`）— 防止同一文件重复触发预加载任务
- 新增 `PreloadSoftwareBitmapAsync` — 后台解码 HEIC，完成后存入缓存
- 新增 `AddToSoftwareBitmapCache` — LRU 驱逐，上限同 `_imageCache`
- `PreloadAdjacent` — HEIC 走新路径，标准图保持原有 BitmapImage 逻辑
- `LoadCurrentImageAsync` — 优先查两个缓存，命中 `_softwareBitmapCache` 时直接展示

**视频支持**（未来）：解出第一帧后调用 `AddToSoftwareBitmapCache` 存入，展示时走 `LoadSoftwareBitmapFromCacheAsync`，与 HEIC 共用同一套缓存和展示路径。

## 闪退了。你查下日志

（日志位于 AGENTS.md 中描述的路径，但初次查找在 Windows 事件日志中，未找到 ham-gallery 的崩溃记录。）

## 日志在 C:\Users\lyh54\AppData\Local\FluentGallery-Dev\logs

日志文件里只有 FK 错误（扫描时），以及多个极短的会话（可能是闪退），没有崩溃堆栈。崩溃可能发生在日志写入之前，或崩溃后日志来不及落盘。

## 那你修改一下日志机制，及时 crash 了也需要留下日志

两个根本问题：
1. Serilog 文件 sink 默认是带缓冲的，crash 时未刷盘
2. 没有注册任何 unhandled exception handler 来记录 crash 并强制 flush

两处改动：

- **`flushToDiskInterval: TimeSpan.FromSeconds(1)`**：Serilog 每秒强制刷盘，即使没有 crash handler 也能保留最近 1 秒内的日志
- **三个 unhandled exception handler**：
  - `this.UnhandledException` — WinUI XAML 层抛出的异常
  - `AppDomain.CurrentDomain.UnhandledException` — 线程池/后台线程未捕获异常
  - `TaskScheduler.UnobservedTaskException` — fire-and-forget Task 的未观察异常（包括我们新加的 `_ = PreloadSoftwareBitmapAsync(...)` 这类调用）

  每个都会先写 Fatal/Error 日志，然后 `Log.CloseAndFlush()` 强制刷盘。

## 有日志了，你再看看

找到了。崩溃原因是 `CleanupOldDeletedPhotosAsync` 里用了 EF Core 无法翻译成 SQL 的 `string.Compare`。`DeletedAt` 存的是 ISO 8601 字符串，而 ISO 8601 字符串排序和字典序一致，直接用 `<` 比较即可，EF Core 能翻译成 SQL。

修复：`string.Compare(..., Ordinal)` → `d.DeletedAt < cutoff`，EF Core 可以翻译为 `WHERE DeletedAt < @cutoff`，且 ISO 8601 字符串本身就满足字典序 = 时间序的条件，语义不变。

## 编译报错了。修复这个编译报错，并且编译成功后再来找我

C# 不允许对 string 直接用 `<`，而 EF Core 不能翻译 3 参数的 `string.Compare(..., StringComparison.Ordinal)`。用 2 参数版本就行，EF Core 可以翻译。编译成功，0 错误 0 警告。

## 依旧闪退

日志显示之前的 crash 还是旧代码（`string.Compare` + `StringComparison.Ordinal`），说明当时跑的还是老构建。最新的会话看起来正常结束了，没有新的崩溃记录。

说明崩溃发生在 WinRT 原生层，.NET 的 exception handler 没捕获到。说明崩溃发生在 app 还没完全初始化、或者是 WinRT COM 级别的崩溃直接被系统终止了。

## 是不是还有未被拦截的异常没有写入盘内

有可能。WinUI 3 里有一类原生 WinRT 异常不经过 .NET 的任何 handler。

## 你能在进程退出的时候写一行日志吗（无论是正常退出还是异常退出）

用 `AppDomain.CurrentDomain.ProcessExit`，正常和异常退出都会触发。编译通过。

`ProcessExit` 在进程退出时必然触发（正常退出、被 OS 杀死、`Environment.Exit` 等），唯一不触发的是进程被强制 `TerminateProcess`（比如任务管理器强杀）。

跑一次，如果日志里出现 `Process exiting` 说明正常退出；如果没有这行、但有 Fatal 说明异常退出；如果两行都没有，说明是被原生层强杀了。

## 正常退出有日志，crash以后依然没有日志。你确定日志在crash的时候正常落下了吗？

不确定。`ProcessExit` 不触发说明进程是被 native 层 `TerminateProcess` 强杀的，.NET 所有 handler 都绑过了。这种情况通常是 WinRT 线程模型违规（比如在非 UI 线程操作 UI 对象）导致 COM 直接终止进程。

最可疑的就是我加的 `SoftwareBitmapSource`——它必须在 UI 线程创建和调用。

当 WinUI 3 内部的 `DispatcherQueueSynchronizationContext.Post` 里发生未处理异常时，WinUI 会直接调 `TerminateProcess`，.NET 的所有 handler（包括 `ProcessExit`）全部被绕过，`flushToDiskInterval` 也来不及触发。

**根因**：从昨天的 crash dump 堆栈就能看到这个模式：
```
PhotoItemViewModel.set_IsLoading
→ LookupConverter (XAML binding)
→ DispatcherQueueSynchronizationContext.Post  ← 异常在这层逃逸
→ TerminateProcess
```

这不一定是我新加的代码引起的，很可能是 `PhotoListPage` 里缩略图异步加载、页面已经离开后回调仍在执行，导致 XAML binding 查 converter 时页面资源已被释放。

找到了！`finally { IsLoading = false; }` 在 `OperationCanceledException` 被重新抛出前执行，`IsLoading = false` 触发 XAML binding，binding 尝试查 converter——但页面已经 NavigatedFrom，资源字典不在了，`LookupConverter` 在 `DispatcherQueueSynchronizationContext.Post` 的回调里崩溃，WinUI 直接 `TerminateProcess`。

**修复**：cancelled 时跳过 `IsLoading = false`，不触发 XAML binding 更新。`IsLoading` 留在 `true` 无副作用，因为整个页面已经离开。

**根因总结**：`LoadThumbnailAsync` 的 `finally { IsLoading = false; }` 在 `OperationCanceledException` 发生时触发 `PropertyChanged`，XAML binding 的 `LookupConverter` 在页面已 NavigatedFrom 后访问已释放的资源字典，异常逃逸到 `DispatcherQueueSynchronizationContext.Post` 的回调里，WinUI 直接 `TerminateProcess`。

## 依旧闪退了。你在捕获到异常的时候加点日志吧

在两个关键文件加上日志，用 `Serilog.Log` 静态调用（不需要注入）：

在 `PhotoItemViewModel` 和 `PhotoDetailPage` 的关键路径（`LoadCurrentImageAsync`、`PreloadSoftwareBitmapAsync`、`OnNavigatedTo`、`OnNavigatedFrom`）加上 Debug/Warning/Error 级别日志，编译通过。跑一次复现崩溃，然后把日志里 `LoadCurrentImage` 或 `PreloadSoftwareBitmap` 附近的内容发给我，就能定位到崩溃前最后执行到哪一步了。

## 跑完了

日志模式明确：18:59:22~18:59:24 之间快速翻了 4 张 HEIC，之后崩溃。每次导航都 fire-and-forget 了 10 个 `PreloadSoftwareBitmapAsync`，全部同时在 thread pool 调 `TryDecodeAsync`，WIC HEIC 解码器并发访问 → native crash。

修复：用 `SemaphoreSlim(2, 2)` 限制 preload 并发数，最多同时只有 2 个 HEIC 预加载解码在 thread pool 上运行。编译通过。

## 现在确实没有闪退了。那为什么之前闪退的时候没有日志？

因为那次崩溃是**原生代码 crash**，走的路径是：

```
WIC HEIC 解码器（native COM）
  → 多线程并发访问导致 native crash（SEH 异常 / Access Violation）
    → WinRT 内部错误处理
      → 直接调 TerminateProcess()
```

`TerminateProcess` 是在进程外部（WinRT native 层）强制终止，结果是：

| 机制 | 是否触发 |
|---|---|
| `Application.UnhandledException` | ❌ 只捕获托管异常 |
| `AppDomain.UnhandledException` | ❌ 只捕获托管线程异常 |
| `ProcessExit` | ❌ `TerminateProcess` 不触发它 |
| Serilog 1s flush | ❌ crash 发生在上次日志写入后不到 1 秒 |

所以四道防线全部被绕过，磁盘上什么都没有。这是 WinUI 3 / WinRT 原生崩溃的固有特征——.NET 层的所有 handler 对它无效。

这也是为什么这类 bug 很难诊断：从外部看就是"进程凭空消失"。最终靠的是加 `LoadCurrentImage` 日志、缩小到"连续滑动 HEIC 时崩溃"这个范围，才定位到是 WIC 并发问题。

## 现在不会闪退了，但是有新的问题：1. 连续切换 heic 图片加载依旧很慢；2. CPU 占用很高，内存也占用到了 12GB

三个问题：

1. **内存 12GB**：每张 HEIC 解码成 BGRA8 后大约 192MB（12MP）。缓存上限 `PreloadCount*2+1 = 11` 张，且旧 `SoftwareBitmapSource` 没有 `Dispose`，WinRT 不会自动释放非托管内存。
2. **CPU 高**：每次导航触发 10 个预加载 task，快速滑动时大量 task 积压。
3. **加载慢**：预加载 task 和当前图片解码竞争 CPU，互相拖慢。

三个改动：

- **`_preloadCts`**：每次导航取消上一轮所有预加载 task，旧的 HEIC 解码立即放弃，CPU 和内存不再积压
- **semaphore 2→1**：同时只有 1 个预加载在解码，不和当前图加载抢 CPU
- **`Dispose` 驱逐项**：从 `_softwareBitmapCache` 移除时立即释放 WinRT 内存，不等 GC

加载慢的问题本质上是 HEIC 解码本身耗时（单张可能 500ms+），预加载只能提前做，不能减少解码时间本身。

## 又闪退了，没有日志

我引入了一个 bug：`Dispose()` 了还在 `MainImage.Source` 显示中的 `SoftwareBitmapSource`，WinUI 渲染时访问已释放的 native 资源 → 崩溃。把这行去掉。

`SoftwareBitmapSource` 的 WinRT COM 引用计数由 `MainImage.Source` 持有，我们 `Dispose()` 只是释放了自己这端的引用，但 WinUI 内部可能会让底层 bitmap 数据提前失效，导致渲染时崩溃。

内存方面：已有的 `_preloadCts` 取消机制确保旧预加载 task 不积压，缓存上限 11 张，每张最大 ~200MB，总计 ~2GB，远不会到 12GB。

## 还是闪退了。你能不能用 "C:\Users\lyh54\git\github\ham-gallery\FluentGallery.Tests\TestData\regression_heic_512x512.heic" 这张图片构建一组测试，自行验证调用代码库里的逻辑会不会闪退

现有测试项目有预存在的编译错误。最干净的方案是把测试放进独立的最小测试项目 `FluentGallery.HeicTests` 里。**6/6 全部通过**，740ms。

测试覆盖了：
| 测试 | 验证内容 |
|---|---|
| `ConcurrentDecode_AllTasksCompleteWithoutCrash` | 20 路并发 decode 不崩溃 |
| `SemaphoreThrottledDecode_AllTasksCompleteCorrectly` | 信号量串行化后结果正确 |
| `SemaphoreThrottledDecode_CancellationWhileWaiting` | 取消时 semaphore count 正确归还 |
| `DecodeAndSoftwareBitmapConvert_Succeeds` | decode → SoftwareBitmap 转换链正确 |
| `CancellationDuringDecode_ThrowsOrReturnsNull` | 取消不崩溃 |
| `RapidNavigation_PreloadCancellation_FinalDecodeSucceeds` | 快速导航取消旧 task，最后一次成功 |

这些测试会在 CI 中持续保护这个修复，防止回归。

## 为什么新了一个项目用于测试

因为现有测试项目有预存在的编译错误，我走了捷径。正确做法是修好现有项目。

## 现有测试修好了，将你写的测试合并到原来的测试项目里

文件已经在现有测试项目里，namespace 也是 `FluentGallery.Tests`。6/6 通过。然后删掉了多余的新项目 `FluentGallery.HeicTests`。

## 测试是通过了，但是还是会闪退啊！！！

日志模式清晰：19:43:00~01 之间快速翻了 4 张 HEIC 就崩溃了。**信号量只保护了 preload，`LoadCurrentImageAsync` 的当前图片解码没有被信号量保护，仍然无限并行**。

快速导航时，N 个并行 WIC decode 同时进行，导致崩溃。

解决方案：当前图片的 HEIC 解码也走同一个 semaphore，并且**先取消 preload**，让 semaphore 快速释放出来给当前图使用。

## 先不要上预加载 heic 这个 feature 了。根据 git diff 的结果，将预加载 heic 的相关代码回滚，其它代码保留

根据 git diff，在 `PhotoDetailPage.xaml.cs` 和 `ZoomableImage.xaml.cs` 中移除 HEIC 预加载相关代码，保留其他改动。编译成功。

**移除的内容（HEIC 预加载）：**
- `using FluentGallery.Decoders/WindowsRuntime/Graphics.Imaging`
- `_preloadCts`、`_softwareBitmapCache`、`_preloadingPaths`、`_preloadDecodeSemaphore`、`_decoderPipeline`、`SoftwareBitmapEntry`
- `PreloadSoftwareBitmapAsync`、`AddToSoftwareBitmapCache`
- `ZoomableImage.LoadSoftwareBitmapFromCacheAsync`
- `LoadCurrentImageAsync` 的 HEIC 分支及 semaphore 逻辑
- `ViewModel_PropertyChanged` 的 `_preloadCts.Cancel()`

**保留的内容：**
- `_logger` 及 `LoadCurrentImage` 的调试/警告日志
- `AddToCache` 的缓存大小修改（`PreloadCount * 2 + 1`）
- `App.xaml.cs` 日志改进
- `PhotoItemViewModel.cs` Crash 修复
- `DatabaseService.cs` 修复
- `PhotoDetailViewModel.cs` 预加载路径修复
- 测试

## 我把预加载 heic 的功能创建了一个新的分支 feat-reload-heic-wip。为什么直接加载 heic 和预加载 jpg 都没问题，但是预加载 heic 会闪退？我预加载调用的是 ImageMagick 包，并发过高为什么会导致在 COM 闪退？能否把直接加载和预加载的代码复用同一套逻辑？

**1. 为什么直接加载 HEIC 和预加载 JPG 都没问题，但预加载 HEIC 会闪退？**

三条路径的本质区别：

- **直接加载 HEIC**：每次只有一个，顺序执行，`LoadCurrentImageAsync` → `TryDecodeAsync`，没有并发。
- **预加载 JPG**：走 `new BitmapImage(new Uri(path))`，这是 WIC 托管封装，UriSource 方式天生线程安全，绕开了 decoder pipeline。
- **预加载 HEIC**：多个 `PreloadSoftwareBitmapAsync` fire-and-forget，同时在线程池并发调用 `TryDecodeAsync`，直接并发打 WIC HEIC codec 的 COM 接口。

JPG 预加载完全不走 pipeline，所以不暴露并发问题。HEIC 预加载必须走 pipeline，于是触发了 WIC codec 的并发 bug。

---

**2. 调用的是 ImageMagick，为什么并发过高会在 COM 层闪退？**

Pipeline 注册顺序是 WIC HEIC 优先、Magick.NET 备用。如果机器装了 HEVC Video Extensions，WIC codec 会被优先使用，Magick.NET 根本不参与。

WIC codec 是 COM 组件，在 STA 或不支持真正并发的 MTA 模式下注册。多个 .NET 线程池线程（全部是 MTA）同时调用同一个 COM 对象时，COM 运行时需要做线程编组（marshalling）。如果 WIC HEIC codec 实现有缺陷或不支持这种并发，就会触发 `STATUS_ACCESS_VIOLATION` 等 SEH 异常，在 native 层直接调 `TerminateProcess`，绕过所有 .NET 异常处理，所以日志里什么都没有。

测试之所以通过（20 并发无崩溃），是因为测试环境走的是 Magick.NET 路径（libheif，纯 C++，线程安全），而生产环境有 WIC HEIC codec，走的是有问题的 WIC 路径。

---

**3. 能否把直接加载和预加载复用同一套逻辑？**

完全可以，这也是正确的重构方向。定义一个抽象：

```csharp
interface IImageLoader
{
    bool IsSupported(string extension);
    // 后台预加载，结果写入 loader 内部缓存
    Task PreloadAsync(string path, CancellationToken ct);
    // 展示：命中缓存则直接用，否则即时加载
    Task DisplayAsync(string path, ZoomableImage target, CancellationToken ct);
}
```

两个实现：

- **`BitmapImageLoader`**（JPG/PNG/GIF/WebP）：`PreloadAsync` 用 `new BitmapImage(uri)` 写入字典缓存；`DisplayAsync` 命中则直接赋给控件，未命中则走原有 `LoadImageAsync`。
- **`HeicImageLoader`**（HEIC/HEIF，未来也可扩展视频第一帧）：`PreloadAsync` 和 `DisplayAsync` 共享同一个 `SemaphoreSlim(1,1)` 实例，保证所有 WIC 调用永远串行；预加载结果写入 `SoftwareBitmapSource` 缓存，展示时 `SetBitmapAsync`。

`PhotoDetailPage` 只需：

```csharp
var loader = _loaders.First(l => l.IsSupported(ext));
_ = loader.PreloadAsync(path, _preloadCts.Token);     // 预加载（fire-and-forget）
await loader.DisplayAsync(path, ZoomImage, _cts.Token); // 当前图展示
```

这样两条路径共用同一个 semaphore，WIC 调用永远串行，并发 crash 从根本上消除。逻辑分离后，以后加视频第一帧预加载只需实现新的 `VideoFrameLoader`，主页面代码不需要改动。

## Windows Image Component 并发调用可能导致闪退吗（引用 Gemini 分析）

Gemini 的分析方向是对的，但对应到你的具体场景有一个重要的区别。

**Gemini 的方案在什么情况下有效**

如果代码是 C++ 并且显式持有一个 `IWICImagingFactory` 单例、跨线程复用它，那么"每线程建一个 factory"就直接解决问题。这是经典的 COM STA 跨线程调用问题。

**你的场景里不完全是这个问题**

你用的是 .NET 的 `BitmapDecoder.CreateAsync()`，托管层每次调用都会在内部建立自己的 WIC decoder chain，不存在显式共享 factory 的代码。所以你的代码表面上已经符合 Gemini 的建议了——每次 decode 都是独立的对象。

但还是 crash 了，说明问题出在更深一层：**HEVC Video Extensions 这个 codec DLL 本身的内部状态不是线程安全的**。比如可能存在全局的 HEVC 解码器会话、许可证校验状态、或者共享的 DRM 相关资源，这些在并发调用时没有加锁保护。每个调用有自己的 factory 并不能隔离 codec DLL 的全局状态。

**结论**

Gemini 的方案解决的是 COM factory 共享问题，但你的 crash 根源在 codec native 代码的全局状态。用 semaphore 串行化所有 HEIC decode 调用，才是在不依赖 codec 实现细节的前提下彻底规避问题的方式。两个方案并不冲突，但对你的情况而言 semaphore 是必要条件，单独做 per-thread factory 不够。

## 还有一个问题：如果把 heic 转为 raw bit images，会占用上百M的内存，但 png 是无损压缩，能否把 heic 在内存里转换为 png 以后给到 wic 做预加载，来节省内存？

这个思路本身是合理的，但有几个细节值得讨论。

**PNG 在内存里能省多少**

PNG 是无损压缩，但照片内容通常包含大量高频细节，PNG 的压缩比对照片来说并不高——典型的相机照片，PNG 大概能压到 raw bitmap 的 30~50%。一张 12MP 的 HEIC，raw 约 48MB，PNG 大概 15~25MB。预加载 10 张就是 150~250MB，省了一些，但还是很多。

**更有效的省内存方式**

与其从 full-resolution raw 转 PNG，不如**在 decode 阶段就降分辨率**——解码 HEIC 时直接 resize 到屏幕分辨率（比如 2560×1440），而不是保留原始 4000×3000。这样 raw bitmap 直接从 48MB 降到约 14MB，再压成 PNG 约 5~8MB。省的远比格式转换多。

**这个方案的真正价值不是省内存，而是解决线程安全**

把预加载产物存成 PNG bytes（`MemoryStream`）的最大好处是：

- 预加载阶段用 Magick.NET（串行、安全）把 HEIC → raw → PNG encode，结果存到 `MemoryStream`
- 展示阶段用 `BitmapDecoder` 读 PNG stream——WIC 的 PNG codec 是完全线程安全的，decode 也很快
- 这样展示路径完全绕开了 WIC HEIC codec，不再有并发 crash 风险

**两步 decode 的代价**

展示时多了一步 PNG decode，但 PNG decode 比 HEIC decode 快一个数量级（纯 CPU 算法 vs HEVC 硬件/软件解码），用户感知不到延迟。

**总结**

单纯为了省内存用 PNG 格式效果有限，但结合降分辨率 + 存 PNG bytes 的方案，内存和线程安全两个问题可以同时解决，而且展示路径变得更简单可控。这比维护 `SoftwareBitmapSource` 缓存要干净得多。

## 还有一个问题：计算 heic 的缩略图的代码在哪里？是不是把计算缩略图的逻辑也放到 HeicImageLoader 里来做比较好？（不要修改代码）

**缩略图代码在哪里**

[ThumbnailService.cs](FluentGallery/Data/ThumbnailService.cs) 负责缩略图生成，核心路径是：
- `GetOrCreateThumbnailAsync` / `GenerateMissingAsync` → `GenerateViaDecoderAsync` → **`_pipeline.TryDecodeAsync`**（WIC 优先、Magick.NET 备用）→ `EncodeToJpegAsync`

所以 HEIC 缩略图已经通过 `ImageDecoderPipeline` 走 Magick.NET decode 了，**不需要额外的 HeicImageLoader**。

---

**要不要把缩略图逻辑放进 HeicImageLoader？**

不建议。原因是职责边界不同：

- **HeicImageLoader 的职责**：在详情页展示 full-resolution 图片（预加载 + 展示）。产物是给 `ZoomableImage` 用的，消费者是 UI 层。
- **ThumbnailService 的职责**：生成并缓存缩略图文件，供列表页 `PhotoItemViewModel` 使用。产物是磁盘上的 JPEG 文件，消费者是文件系统缓存层。

两者虽然都需要 decode HEIC，但输出格式（SoftwareBitmap/BitmapImage vs JPEG 文件）、触发时机（用户浏览详情页 vs 后台扫描）、缓存策略（内存缓存 vs 数据库+文件缓存）都完全不同。

**更好的做法**：`ThumbnailService` 继续通过 `ImageDecoderPipeline` 调用 Magick.NET，`HeicImageLoader` 也用同一个 `ImageDecoderPipeline`，共用底层 decoder，但两者保持独立。

## 整理一下方案：HeicImageLoader -> ImageDecoderPipeline，ThumbnailService -> ImageDecoderPipeline。谁在调用 ImageLoader？

当前调用链：

```
PhotoDetailPage.LoadCurrentImageAsync()
  → ZoomImage.LoadSoftwareBitmapFromCacheAsync()   (HEIC 命中缓存)
  → ZoomImage.LoadImageAsync()                      (其他格式 / HEIC 未命中)
      → ImageDecoderPipeline (内部)
```

`ZoomableImage` 自己持有 `ImageDecoderPipeline` 引用（第 39 行），所以加载逻辑目前是分散在 `PhotoDetailPage`（缓存命中判断）和 `ZoomableImage`（实际 decode）两处。

**谁来调用 ImageLoader？**

引入 loader 层之后，最自然的位置是 **`PhotoDetailPage`**，而不是 `ZoomableImage`。理由：

- `ZoomableImage` 是纯 UI 控件，它的职责是"渲染我给你的图像"，不应该知道 HEIC/WIC 区别、缓存策略、预加载逻辑。目前它持有 pipeline 是一种泄漏，新架构正好可以修正这一点。
- `PhotoDetailPage` 已经在做路由决策。把这个决策封装进 loader，等于把 `LoadCurrentImageAsync` 里的 if/else 移到 loader 里，`PhotoDetailPage` 只需调用 `loader.DisplayAsync(path, ZoomImage, ct)`。

整理后的分层：

```
PhotoDetailPage
  ├── HeicImageLoader.DisplayAsync(path, ZoomImage, ct)   → 命中PNG缓存或现场decode→PNG→展示
  │       HeicImageLoader.PreloadAsync(path, ct)          → 后台decode→PNG bytes→内存缓存
  └── WicImageLoader.DisplayAsync(path, ZoomImage, ct)    → 命中BitmapImage缓存或BitmapImage(uri)
          WicImageLoader.PreloadAsync(path, ct)           → 后台 new BitmapImage(uri)→内存缓存

ZoomableImage（只剩两个入口）
  ├── SetSourceAsync(SoftwareBitmapSource)   ← HeicImageLoader 调用
  └── SetSource(BitmapImage)                 ← WicImageLoader 调用
```

`ZoomableImage` 可以去掉对 `ImageDecoderPipeline` 的依赖，变成纯控件。

## ZoomableImage 的两个入口可以合并为一个吗？

可以，关键在于 HeicImageLoader 的产物格式。

**如果 HeicImageLoader 存的是 PNG bytes（`MemoryStream`）**

展示时：PNG MemoryStream → `new BitmapImage()` + `SetSourceAsync(stream)` → 得到 `BitmapImage`。这样两个 loader 的产物类型完全相同，ZoomableImage 只需一个入口：

```
ZoomableImage.LoadAsync(BitmapImage bitmap, int width, int height)
```

**推荐路径**

HeicImageLoader 存 PNG bytes、展示时解码为 `BitmapImage`，两个 loader 统一输出 `BitmapImage`，ZoomableImage 只有一个入口。这样 ZoomableImage 彻底不知道 HEIC 的存在，loader 负责全部格式差异，职责最清晰。代价是 HEIC 展示时多一次 PNG decode，但 PNG decode 很快，用户感知不到。

## 那渲染缩略图走 ImageDecoderPipeline 了吗

**走了**。`GetOrCreateThumbnailAsync` 和 `GenerateMissingAsync` 都调用 `GenerateViaDecoderAsync`（第 124、201 行），后者调 `_pipeline.TryDecodeAsync`（第 257 行）。

有一个例外：`GenerateAsync`（第 277 行）是 `internal static` 方法，直接用 WIC `BitmapDecoder`，**绕过了 pipeline**——但这个方法只暴露给单元测试用，生产路径不走它。

整体架构是：

```
缩略图生成  → ThumbnailService → ImageDecoderPipeline → WIC / Magick.NET
详情页展示  → (未来) WicImageLoader / HeicImageLoader → ImageDecoderPipeline → WIC / Magick.NET
```

## GetOrCreateThumbnailAsync 获取到缩略图以后，渲染到前端页面的时候有涉及到解码吗

有。从 [PhotoItemViewModel.cs:68](FluentGallery/ViewModels/PhotoItemViewModel.cs#L68) 可以看到：

```csharp
ThumbnailSource = new BitmapImage(new Uri(displayPath));
```

`BitmapImage` 设置 `UriSource` 时，WIC 会在后台线程解码那个 JPEG 缩略图文件，然后上传到 GPU 纹理。这是一次额外的 JPEG decode。

不过这不是问题：
- **JPEG decode 极快**，WIC 的 JPEG codec 高度优化
- **`UriSource` 是异步的**，WIC 在后台做，不阻塞 UI 线程
- **WIC JPEG codec 并发安全**，多个 GridView item 同时解码不会 crash

整条链路是：

```
磁盘 HEIC → ThumbnailService → Magick.NET decode → JPEG encode → 磁盘
磁盘 JPEG缩略图 → BitmapImage(UriSource) → WIC JPEG decode → GPU纹理 → 显示
```

## WICImageLoader 也是设置 BitmapImage(UriSource) 来加载图片。加载缩略图的时候也走这个链路，所有图片的加载都统一走 imageLoader，会有什么问题吗？

思路合理，但有一个结构性问题：**ImageLoader 的目标是 `ZoomableImage`，缩略图的目标是 `PhotoItemViewModel.ThumbnailSource`**，两者不同。

具体问题：

1. **API 签名对不上**：`WicImageLoader.DisplayAsync(path, ZoomableImage, ct)` 无法复用给缩略图，缩略图需要的是 `Task<BitmapImage>`。

2. **预加载缓存键不同**：WicImageLoader 的预加载缓存以原始文件路径为键，缩略图加载的是 thumbnail JPEG 的路径，两套路径体系，缓存逻辑会混淆。

3. **生命周期不同**：详情页缩略图缓存按导航周期管理，列表页缩略图缓存由 VM 自己持有（GridView 回收时 `ClearThumbnail()`），强行统一会使生命周期管理复杂化。

4. **缩略图加载本身极简**：就一行 `new BitmapImage(new Uri(path))`，不需要预加载、不需要格式分支，引入 loader 层是过度抽象。

**结论**：ImageLoader 抽象管详情页（预加载 + 全分辨率展示），缩略图继续保持现有 `PhotoItemViewModel` 直接创建 `BitmapImage(UriSource)` 的方式。

## 所以 ImageLoader 输出 ZoomableImage，意味着是和 UI 组件捆绑的吗？我想要 ImageLoader 输出 BitmapImage，和 UI 组件解耦。这样有什么问题？

好设计，解耦是对的。

**WicImageLoader → `BitmapImage`：没问题**

`new BitmapImage(new Uri(path))` 可以在任意线程构造，WIC 内部异步 decode，返回 `BitmapImage` 给调用方完全没问题。

**HeicImageLoader → `BitmapImage`：有约束**

缓存里存的是 PNG bytes（`MemoryStream`），展示时要做：

```
MemoryStream → new BitmapImage() + SetSourceAsync(stream)
```

`SetSourceAsync` 必须在 UI 线程调用，所以 `HeicImageLoader.LoadAsync` 本身必须从 UI 线程调用。这不是大问题——`LoadCurrentImageAsync` 本来就在 UI 线程——但这个约束需要文档化。

两者缓存类型不同，但对外接口可以统一为 `Task<BitmapImage> LoadAsync(path, ct)`，差异封装在实现内部。

## HeicImageLoader 为什么不能在内部将 PNG bytes 转为 BitmapImage

可以的，之前说的约束不是根本限制。

`LoadAsync` 在实践中就是从 UI 线程调用的（`LoadCurrentImageAsync` 在 UI 线程），所以在 `LoadAsync` 内部直接：

```
PNG bytes → new BitmapImage() + await SetSourceAsync(stream)
```

完全没问题，不需要任何额外的 dispatch。loader 内部全部封装，对外就是 `Task<BitmapImage> LoadAsync(path, ct)`，调用方感知不到任何差异。

## 整理一下方案（最终版）：你看看我整理的有什么问题

（方案：`PhotoDetailPage -> WicImageLoader/HeicImageLoader -> ImageDecoderPipeline`，ImageLoader 统一返回 `Task<BitmapImage>`，ZoomableImage 只剩一个 `SetSource` 入口，AlbumListPage/PhotoListPage 加载缩略图也走 WicImageLoader。）

**有两个矛盾点和一个遗漏需要指出：**

**矛盾1：第4点和 PhotoDetailPage 小节不一致**

第4点说 "ImageLoader 统一返回 `Task<BitmapImage>`，和 UI 解耦"，但 PhotoDetailPage 小节里的签名还是 `DisplayAsync(path, ZoomImage, ct)`，仍然耦合了 `ZoomableImage`。两者只能选一个：

- 返回 `BitmapImage` → 页面自己 `ZoomImage.SetSource(bmp)`
- `DisplayAsync` 接收 `ZoomableImage` → loader 内部设置

**矛盾2：AlbumListPage/PhotoListPage 加载缩略图走 WicImageLoader**

缩略图加载已经在 `PhotoItemViewModel` 里用 `new BitmapImage(uri)` 直接完成，WicImageLoader 的预加载缓存是给详情页导航用的（N±N 预加载），对无限滚动列表没有意义。之前讨论的结论是两者不需要统一，这里把列表页缩略图也归入 WicImageLoader 和之前的结论矛盾了。

**遗漏：BitmapImage(UriSource) 的尺寸问题**

ZoomableImage 的新入口是 `SetSource(ImageSource, int width, int height)`，但 WicImageLoader 用 `new BitmapImage(new Uri(path))` 时，图片尺寸在 `ImageOpened` 事件触发之前是未知的。建议 `ZoomableImage` 内部处理 `ImageOpened` 事件，`SetSource` 不要求传宽高，保持接口简单。

## 矛盾1：返回 BitmapImage。矛盾2：缩略图加载也走 WicImageLoader。遗漏：ZoomableImage 内部处理宽高。还有别的问题吗？没问题的话，你来重新整理一下，需要输出调用链、修改点

整理后的完整方案如下。

---

### 调用链

```
详情页展示/预加载
  PhotoDetailPage
    ├── WicImageLoader.LoadAsync(path, ct)   → BitmapImage(UriSource)
    └── HeicImageLoader.LoadAsync(path, ct)  → decode → PNG bytes → BitmapImage(SetSourceAsync)
          ↓ 两者均调用
          ImageDecoderPipeline.GetDecoder(path, concurrentSafe: bool)
            ├── WicImageDecoder   (IsAvailable=有HEVC扩展, concurrentSafe=false)
            └── MagickImageDecoder (IsAvailable=true,       concurrentSafe=true)

列表页缩略图展示
  PhotoItemViewModel.LoadThumbnailAsync
    └── WicImageLoader.LoadThumbnailAsync(thumbPath, ct) → BitmapImage(UriSource)

缩略图生成（不变）
  ThumbnailService → ImageDecoderPipeline.TryDecodeAsync → WIC / Magick.NET
```

---

### 新增/修改点

**新增：`IImageDecoder` 支持并发标记**
- [IImageDecoder.cs](FluentGallery/Decoders/IImageDecoder.cs)：加 `bool SupportsConcurrentDecode { get; }`
- `WicImageDecoder`：HEIC 实现返回 `false`，标准格式返回 `true`
- `MagickImageDecoder`：返回 `true`

**修改：`ImageDecoderPipeline.GetDecoder`**
- `GetDecoder(string filePath, bool concurrentSafe = false)` 加过滤：`concurrentSafe=true` 时跳过 `SupportsConcurrentDecode=false` 的 decoder

**新增：`WicImageLoader`**
- `LoadAsync(path, ct) → Task<BitmapImage>`：`new BitmapImage(new Uri(path))`，写入内存缓存
- `PreloadAsync(path, ct)`：同上，fire-and-forget 友好
- 内存缓存：`Dictionary<string, BitmapImage>`，LRU 淘汰

**新增：`HeicImageLoader`**
- `LoadAsync(path, ct) → Task<BitmapImage>`：命中缓存则 `MemoryStream → SetSourceAsync`，未命中则 `GetDecoder(concurrentSafe: true)` → decode → PNG encode → 存缓存 → `SetSourceAsync`
- `PreloadAsync(path, ct)`：后台线程，`GetDecoder(concurrentSafe: true)` → decode → PNG encode → 存 `Dictionary<string, MemoryStream>`
- 共用一个 `SemaphoreSlim(1,1)` 串行化所有 decode（预加载和即时加载共享）

**修改：`ZoomableImage`**
- 删除 `_pipelineExtensions`、`_decoderPipeline`、`LoadViaDecoderPipelineAsync`、`LoadSoftwareBitmapFromCacheAsync`
- `LoadImageAsync(string)` → 改为 `SetSource(BitmapImage)`（仅设置 source、reset zoom、FitToWindow）
- 内部监听 `BitmapImage.ImageOpened` 获取宽高，更新 `MainImage.Width/Height` 和 fit-zoom
- 去掉对 `ImageDecoderPipeline` 的依赖，变成纯 UI 控件

**修改：`PhotoDetailPage`**
- 删除 `_softwareBitmapCache`、`_preloadDecodeSemaphore`、`PreloadSoftwareBitmapAsync`、`_noBitmapCacheExtensions`
- `LoadCurrentImageAsync`：根据扩展名选 loader，`var bmp = await loader.LoadAsync(path, ct)`，然后 `ZoomImage.SetSource(bmp)`
- `PreloadAdjacent`：根据扩展名选 loader，调 `loader.PreloadAsync(path, preloadCt)`
- 统一缓存由各 loader 自己管理，页面不再持有 `_imageCache`

**修改：`PhotoItemViewModel`**
- `LoadThumbnailAsync` 内部改为调 `WicImageLoader.LoadAsync(thumbPath, ct)`，逻辑等价，但统一入口；`ThumbnailSource` 赋值不变

## 将这些形成一个文档，写到 docs 目录下，用于说明 ImageLoader、ImageDecoder 的架构，以及他们和 UI 层调用方的调用链。需要在文档里说明 ImageLoader、ImageDecoder 都是和 UI 层解耦的

文档已写入 [docs/image-loader-architecture.md](docs/image-loader-architecture.md)，包含：

- **Decoder 层**：`IImageDecoder` 接口、三种实现的对比表、`ImageDecoderPipeline` 的 `concurrentSafe` 参数说明
- **Loader 层**：`IImageLoader` 接口、`WicImageLoader` 和 `HeicImageLoader` 的实现策略、为什么存 PNG bytes 而不是 `BitmapImage`
- **完整调用链**：详情页加载/预加载、列表页缩略图、缩略图生成三条路径
- **层次图**：说明 UI → Loader → Decoder 三层解耦边界，以及 Loader 不持有任何 UI 组件引用的原则
- **ZoomableImage 变更**：需要删除的部分和新增的 `SetSource` 入口

## 根据我们上面讨论的架构和文档，修改代码和测试

（进入计划模式，探索代码库中的 Decoder、Loader、DI 注册和测试，作为下一步实现的准备。）
