# Image Loader & Decoder 架构

## 概述

图片加载分为两层：

- **Decoder 层**（`FluentGallery/Decoders/`）：将磁盘文件解码为原始像素数据（`DecodedImageData`）。与 UI 完全无关。
- **Loader 层**（`FluentGallery/Loaders/`）：将文件路径转换为 `LoadedImage`（含 `SoftwareBitmapSource` + 尺寸），供 UI 层使用。内部调用 Decoder 层，并管理内存缓存和预加载。

两层均**不依赖任何 UI 组件**（`ZoomableImage`、`Page`、`ViewModel` 等）。UI 层调用 Loader 取得 `LoadedImage` 后，自行决定如何渲染。

---

## Decoder 层

### 接口

```csharp
public interface IImageDecoder
{
    IReadOnlyList<string> SupportedExtensions { get; }
    bool IsAvailable { get; }
    bool SupportsConcurrentDecode { get; }   // 是否支持多线程并发调用
    Task<DecodedImageData> DecodeAsync(string filePath, uint maxWidth, uint maxHeight, CancellationToken ct);
}
```

输出 `DecodedImageData`：BGRA8 原始像素 + 宽高 + DPI，EXIF 旋转已处理。

### 实现

| 类 | 格式 | IsAvailable | SupportsConcurrentDecode |
|---|---|---|---|
| `WicImageDecoder`（标准格式） | JPG/PNG/GIF/WebP/BMP/TIFF | 始终 true | true |
| `WicImageDecoder`（HEIC） | .heic/.heif | 需安装 HEVC Video Extensions | **false**（WIC HEIC codec 非并发安全） |
| `MagickImageDecoder` | .heic/.heif | 始终 true（内置 libheif） | true |

### ImageDecoderPipeline

统一管理 decoder 注册和选择：

```csharp
// 注册顺序即优先级（先注册先使用）
pipeline.Register(WicImageDecoder.CreateForStandardFormats());
pipeline.Register(WicImageDecoder.CreateForHeic());   // 优先用系统 WIC
pipeline.Register(new MagickImageDecoder());           // 无 WIC 时 fallback

// 普通查询：取第一个 IsAvailable 的 decoder
IImageDecoder? GetDecoder(string filePath);

// 并发安全查询：跳过 SupportsConcurrentDecode=false 的 decoder
IImageDecoder? GetDecoder(string filePath, bool concurrentSafe);
```

`TryDecodeAsync` 按优先级依次尝试，某个 decoder 抛出异常时自动 fallback 到下一个。

---

## Loader 层

### 接口

```csharp
public interface IImageLoader
{
    bool IsSupported(string extension);

    // 后台预加载，结果写入 loader 内部缓存，调用方 fire-and-forget
    Task PreloadAsync(string filePath, CancellationToken ct);

    // 加载并返回 LoadedImage，命中缓存则直接返回，否则现场解码
    // 必须在 UI 线程调用（SoftwareBitmapSource.SetBitmapAsync 要求）
    // priority: High=当前图, Normal=缩略图, Low=预加载（影响 WicGate 排队顺序）
    Task<LoadedImage?> LoadAsync(string filePath, CancellationToken ct,
        WicPriority priority = WicPriority.High);

    void ClearCache();
    int MaxCacheSize { get; set; }
}

public sealed class LoadedImage(ImageSource source, int pixelWidth, int pixelHeight)
{
    public ImageSource Source      { get; }  // SoftwareBitmapSource 或 BitmapImage (GIF)
    public int         PixelWidth  { get; }
    public int         PixelHeight { get; }
}
```

`LoadedImage.Source` 的所有权归调用方：是 `SoftwareBitmapSource` 时，不再使用后必须调用 `Dispose()` 释放 GPU 纹理（通过 `DeferDispose` 延迟到下两个消息循环迭代，防止 compositor 仍在引用）。

### WicGate — 优先级序列化门

`WicGate`（`FluentGallery/Loaders/WicGate.cs`）是全局互斥锁，序列化所有 WIC COM 操作（`BitmapDecoder`、`BitmapEncoder`）。WIC 对象在 MTA 线程池并发访问时会发生 `STATUS_STOWED_EXCEPTION (0xC000027B)` 崩溃，WicGate 是防止此类崩溃的核心机制。

```csharp
WicPriority.High   = 2   // 当前查看图片（LoadAsync 直接加载）
WicPriority.Normal = 1   // 缩略图加载
WicPriority.Low    = 0   // 预加载相邻图片（PreloadAsync）
```

实现：每个优先级一个 FIFO 队列；`Release()` 时从高到低找第一个未取消的等待者唤醒。取消的等待者通过 `TrySetCanceled` 标记为 completed，`Release()` 跳过已 completed 的 TCS。

### WicImageLoader

适用于 WIC 原生支持的格式（JPG/PNG/WebP/BMP/TIFF）及缩略图加载。

- **PreloadAsync**：在 `Task.Run`（MTA）内通过 `WicGate.WaitAsync(Low)` 获取 gate，用 `BitmapDecoder` + `GetSoftwareBitmapAsync` 解码，再 `SetBitmapAsync`（UI 线程）上传 GPU，结果存入 `_preloadCache`。
- **LoadAsync**：命中 `_preloadCache` → 转移所有权并返回；未命中 → `DecodeToLoadedImageAsync(priority)`。
- **GIF**：返回 `BitmapImage(new Uri(path))`（唯一支持动画的方式），不经过 WicGate。
- **缓存**：`Dictionary<string, LoadedImage>`，按 `MaxCacheSize` LRU 淘汰，淘汰时 DeferDispose。

### HeicImageLoader

适用于 HEIC/HEIF 格式。核心目标：规避 WIC HEIC codec 的并发崩溃，同时利用 PNG bytes 缓存减少重复解码。

**缓存层**：`Dictionary<string, byte[]>` 存储 PNG bytes（~7 MB/张，远低于 BGRA8 的 ~48 MB），`_cacheLock` 保护所有读写（从线程池访问）。

- **PreloadAsync**（线程池，`concurrentSafe: true`）：
  1. Magick.NET 解码 HEIC → `DecodedImageData`（并发安全，无需 WicGate）
  2. `WicGate.WaitAsync(Low)` → `BitmapEncoder` PNG encode → 存入 `_pngCache`

- **LoadAsync**（必须从 UI 线程调用，因 `SetBitmapAsync` 要求）：
  1. `Task.Run` 内：检查 `_pngCache`；未命中则 Magick 解码 + `WicGate.WaitAsync(priority)` encode
  2. `Task.Run` 内：`WicGate.WaitAsync(priority)` → `BitmapDecoder` 解码 PNG bytes → `SoftwareBitmap`
  3. 回 UI 线程：`SetBitmapAsync(softwareBitmap)` 上传 GPU，`softwareBitmap.Dispose()`

> **为什么存 PNG bytes 而不是 `SoftwareBitmapSource`？**
> PNG bytes 可以在后台线程生成并被多个消费者独立解码；`SoftwareBitmapSource` 的 GPU 纹理生命周期需要与 UI 生命周期绑定，不适合作为缓存对象。

---

## WicGate 优先级排队示意

```
时刻 T0: 预加载任务 A (Low) 进入，gate 空闲，立即获取
时刻 T1: 用户切换图片，当前图任务 B (High) 到达，gate 被 A 持有，B 进入 High 队列
时刻 T2: 缩略图任务 C (Normal) 到达，gate 被 A 持有，C 进入 Normal 队列
时刻 T3: 预加载任务 D (Low) 到达，进入 Low 队列
时刻 T4: A 完成，Release()
  → 优先唤醒 High 队列 → B 获取 gate
时刻 T5: B 完成，Release()
  → High 队列为空，唤醒 Normal 队列 → C 获取 gate
时刻 T6: C 完成，Release()
  → 唤醒 Low 队列 → D 获取 gate
```

---

## 调用链

### 详情页（PhotoDetailPage）

```
用户切换图片
└── PhotoDetailPage.LoadCurrentImageAsync()   [WicPriority.High (default)]
      ├── ext ∈ {.heic, .heif}
      │     └── HeicImageLoader.LoadAsync(path, ct, High)
      │           ├── 命中 _pngCache → Task.Run: WicGate(High) + BitmapDecoder → SoftwareBitmap
      │           │                    → UI: SetBitmapAsync → SoftwareBitmapSource
      │           └── 未命中 → Task.Run: Magick decode → WicGate(High) + PNG encode → cache
      │                        → Task.Run: WicGate(High) + BitmapDecoder → SoftwareBitmap
      │                        → UI: SetBitmapAsync → SoftwareBitmapSource
      └── 其他格式
            └── WicImageLoader.LoadAsync(path, ct, High)
                  ├── 命中 _preloadCache → 直接返回（所有权转移）
                  └── 未命中 → Task.Run: WicGate(High) + BitmapDecoder → SoftwareBitmap
                               → UI: SetBitmapAsync → SoftwareBitmapSource

后台预加载（1s debounce + diff-based，UpdatePreloadTasks）
└── HeicImageLoader.PreloadAsync(path, cts.Token)   [WicPriority.Low]
      → Task.Run: Magick decode → WicGate(Low) + BitmapEncoder PNG encode → _pngCache
└── WicImageLoader.PreloadAsync(path, cts.Token)    [WicPriority.Low]
      → Task.Run: WicGate(Low) + BitmapDecoder → SetBitmapAsync → _preloadCache
```

### 列表页缩略图（PhotoListPage）

```
GridView item 进入视口
└── PhotoItemViewModel.LoadThumbnailAsync()
      └── ThumbnailService.GetOrCreateThumbnailAsync()   ← 生成/取缓存缩略图路径（磁盘 JPEG）
      └── WicImageLoader.LoadAsync(thumbPath, ct, Normal)  ← WicPriority.Normal
            → Task.Run: WicGate(Normal) + BitmapDecoder → SoftwareBitmap
            → UI: SetBitmapAsync → SoftwareBitmapSource
      → PhotoItemViewModel.ThumbnailSource = source   ← 绑定到 XAML Image.Source
```

### 缩略图生成（ThumbnailService，不经过 Loader）

```
ThumbnailService.GetOrCreateThumbnailAsync()
└── ImageDecoderPipeline.TryDecodeAsync(path, thumbSize, thumbSize)
└── EncodeToJpegAsync() → 写入磁盘缓存
```

---

## DeferDispose — 防止 GPU 纹理 use-after-free

`SoftwareBitmapSource.Dispose()` 释放 GPU 纹理。若 compositor 仍在引用该纹理时调用 Dispose，会导致 `STATUS_STOWED_EXCEPTION (0xC000027B)` 崩溃（native AV，绕过所有 .NET 异常处理器）。

解决方案（`ZoomableImage.DeferDispose`）：

```csharp
DispatcherQueue.TryEnqueue(Low, () =>
    DispatcherQueue.TryEnqueue(Low, () =>
    {
        try { disposable.Dispose(); } catch { }
    }));
```

两次 Low-priority enqueue 保证至少经过两个 UI 消息循环迭代，compositor 有足够时间处理 `Source = null` 并停止引用 GPU surface，再释放。

所有 `SoftwareBitmapSource` 的释放路径均通过 DeferDispose：

- `ZoomableImage.SetLoading()` / `SetSource()`：释放 `_currentDisposable`
- `PhotoDetailPage.LoadCurrentImageAsync`：stale load result（gen 检查失败时）
- `WicImageLoader.ClearCache()` / `AddToPreloadCache()` 淘汰
- `PhotoItemViewModel.ClearThumbnail()`

---

## 层次与解耦关系

```text
┌─────────────────────────────────────────────────────────────┐
│                        UI 层                                 │
│   PhotoDetailPage  /  PhotoItemViewModel                     │
│   ZoomableImage（纯 UI 控件，接受 LoadedImage）              │
└───────────────┬─────────────────────────────────────────────┘
                │ Task<LoadedImage>（Loader 返回值）
                │ 无 UI 类型向下传递
┌───────────────▼─────────────────────────────────────────────┐
│                      Loader 层                               │
│   WicImageLoader  /  HeicImageLoader                         │
│   WicGate（优先级互斥锁，序列化所有 WIC COM 操作）           │
│   - 管理内存缓存（SoftwareBitmapSource / PNG bytes）         │
│   - 管理预加载（1s debounce, diff-based, per-path CTS）      │
│   - 不持有任何 UI 组件引用                                   │
└───────────────┬─────────────────────────────────────────────┘
                │ DecodedImageData（原始像素）
┌───────────────▼─────────────────────────────────────────────┐
│                     Decoder 层                               │
│   ImageDecoderPipeline                                       │
│   WicImageDecoder  /  MagickImageDecoder                     │
│   - 纯文件 → 像素的转换，无任何 UI 依赖                     │
└─────────────────────────────────────────────────────────────┘
```
