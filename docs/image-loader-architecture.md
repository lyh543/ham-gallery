# Image Loader & Decoder 架构

## 概述

图片加载分为两层：

- **Decoder 层**（`FluentGallery/Decoders/`）：将磁盘文件解码为原始像素数据（`DecodedImageData`）。与 UI 完全无关。
- **Loader 层**（`FluentGallery/Loaders/`）：将文件路径转换为 `LoadedImage`，供 UI 层使用。内部管理内存缓存和预加载。路由策略：`WicImageLoader` 处理 BitmapImage 能原生支持的格式；其他格式 fallback 到 `MagickImageLoader`，后者通过 Decoder 层解码为原始像素。

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
| `WicImageDecoder`（标准格式） | JPG/PNG/GIF/WebP/BMP/TIFF | 始终 true | **true**（标准格式 WIC codec 并发安全） |
| `WicImageDecoder`（HEIC） | .heic/.heif | 需安装 HEVC Video Extensions | **false**（WIC HEIC codec 非并发安全） |
| `MagickImageDecoder` | .heic/.heif | 始终 true（内置 libheif） | **true** |

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
IImageDecoder? GetDecoder(string filePath, concurrentSafe: true);
```

`TryDecodeAsync` 按优先级依次尝试，某个 decoder 抛出异常时自动 fallback 到下一个。
`concurrentSafe: true` 时，WIC HEIC codec 被跳过，HEIC 由 MagickImageDecoder 处理。

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
    // 必须在 UI 线程调用（MagickImageLoader 的 SetBitmapAsync 要求）
    Task<LoadedImage?> LoadAsync(string filePath, CancellationToken ct);

    void ClearCache();
    int MaxCacheSize { get; set; }
}

public sealed class LoadedImage(ImageSource source, int pixelWidth, int pixelHeight)
{
    public ImageSource Source      { get; }  // SoftwareBitmapSource (MagickImageLoader) 或 BitmapImage (WicImageLoader)
    public int         PixelWidth  { get; }  // > 0 表示已解码可立即显示；== 0 需等 ImageOpened
    public int         PixelHeight { get; }
}
```

`LoadedImage.Source` 的所有权归调用方：
- `SoftwareBitmapSource`（`MagickImageLoader`）：实现 `IDisposable`，不再使用后须通过 `DeferDispose` 延迟释放 GPU 纹理。
- `BitmapImage`（`WicImageLoader`）：不实现 `IDisposable`，由 XAML 框架和 GC 管理生命周期，无需手动释放。

### WicImageLoader

适用于 BitmapImage 能可靠处理的格式（JPG/JPEG/PNG/WebP/BMP/TIF/TIFF/GIF），通过 `_supportedExts` 白名单控制。

核心原则：**完全不使用用户代码 WIC（`BitmapDecoder`/`BitmapEncoder`）**，一切交由 XAML 框架内部的 `BitmapImage` 处理，天然并发安全，无需序列化门。

- **IsSupported**：返回 `_supportedExts.Contains(extension)`（明确白名单，非白名单格式 fallback 到 `MagickImageLoader`）。
- **PreloadAsync**（UI 线程，同步返回）：`new BitmapImage(new Uri(path))` 触发 XAML 框架后台解码，结果存入 `_preloadCache`。
- **LoadAsync**：命中 `_preloadCache` → 取出 `BitmapImage` 并返回（若 `PixelWidth > 0` 则已解码，立即显示）；未命中 → 新建 `BitmapImage(new Uri(path))` 返回（`PixelWidth == 0`，等 `ImageOpened`）。
- **缓存**：`Dictionary<string, BitmapImage>`，按 `MaxCacheSize` LRU 淘汰。无需 Dispose（`BitmapImage` 非 `IDisposable`）。

### MagickImageLoader

通用 fallback loader，适用于所有 `WicImageLoader` 不支持的格式（HEIC/HEIF 等）。通过 `ImageDecoderPipeline`（Magick.NET / libheif）解码，使用原始 BGRA8 像素缓存，完全绕过 WIC HEIC codec。

- **IsSupported**：始终返回 `true`，作为兜底 fallback。路由由上层（`PhotoDetailPage.GetLoader`）决定，先尝试 `WicImageLoader`，不支持再走本 loader。

**缓存层**：`Dictionary<string, DecodedImageData>` 存储 BGRA8 原始像素（~48 MB/张，12 MP），`_cacheLock` 保护所有读写。

- **PreloadAsync**（线程池，`concurrentSafe: true`）：
  1. pipeline 选择并发安全的 decoder（HEIC → MagickImageDecoder）解码 → `DecodedImageData`
  2. 存入 `_pixelCache`

- **LoadAsync**（必须从 UI 线程调用，因 `SetBitmapAsync` 要求）：
  1. `Task.Run` 内：从 `_pixelCache` 取像素（未命中则 pipeline 解码，`concurrentSafe: true`）
  2. `Task.Run` 内：`SoftwareBitmap.CreateCopyFromBuffer` + `Convert`（Premultiplied，无用户代码 WIC）
  3. 回 UI 线程：`SoftwareBitmapSource.SetBitmapAsync` 上传 GPU，`softwareBitmap.Dispose()`
  4. 返回 `LoadedImage(source, w, h)`，`PixelWidth` 始终 > 0（已完全解码）

> **为什么存 BGRA8 像素而不是 `SoftwareBitmapSource`？**
> `SoftwareBitmapSource` 封装 GPU 纹理，生命周期需绑定 UI；BGRA8 像素是纯内存数据，可在后台线程安全地读写和共享。

---

## 调用链

### 详情页（PhotoDetailPage）

路由策略（`GetLoader`）：`WicImageLoader.IsSupported(ext)` 为 true → `WicImageLoader`；否则 → `MagickImageLoader`。

```
用户切换图片
└── PhotoDetailPage.LoadCurrentImageAsync()
      ├── ext ∈ {.jpg/.jpeg/.png/.webp/.bmp/.tif/.tiff/.gif}（WicImageLoader 白名单）
      │     └── WicImageLoader.LoadAsync(path, ct)
      │           ├── 命中 _preloadCache → BitmapImage（PixelWidth > 0 立即显示，或 0 等 ImageOpened）
      │           └── 未命中 → new BitmapImage(new Uri(path))（PixelWidth == 0，等 ImageOpened）
      │           └── ZoomableImage.SetSource(loaded)
      └── 其他格式（HEIC/HEIF 等，MagickImageLoader fallback）
            └── MagickImageLoader.LoadAsync(path, ct)
                  ├── 命中 _pixelCache → Task.Run: SoftwareBitmap 转换 → UI: SetBitmapAsync → SoftwareBitmapSource
                  └── 未命中 → Task.Run: pipeline.Decode(concurrentSafe:true) → 存像素缓存 → SoftwareBitmap → UI: SetBitmapAsync
                  └── ZoomableImage.SetSource(loaded)  [PixelWidth > 0，立即显示]

后台预加载（1s debounce + diff-based，UpdatePreloadTasks）
├── WicImageLoader.PreloadAsync(path, ct)
│     → new BitmapImage(new Uri(path)) → _preloadCache  [同步，无 Task.Run]
└── MagickImageLoader.PreloadAsync(path, ct)
      → Task.Run: pipeline.Decode(concurrentSafe:true) → _pixelCache
```

### 列表页缩略图（PhotoListPage / AllPhotosPage）

```
GridView item 进入视口
└── PhotoItemViewModel.LoadThumbnailAsync(thumbService, ct)
      └── Task.Run: ThumbnailService.GetOrCreateThumbnailAsync()  ← 生成/取缓存缩略图路径（磁盘 JPEG）
      └── new BitmapImage(new Uri(thumbPath))
      → PhotoItemViewModel.ThumbnailSource = bitmapImage  ← 绑定到 XAML Image.Source
```

### 缩略图生成（ThumbnailService，后台，不经过 Loader）

```
ThumbnailService.GenerateViaDecoderAsync(sourcePath, destPath, thumbSize, ct)
└── ImageDecoderPipeline.TryDecodeAsync(concurrentSafe: true)
      ├── 标准格式 → WicImageDecoder（concurrent-safe，直接使用）
      └── HEIC → WicImageDecoder(HEIC) 被跳过 → MagickImageDecoder（concurrent-safe）
└── EncodeToJpegAsync() → 写入磁盘缓存
```

> `concurrentSafe: true` 保证 HEIC 缩略图生成走 MagickImageDecoder（线程安全），避免 WIC HEIC codec 在 `MaxConcurrent=2` 时的并发崩溃。标准格式 WIC codec 本来就是并发安全的，直接使用。

---

## DeferDispose — 防止 SoftwareBitmapSource use-after-free

**仅适用于 MagickImageLoader 返回的 `SoftwareBitmapSource`**。`BitmapImage`（标准格式，`WicImageLoader`）不实现 `IDisposable`，无此问题。

`SoftwareBitmapSource.Dispose()` 释放 GPU 纹理。若 compositor 仍在引用该纹理时调用 Dispose，会导致 `STATUS_STOWED_EXCEPTION (0xC000027B)` 崩溃（native AV，绕过所有 .NET 异常处理器）。

解决方案（`ZoomableImage.DeferDispose`）：

```csharp
// 双重 Low-priority enqueue，确保经过两次消息循环迭代
// 第一次：XAML 将 Source=null 提交给 compositor
// 第二次：compositor 处理完毕，释放 GPU surface 引用
DispatcherQueue.TryEnqueue(Low, () =>
    DispatcherQueue.TryEnqueue(Low, () =>
    {
        try { disposable.Dispose(); } catch { }
    }));
```

`_currentDisposable` 跟踪当前显示的 `SoftwareBitmapSource`（`WicImageLoader` 路径下为 null）：

- `ZoomableImage.SetLoading()`：`DeferDispose(_currentDisposable)`，`_currentDisposable = null`
- `ZoomableImage.SetSource()`：`DeferDispose(_currentDisposable)`，`_currentDisposable = image.Source as IDisposable`
- `PhotoDetailPage.LoadCurrentImageAsync`：stale load result（gen 检查失败时），单次 Low-priority enqueue 释放（source 从未进入 compositor）

---

## 层次与解耦关系

```text
┌─────────────────────────────────────────────────────────────┐
│                        UI 层                                 │
│   PhotoDetailPage  /  PhotoItemViewModel                     │
│   ZoomableImage（纯 UI 控件，接受 LoadedImage）              │
└───────────────┬─────────────────────────────────────────────┘
                │ Task<LoadedImage?>（Loader 返回值）
                │ 无 UI 类型向下传递
┌───────────────▼─────────────────────────────────────────────┐
│                      Loader 层                               │
│   WicImageLoader    — BitmapImage.UriSource，白名单格式      │
│   MagickImageLoader — BGRA8 像素缓存 + SoftwareBitmapSource │
│   - 管理内存缓存（BitmapImage / DecodedImageData）          │
│   - 管理预加载（1s debounce, diff-based, per-path CTS）     │
│   - 不持有任何 UI 组件引用                                   │
└───────────────┬─────────────────────────────────────────────┘
                │ DecodedImageData（原始像素，仅 HEIC 路径）
┌───────────────▼─────────────────────────────────────────────┐
│                     Decoder 层                               │
│   ImageDecoderPipeline                                       │
│   WicImageDecoder  /  MagickImageDecoder                     │
│   - 纯文件 → 像素的转换，无任何 UI 依赖                     │
│   - ThumbnailService 也直接调用此层（concurrentSafe: true）  │
└─────────────────────────────────────────────────────────────┘
```

## 线程安全策略

| 路径 | 机制 |
|---|---|
| 白名单格式主图加载（JPG/PNG 等） | `WicImageLoader`：`BitmapImage.UriSource`，XAML 内部线程安全 |
| 非白名单格式主图加载（HEIC 等） | `MagickImageLoader`：`concurrentSafe: true` → MagickImageDecoder + `SoftwareBitmapSource.SetBitmapAsync`（UI 线程） |
| 标准格式缩略图生成 | WIC 标准 codec（`SupportsConcurrentDecode=true`），无需额外保护 |
| HEIC 缩略图生成 | `ThumbnailService`：`concurrentSafe: true` → MagickImageDecoder（线程安全） |
| 非白名单格式预加载（HEIC 等） | `MagickImageLoader.PreloadAsync`：`concurrentSafe: true` → MagickImageDecoder，N 路完全并行 |
| SoftwareBitmapSource 释放 | `DeferDispose`（双重 Low-priority enqueue），仅 `MagickImageLoader` 路径需要 |
