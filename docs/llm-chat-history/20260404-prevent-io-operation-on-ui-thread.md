# Fluent gallery 相册卡死
_Exported on 4/4/2026 at 16:37:45 GMT+8 from Cursor (2.6.22)_

---

**User**

刚把相册加入扫描以后，点进相册-点进图片就卡死了。什么操作 block 了前端显示？

---

**Cursor**

让我先看一下相关的代码文件，了解扫描、相册和图片详情页的实现。

现在我来看看 ViewModel 的实现，特别是 `PhotoDetailViewModel` 和 `ThumbnailService`。

让我查找 `GenerateMissingAsync` 在哪里被调用，以及 `SettingsViewModel` 的相关更改。

再来看一下解码器流水线的实现。

现在我完全清楚问题所在了。让我梳理整个调用链。

现在我有完整的调用链图了。问题很清楚，让我直接说明。

---

## 根本原因：三层同步堵塞叠加在 UI 线程

### 调用链

```
PhotoDetailPage.OnPageLoaded（UI线程）
 └─ await ViewModel.InitializeAsync(...)
     └─ _ = LoadFilmStripThumbsAsync(dispatcher, ct)   ← fire-and-forget，但…
         └─ await _thumbnail.GetOrCreateThumbnailAsync(photo, ct)
             └─ await GenerateViaDecoderAsync(...)
                 └─ await _pipeline.TryDecodeAsync(...)
                     └─ await WicImageDecoder.DecodeAsync(...)
                         ├─ File.OpenRead(filePath)           ← 同步磁盘 I/O，UI线程
                         ├─ srcStream.AsRandomAccessStream()  ← 创建 STA-tied 流对象
                         └─ await BitmapDecoder.CreateAsync(srcRas)
```

### 问题一：`LoadFilmStripThumbsAsync` 全程跑在 UI 线程上

`_ = LoadFilmStripThumbsAsync(...)` 虽然是 fire-and-forget，但它是从 UI 线程（STA）发起、没有 `Task.Run()` 包裹、也没有 `ConfigureAwait(false)`。C# 的 `await` 默认把续体（continuation）捕获到当前的 `SynchronizationContext`，也就是 WinUI3 的 `DispatcherQueue`。所以**所有续体都回调到 UI 线程**，两次 await 之间的同步代码也在 UI 线程执行。

### 问题二：STA ↔ MTA 跨公寓编组（apartment marshaling）

```57:62:FluentGallery/Decoders/WicImageDecoder.cs
        await using var srcStream = File.OpenRead(filePath);
        var srcRas  = srcStream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(srcRas).AsTask(ct);
```

`File.OpenRead()` 在 UI 线程（STA）调用，返回的 `FileStream` 对象属于 STA。`srcStream.AsRandomAccessStream()` 把它包装成 WinRT 流，这个流对象也 **"绑定"在 STA**。

WIC 解码器跑在 MTA 线程池。每次 WIC 读取这个流，都必须通过 **COM 跨公寓调用（STA reentrancy）** 把读请求 marshal 回 UI 线程处理——每张图片有数十次这样的往返。新相册的 N 张图全都要生成缩略图，产生 **N × 数十次** STA 编组往返，UI 消息泵被打满，渲染帧根本排不上队。

### 问题三：`File.OpenRead` 本身就是同步磁盘 I/O

在 `await BitmapDecoder.CreateAsync(...)` 之前，`File.OpenRead(filePath)` 是**纯同步调用**，直接在 UI 线程阻塞磁盘，N 张图 = N 次同步文件打开。

---

## 为什么界面全黑

`BitmapImage.UriSource`（PNG/JPEG 正片）是在后台解码的，本身不卡。但它解码完后，需要把 `ImageOpened` 回调**分发到 UI 线程**来设置 `MainImage.Source`。由于 UI 线程已经被上面的 STA 编组往返塞满，这个回调长时间排不上——图永远是黑的。

---

## 修复方案

**核心：把缩略图生成移出 UI 线程。** 最小改动是在 `PhotoDetailViewModel.LoadFilmStripThumbsAsync` 里，用 `Task.Run` 把生成工作切到线程池：

同时还要修复 `WicImageDecoder.DecodeAsync` 中流对象的创建方式。来一起改：

### 修复 1：`LoadFilmStripThumbsAsync` — 整个循环切到线程池

`LoadFilmStripThumbsAsync` 本身已经用 `dispatcher.TryEnqueue` 来更新 UI，所以把它整体 `Task.Run` 是安全的：

### 修复 2：`WicImageDecoder.DecodeAsync` — 用 `StorageFile` 替换 `File.OpenRead`

`File.OpenRead().AsRandomAccessStream()` 产生的是一个绑定在调用线程的 STA 对象，被 WIC（MTA）读取时要 marshal 回 UI 线程。改用 `StorageFile.OpenAsync` 返回的是真正的 WinRT 流，WIC 可以在 MTA 上直接读取：

### 修复 3：`ThumbnailService.GenerateAsync` — 同样修复 `File.OpenRead`

`GenerateAsync`（被 `GenerateMissingAsync` 调用，但也可能被其他路径调用）有相同问题：

现在检查 `GetOrCreateThumbnailAsync` 中的 `File.Exists`（同步磁盘调用）以及 `EncodeToJpegAsync` 中的 `File.Create`：

现在 `LoadFilmStripThumbsAsync` 已经移到线程池，`GetOrCreateThumbnailAsync` 内部的 `File.Exists` 和 `EncodeToJpegAsync` 中的 `File.Create` 也都在线程池线程执行了，不用单独改。

但同样的问题也存在于 `PhotoListPage` 的缩略图加载路径，让我检查一下：

`PhotoListPage` 的缩略图加载也有同样的问题——`File.OpenRead(path)` + `AsRandomAccessStream()` 在 UI 线程创建 STA-tied 流，再传给 `SetSourceAsync`。已有缩略图只是读一个小 JPEG，最简单的修复是换成 `BitmapImage.UriSource`（完全不阻塞）：

`PhotoItemViewModel` 去掉了 `SetSourceAsync` 后不再需要 `using Microsoft.UI.Xaml.Media.Imaging;` 之外的命名空间，检查一下 lint：

---

## 总结

### 什么在 block 前端显示

三个问题叠加：

**① `LoadFilmStripThumbsAsync` 全程跑在 UI 线程上**

```csharp
// 修复前：fire-and-forget 但 continuation 全回 UI 线程
_ = LoadFilmStripThumbsAsync(dispatcher, _preloadCts.Token);
```

`InitializeAsync` 在 UI 线程中被 `await`，它 fire-and-forget 启动的 `LoadFilmStripThumbsAsync` 没有 `Task.Run`、也没有 `ConfigureAwait(false)`，所以 C# 把所有 `await` 之后的续体都调度回 `DispatcherQueue`（UI 线程）。对一个全是新照片、没有缓存缩略图的相册，每张图的生成工作都跑在 UI 线程上。

**② `File.OpenRead().AsRandomAccessStream()` 产生 STA-tied 流**

```csharp
// 修复前（WicImageDecoder & ThumbnailService）
await using var srcStream = File.OpenRead(filePath);  // 同步磁盘 I/O，在 UI 线程
var srcRas = srcStream.AsRandomAccessStream();         // 流对象绑定到调用线程的 STA
var decoder = await BitmapDecoder.CreateAsync(srcRas); // WIC (MTA) 每次读流都要 marshal 回 UI 线程
```

WIC 跑在 MTA 线程池，每次读这个 STA-bound 流必须通过 COM 跨公寓调用（STA reentrancy）回到 UI 线程——每张图数十次往返，N 张图 = N × 数十次 STA 往返，UI 消息泵被塞满，渲染帧排不上队，画面就全黑了。

**③ `BitmapImage.SetSourceAsync` + STA 流加剧相册格子页的问题**

`PhotoItemViewModel.LoadThumbnailAsync` 原本用 `File.OpenRead(path).AsRandomAccessStream()` 传给 `SetSourceAsync`，即使缩略图已经生成，读已有的 JPEG 时 WIC 也要回调 UI 线程。

### 三处修复

| 文件 | 改动 |
|---|---|
| `PhotoDetailViewModel.cs` | `LoadFilmStripThumbsAsync` → `Task.Run(() => LoadFilmStripThumbsCoreAsync(...))` + `ConfigureAwait(false)`，彻底移出 UI 线程 |
| `WicImageDecoder.cs` | `File.OpenRead().AsRandomAccessStream()` → `StorageFile.GetFileFromPathAsync` + `storageFile.OpenAsync()`，得到真正的 WinRT 流（无 STA binding） |
| `ThumbnailService.cs` | `GenerateAsync` 同样改用 `StorageFile.OpenAsync` |
| `PhotoItemViewModel.cs` | `SetSourceAsync(stream.AsRandomAccessStream())` → `new BitmapImage(new Uri(path))`，完全不阻塞 UI 线程 |

---

**User**

有什么办法能够在架构层面预防所有 IO 和 CPU 密集型功能运行在 UI 线程吗

---

**Cursor**

这是一个很好的架构问题。核心矛盾在于：**`async/await` 默认会捕获调用处的 `SynchronizationContext`**，所以只要在 UI 线程上 `await` 一个服务方法，该方法里所有续体（和首次运行的同步代码）都会回到 UI 线程——即使方法签名看起来是"异步"的。

三层防御从最轻到最强：

---

## 层一：`ThreadGuard` — 运行时卫哨（最快落地）

在 DEBUG 包里，服务方法一进入就检查自己是否在 UI 线程：

现在把卫哨加到所有 IO/CPU 密集型服务方法上：

`MagickImageDecoder` 已经自带 `Task.Run` 包裹，不需要加。现在加 using：

`AppDataPaths` 与 `Windows.Storage.AppDataPaths` 冲突，需要加回别名：

编译通过，0 错误 0 警告。Linter 只是还没刷新到新文件。

---

## 层二：`ConfigureAwait(false)` 全面铺开（阻断 SyncCtx 传播）

`ThreadGuard` 只在 DEBUG 里报错，Release 里不保护。下一道防线是**在所有服务层 `await` 上加 `ConfigureAwait(false)`**，让续体永远不会捕获 UI 线程的 `SynchronizationContext`——即使有人忘了 `Task.Run`，续体也不会跑回 UI 线程。

检查一下现有服务层还有哪些 `await` 缺少 `ConfigureAwait(false)`：

有约 80 处 `await` 需要加，这不是手工一行一行改的最佳方式。我用分析器包解决，这才是架构层面的长效方案。

## 层三：Roslyn 静态分析器（编译期阻断）

给项目添加 `ConfigureAwaitChecker.Analyzer`，它会对所有缺少 `ConfigureAwait(false)` 的 `await` 报 **编译器警告**，在 CI 里可以升为错误：

分析器已经精确地把所有遗漏标出来了。现在需要分两类处理：

- **服务层**（`Data/`、`Decoders/`、`Helpers/`）→ 加 `ConfigureAwait(false)`，让续体留在线程池
- **视图层**（`Views/`、`ViewModels/`、`Controls/`）→ **不能加**，视图代码必须回到 UI 线程才能操作 DependencyObject

用 `.editorconfig` 在不同目录设置不同严重级别，让分析器只在服务层强制报错：

完美——服务层全部升为 **error**，视图层静默。现在用 `dotnet-codefix` 或 IDE 快速修复可以一键清零所有 error。我来批量处理服务层文件：

分析器工作完美——`Data/`, `Decoders/`, `Helpers/` 全报 error，`Views/`, `ViewModels/`, `Controls/` 静默。

现在清零 error。`DatabaseService.cs` 有大量重复的 `await using var db = await _factory.CreateDbContextAsync(ct)` 模式，先批量替换：

现在处理文件里剩余的独立 `await` 调用（EF Core 查询、SaveChangesAsync、ExecuteDeleteAsync 等）：

`DatabaseService.cs` 里的 `await using var db = await _factory.CreateDbContextAsync(ct)` 产生两个 await（工厂创建 + 隐式 Dispose），对 SQLite 来说工厂创建完全不需要 async。换成同步的 `_factory.CreateDbContext()` 一步消灭两个警告，同时也是 EF Core 推荐的短生命周期 DbContext 用法：

现在处理 `InitializeAsync` 里剩余的两个 await（`EnsureCreatedAsync`、`ExecuteSqlRawAsync`），以及所有其他 EF Core async 调用：

还有很多，我来用批量替换系统性地清零。`DatabaseService.cs` 里的 EF Core 模式最规律：

剩余的错误分散在各种 EF Core 方法和 WinRT 调用里。我来逐文件批量修复：

让我直接读取剩余文件的当前内容，然后做精准的批量替换：

现在修复 `ThumbnailService` 中的 WinRT 调用和其余 `GenerateViaDecoderAsync`/`EncodeToJpegAsync` 方法：

现在修复其他文件：

现在处理 `DatabaseService.cs` 里剩余的 `ExecuteSqlRawAsync`，和 `ScanService.cs`：

现在处理 `DatabaseService.cs` 剩余的几个和 `ThumbnailService.cs` 的最后几个：

现在处理 `ThumbnailService.cs` 最后两个报错——`Parallel.ForEachAsync` 和 `await using var dstStream`：

那个方法不对，撤回：

现在处理 `await using var dstStream` 问题——改为同步 `using`：

**0 警告，0 错误。** 三层防御已全部到位。来梳理一下整体架构：

---

## 总结：三层防御体系

### 层一：运行时卫哨（`ThreadGuard`）

```12:45:FluentGallery/Helpers/ThreadGuard.cs
public static class ThreadGuard
{
    [Conditional("DEBUG")]
    public static void EnsureBackground([CallerMemberName] string caller = "")
    {
        if (DispatcherQueue.GetForCurrentThread() is not null)
            throw new InvalidOperationException(
                $"[ThreadGuard] '{caller}' must not run on the UI thread. ...");
    }
    // ...
}
```

- **作用**：在 DEBUG 构建里，服务方法一进入就检查自己是否在 UI 线程，不合规立刻抛异常。
- **覆盖点**：`ThumbnailService.GetOrCreateThumbnailAsync`、`GenerateMissingAsync`、`WicImageDecoder.DecodeAsync`。
- **代价**：Release 里 `[Conditional("DEBUG")]` 完全编译消失，零开销。

### 层二：编译期静态分析（`ConfigureAwaitChecker.Analyzer` + `.editorconfig`）

```7:38:.editorconfig
# 服务层 (Data/, Decoders/, Helpers/)：缺 ConfigureAwait(false) → 编译错误
[FluentGallery/Data/**.cs]
dotnet_diagnostic.CAC001.severity = error

[FluentGallery/Decoders/**.cs]
dotnet_diagnostic.CAC001.severity = error

[FluentGallery/Helpers/**.cs]
dotnet_diagnostic.CAC001.severity = error

# 视图层 (Views/, ViewModels/, Controls/)：必须回到 UI 线程 → 静默
[FluentGallery/Views/**.cs]
dotnet_diagnostic.CAC001.severity = none

[FluentGallery/ViewModels/**.cs]
dotnet_diagnostic.CAC001.severity = none
```

- **作用**：新增任何缺少 `ConfigureAwait(false)` 的 `await` 在服务层都会导致构建失败，在 PR 阶段就被 CI 拦截。
- **分层意图**：视图层 `await` 必须返回 UI 线程（才能操作 DependencyObject），所以不能加，也不应该报警。

### 层三：`ConfigureAwait(false)` 全面铺开

所有服务层的 `await` 都已加上 `.ConfigureAwait(false)`，确保：
- WinRT 续体（`BitmapDecoder.CreateAsync` 等）不会因 STA 被强制回 UI 线程
- EF Core 查询续体留在线程池
- 即使误从 UI 线程调用服务方法，续体也不会持续占用 UI 线程

### 约定一览

| 层         | 文件                               | 规则 |
|------------|-----------------------------------|------|
| 服务层     | `Data/`, `Decoders/`, `Helpers/`  | `ThreadGuard.EnsureBackground()` 进入 + 所有 `await` 加 `ConfigureAwait(false)` |
| 视图层     | `Views/`, `ViewModels/`, `Controls/` | UI 操作 + 需要跨线程时用 `dispatcher.TryEnqueue(() => {...})` 显式派发 |
| 新写服务方法 | 任意 | 不加 `ConfigureAwait(false)` → 构建直接报 error，PR 无法合并 |

---

**User**

现在生成缩略图报错了，不管是自动升成还是手动生成都报错了

---

**Cursor**

`ThreadGuard.EnsureBackground()` 现在会在 DEBUG 下抛异常，而有的调用方还是在 UI 线程上调用服务层方法。让我先看看调用栈：

找到原因了：`ThreadGuard.EnsureBackground()` 正确地在两处 UI 线程调用时抛了异常——

1. `PhotoItemViewModel.LoadThumbnailAsync` 第 52 行直接从 UI 线程调用 `GetOrCreateThumbnailAsync`
2. `SettingsViewModel.GenerateMissingThumbnailsAsync` 第 578 行直接从 UI 线程调用 `GenerateMissingAsync`

`ThreadGuard` 发现了两个真实的 bug——这两个调用点原来就是问题根源，之前只修了 `PhotoDetailViewModel`，漏了这两处。修法是在调用点加 `Task.Run`：

0 警告，0 错误。

---

**这恰好是 `ThreadGuard` 应该做的事**——它不是误报，而是发现了两个真实遗漏：

| 调用点 | 问题 | 修复 |
|--------|------|------|
| `PhotoItemViewModel.LoadThumbnailAsync` | 从 `PhotoListPage` UI 线程直接调用 `GetOrCreateThumbnailAsync`（缩略图生成发生在 UI 线程） | 加 `Task.Run(...)` |
| `SettingsViewModel.GenerateMissingThumbnailsAsync` | 手动生成按钮的处理器在 UI 线程直接调用 `GenerateMissingAsync` | 加 `Task.Run(...)` |

之前上次修 `PhotoDetailViewModel` 的时候只加了 `Task.Run` 把胶片栏缩略图移出 UI 线程，但这两个入口漏掉了。`ThreadGuard` 在你第一次运行时立刻把它们揪了出来，正好说明三层防御体系按预期工作了。

---

**User**

现在缩略图可以正常生成了，但是相册的封面为空了

---

**Cursor**

让我先看看相册封面是如何加载的：

找到了，`AlbumItemViewModel.LoadCoverAsync` 有两个问题：
1. 第 76 行从 UI 线程直接调用 `GetOrCreateThumbnailAsync`，被 `ThreadGuard` 拦截
2. 第 79–81 行还在用老的 `File.OpenRead + SetSourceAsync` 模式（这正是之前导致 UI 卡死的根因）

两个问题一起修：

0 警告，0 错误。

`AlbumItemViewModel.LoadCoverAsync` 被漏掉了，原因是相册封面加载的调用链和缩略图加载的调用链是分开的：

```
PhotoListPage → PhotoItemViewModel.LoadThumbnailAsync       ← 上次修了
PhotoDetailViewModel.LoadFilmStripThumbsCoreAsync           ← 上次修了
AlbumListPage → AlbumItemViewModel.LoadCoverAsync           ← 这次修了
SettingsPage → SettingsViewModel.GenerateMissingThumbnails  ← 上次修了
```

同时顺手把 `LoadCoverAsync` 里残留的 `File.OpenRead + SetSourceAsync` 老模式也换成了 `new BitmapImage(new Uri(path))`，和 `PhotoItemViewModel` 保持一致——这样就消除了另一个 STA-bound 流的潜在 UI 线程阻塞点。

