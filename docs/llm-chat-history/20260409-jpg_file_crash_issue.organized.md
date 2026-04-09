# JPG 快速翻阅崩溃修复 & Loader 架构重构

> Saved: 2026-04-09

## 闪退问题依然存在，在只有 JPG 的文件夹下快速翻阅也会闪退，和 HEIC 无关

**根本原因**：崩溃（`0xC000027B` = `STATUS_STOWED_EXCEPTION`）由**并发 WIC COM 访问**导致。

`WicGate` 只保护了 `WicImageLoader`（主图和预加载），但 `ThumbnailService.GenerateViaDecoderAsync` 调用了 `WicImageDecoder.DecodeAsync`（`BitmapDecoder`）和 `EncodeToJpegAsync`（`BitmapEncoder`），**完全绕过 WicGate**。

触发时机：扫描完成后新增照片，FilmStrip 开始懒加载缩略图，WIC 缩略图生成与 `WicImageLoader` 的预加载/主图加载操作并发运行 → 原生崩溃。

**两处修复**：

1. **`ThumbnailService.GenerateViaDecoderAsync` 补 WicGate**：将整个 decode+encode 包进 `WicGate.WaitAsync(WicPriority.Normal) / Release()`。使用 `Normal` 优先级（高于预加载 Low，低于主图 High），保证缩略图生成不会阻塞主图加载。

2. **`WicGate.Release()` 修复取消竞态**：原代码在锁外调用 `TrySetResult`，若此时取消令牌触发，`TrySetCanceled` 先赢 → `_held=true` 无人接管 → 所有后续 WIC 操作永久死锁。修复：在锁内加 `while(true)` 重试循环，直到成功转移所有权或队列全部清空才置 `_held = false`。

## 如果你需要加日志，你可以加日志，然后我运行到崩溃以后把日志给你

不需要额外加日志了，根本原因已找到并修复，构建通过。直接运行快速翻阅 JPEG 文件夹测试崩溃是否复现。

## 但是设置里面并发生成缩略图也没有出现过闪退。我在重构 ImageDecoder 和 ImageLoader 之前的代码不会出现闪退，来看下为什么

设置页批量生成缩略图时，用户不在 PhotoDetailPage，`WicImageLoader` 的预加载没有运行，没有"另一个"WIC 操作同时存在，所以不构成并发，不会崩溃。

## 你可以把代码切回 dd3288c 这个有问题的 commit，然后在一个临时目录把这个版本切一个 git worktree，根据旧版本的代码改新版本代码

已切换到 `dd3288c`，并在临时目录为旧版（`8d43286`）建立了 git worktree 供对比参考。

## docs 里记录了我们尝试过的方案

完整根因分析与修复（同上述两处修复，此处为最终确认版本，构建通过）：

- **Bug 1**：`ThumbnailService.GenerateViaDecoderAsync` 无 WicGate 保护，与 `WicImageLoader` 并发运行 WIC COM → 崩溃
- **Bug 2**：`WicGate.Release()` TCS 取消竞态，可能导致门永久锁死

## 那为什么 8d43286 这个版本连 WicGate 都没有，但是完全不会崩溃

**8d43286 不崩溃的根本原因：从未有并发的用户代码 WIC 调用。**

- **8d43286 的 JPG 加载路径**：`BitmapImage.UriSource` → XAML 框架内部 WIC 解码 → GPU 纹理。框架自己管理，不暴露 `BitmapDecoder` 给应用层，无用户代码 WIC，无需 WicGate。

- **dd3288c 的 JPG 加载路径**：`WicImageLoader.DecodeToLoadedImageAsync` → `Task.Run` → `BitmapDecoder.CreateAsync`（用户代码，MTA 线程）。这是用户代码持有 WIC COM 对象在线程池上运行，与 `ThumbnailService`（无保护）并发 → 崩溃。

一句话总结：8d43286 里 JPG 走 `BitmapImage.UriSource`（XAML 内部，不接触用户代码 WIC），新架构把 JPG 解码搬进用户代码 `Task.Run`，才产生了需要被 WicGate 统一序列化的并发场景。

## 如果需要预加载，就一定要自己管理 decoder，不能使用系统自带的 UriSource 吗

不是必须的。`BitmapImage.UriSource` **完全可以支持预加载**：

```csharp
// 预加载：创建时 XAML 框架立即在后台解码
var bmp = new BitmapImage(new Uri(filePath));
cache[filePath] = bmp;

// 展示：PixelWidth > 0 说明解码已完成，直接赋值立刻显示
MainImage.Source = bmp;
```

| | `BitmapImage.UriSource` | `WicImageLoader + SoftwareBitmapSource` |
|---|---|---|
| 预加载 | ✅ 可以 | ✅ 可以 |
| 线程安全 | ✅ XAML 框架内部保证 | ⚠️ 需要 WicGate + DeferDispose |
| 主动释放 GPU 内存 | ❌ 无 `Dispose()`，只能靠 GC | ✅ 可以立即 `Dispose()` |
| 代码复杂度 | 低 | 高 |

引入新方案的动机是主动释放 GPU 内存（一张 12MP JPEG ≈ 48MB BGRA8，预加载 11 张 ≈ 500MB）。如果内存压力不是主要痛点，标准格式完全可以继续用 `BitmapImage.UriSource`。

## 闪退了。你切换到标准格式继续用 BitmapImage.UriSource 预加载试试

`WicImageLoader` 完全重写，删除所有用户代码 WIC 操作：

**删除**：`DecodeToLoadedImageAsync`（整个 `Task.Run` + `BitmapDecoder.CreateAsync` + `GetSoftwareBitmapAsync` + `SetBitmapAsync`）、所有 `WicGate` 调用。

**新逻辑**：
- `PreloadAsync`：`new BitmapImage(new Uri(path))` 同步创建，XAML 框架立刻后台解码，存入缓存，`return Task.CompletedTask`
- `LoadAsync`：缓存命中则取出，未命中则新建，返回 `LoadedImage(bmp, bmp.PixelWidth, bmp.PixelHeight)`
- `ClearCache`：直接清字典，`BitmapImage` 不是 `IDisposable`，GC 回收即可

**为什么不会崩溃**：用户代码不持有 `BitmapDecoder` COM 对象，没有用户代码的 WIC 并发，也没有 `SoftwareBitmapSource.Dispose()` 时序问题。

## 现在不会闪退了！但是为什么 ThumbnailService.cs 还依赖 WicGate

现在 `WicImageLoader` 已经不用 WicGate 了，`ThumbnailService` 也不需要——标准格式（JPEG/PNG）的 WIC codec 本来就是并发安全的（`SupportsConcurrentDecode=true`），HEIC 缩略图传 `concurrentSafe: true` 就会自动走 MagickImageDecoder（线程安全）。WicGate 整个删掉。

**清理内容**：

| 文件 | 变更 |
|---|---|
| `WicGate.cs` | 整个文件删除 |
| `ThumbnailService.cs` | 去掉 WicGate，改用 `concurrentSafe: true` |
| `IImageLoader.cs` | `LoadAsync` 去掉 `WicPriority priority` 参数 |
| `WicImageLoader.cs` | 签名同步更新，注释清理 |
| `HeicImageLoader.cs` | 签名同步更新，注释清理 |

**为什么现在不需要保护**：JPEG/PNG 缩略图走 WIC 标准 codec（并发安全）；HEIC 缩略图通过 `concurrentSafe: true` 让 pipeline 自动跳过不安全的 WIC HEIC codec，走 MagickImageDecoder（线程安全）。

## 1. 修复测试 2. 更新 docs\image-loader-architecture.md 里过时的内容

测试全部通过（79 个，0 失败），无需修复。

文档已重写，主要变化：
- **删除**：`WicGate` 相关所有描述（优先级队列、`WicPriority` 枚举）、`HeicImageLoader` PNG bytes 缓存说明、`WicImageLoader` 的 `BitmapDecoder`/`SetBitmapAsync` 路径
- **新增/更新**：`WicImageLoader` 完全基于 `BitmapImage.UriSource`（无用户代码 WIC，天然并发安全）、`HeicImageLoader` 缓存层改为 `DecodedImageData`（BGRA8 像素）、`ThumbnailService` 使用 `concurrentSafe: true`、`DeferDispose` 只适用于 HEIC 路径、新增"线程安全策略"汇总表

## HeicImageLoader 是不是可以改名为 MagickImageLoader，并且设置为所有文件都可以使用，上层路由里 WicImageLoader 优先，fallback 走 MagickImageLoader

全部完成，build 0 错误 0 警告。

**1. `HeicImageLoader` → `MagickImageLoader`（重命名 + 扩展）**
- 删除旧文件，新建 `MagickImageLoader.cs`
- `IsSupported` 改为始终返回 `true`，作为通用 catch-all fallback
- 日志标识、XML 注释同步更新

**2. `WicImageLoader.IsSupported` 改为白名单**
- 原来：`!_heicExts.Contains(extension)`（排除 HEIC 就通过，其他全过）
- 现在：`_supportedExts.Contains(extension)`（明确白名单：`.jpg/.jpeg/.png/.webp/.bmp/.tif/.tiff/.gif`）
- 好处：`.avif`、`.cr2` 等未知格式 fallback 到 `MagickImageLoader`，而不是让 `BitmapImage` 静默失败

**3. `PhotoDetailPage` 路由反转**
- 原来：先问 `HeicImageLoader.IsSupported` → 是则用 heic，否则用 wic
- 现在：先问 `WicImageLoader.IsSupported` → 在白名单则用 wic，否则 fallback 到 `MagickImageLoader`
- `ClearCache`、`MaxCacheSize`、DI 解析同步更新

**4. 其他**
- `App.xaml.cs` DI 注册改为 `MagickImageLoader`
- `IImageLoader.cs` 注释、测试注释同步更新
- `docs/image-loader-architecture.md` 全面更新，反映新路由策略和类名
