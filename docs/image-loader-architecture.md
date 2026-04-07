# Image Loader & Decoder 架构

## 概述

图片加载分为两层：

- **Decoder 层**（`FluentGallery/Decoders/`）：将磁盘文件解码为原始像素数据（`DecodedImageData`）。与 UI 完全无关。
- **Loader 层**（`FluentGallery/Loaders/`，待实现）：将文件路径转换为 `BitmapImage`，供 UI 层使用。内部按需调用 Decoder 层，并管理内存缓存和预加载。

两层均**不依赖任何 UI 组件**（`ZoomableImage`、`Page`、`ViewModel` 等）。UI 层调用 Loader 取得 `BitmapImage` 后，自行决定如何渲染。

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

    // 后台预加载，结果写入 loader 内部缓存，调用方无需等待结果
    Task PreloadAsync(string filePath, CancellationToken ct);

    // 加载并返回 BitmapImage，命中缓存则直接返回，否则现场加载
    // 必须在 UI 线程调用（BitmapImage.SetSourceAsync 要求）
    Task<BitmapImage?> LoadAsync(string filePath, CancellationToken ct);
}
```

### WicImageLoader

适用于 WIC 原生支持的格式（JPG/PNG/GIF/WebP 等）及缩略图加载。

- **PreloadAsync**：`new BitmapImage(new Uri(path))` 写入缓存，完全异步，线程安全
- **LoadAsync**：命中缓存直接返回；未命中则 `new BitmapImage(new Uri(path))` 并缓存
- **缓存**：`Dictionary<string, BitmapImage>`，LRU 淘汰

### HeicImageLoader

适用于 HEIC/HEIF 格式。核心目标：规避 WIC HEIC codec 的并发崩溃。

- **PreloadAsync**（后台线程）：
  1. `GetDecoder(path, concurrentSafe: true)` → 选取 `MagickImageDecoder`（跳过不安全的 WIC HEIC）
  2. decode → 原始像素
  3. PNG encode → `MemoryStream`，存入缓存
  4. 通过 `SemaphoreSlim(1,1)` 串行化，防止 Magick.NET 并发过高

- **LoadAsync**（UI 线程）：
  1. 命中缓存：`MemoryStream` → `new BitmapImage()` + `await SetSourceAsync(stream)` → 返回
  2. 未命中：同 PreloadAsync 的 decode+encode 流程，再 SetSourceAsync，返回 BitmapImage

- **缓存**：`Dictionary<string, MemoryStream>`（PNG bytes），LRU 淘汰
- **SemaphoreSlim**：预加载和即时加载共用同一个 semaphore，保证 decode 串行

> **为什么存 PNG 而不是存 `BitmapImage`？**
> PNG bytes 可以在后台线程生成；`BitmapImage.SetSourceAsync` 必须在 UI 线程调用，无法在 PreloadAsync 中完成。

---

## 调用链

### 详情页（PhotoDetailPage）

```
用户切换图片
└── PhotoDetailPage.LoadCurrentImageAsync()
      ├── ext ∈ {.heic, .heif}
      │     └── HeicImageLoader.LoadAsync(path, ct)
      │           ├── 命中缓存 → MemoryStream → BitmapImage(SetSourceAsync)
      │           └── 未命中  → GetDecoder(concurrentSafe:true)
      │                         → MagickImageDecoder.DecodeAsync()
      │                         → PNG encode → BitmapImage(SetSourceAsync)
      └── 其他格式
            └── WicImageLoader.LoadAsync(path, ct)
                  ├── 命中缓存 → BitmapImage
                  └── 未命中  → new BitmapImage(new Uri(path))

PhotoDetailPage.LoadCurrentImageAsync() 拿到 BitmapImage 后：
└── ZoomableImage.SetSource(bmp)   ← UI 层自行决定如何渲染

后台预加载（PreloadAdjacent）
└── HeicImageLoader.PreloadAsync(path, preloadCt)
      → GetDecoder(concurrentSafe:true) → decode → PNG encode → 存缓存
└── WicImageLoader.PreloadAsync(path, preloadCt)
      → new BitmapImage(new Uri(path)) → 存缓存
```

### 列表页缩略图（AlbumListPage / PhotoListPage）

```
GridView item 进入视口
└── PhotoItemViewModel.LoadThumbnailAsync()
      └── ThumbnailService.GetOrCreateThumbnailAsync()   ← 生成/取缓存缩略图路径
            └── ImageDecoderPipeline.TryDecodeAsync()    ← 仅生成阶段调用
      └── WicImageLoader.LoadAsync(thumbPath, ct)        ← 展示：缩略图始终是 JPEG，走 WIC 即可
            → new BitmapImage(new Uri(thumbPath))
      → PhotoItemViewModel.ThumbnailSource = bmp         ← 绑定到 XAML Image.Source
```

### 缩略图生成（ThumbnailService，不经过 Loader）

```
ThumbnailService.GetOrCreateThumbnailAsync()
└── ImageDecoderPipeline.TryDecodeAsync(path, thumbSize, thumbSize)
      ├── WicImageDecoder（标准格式）
      └── MagickImageDecoder（HEIC fallback）
└── EncodeToJpegAsync() → 写入磁盘
```

---

## 层次与解耦关系

```
┌─────────────────────────────────────────────────────┐
│                    UI 层                             │
│   PhotoDetailPage  /  PhotoItemViewModel             │
│   ZoomableImage（纯 UI 控件，只接受 BitmapImage）    │
└───────────────┬─────────────────────────────────────┘
                │ Task<BitmapImage>（Loader 返回值）
                │ 无 UI 类型向下传递
┌───────────────▼─────────────────────────────────────┐
│                  Loader 层                           │
│   WicImageLoader  /  HeicImageLoader                 │
│   - 管理内存缓存（BitmapImage / PNG MemoryStream）   │
│   - 管理预加载和并发控制                             │
│   - 不持有任何 UI 组件引用                           │
└───────────────┬─────────────────────────────────────┘
                │ DecodedImageData（原始像素）
┌───────────────▼─────────────────────────────────────┐
│                 Decoder 层                           │
│   ImageDecoderPipeline                              │
│   WicImageDecoder  /  MagickImageDecoder            │
│   - 纯文件 → 像素的转换，无任何 UI 依赖             │
└─────────────────────────────────────────────────────┘
```

**Loader 层与 UI 层的解耦边界**：Loader 的返回类型是 `Task<BitmapImage>`，`BitmapImage` 是 WinRT 图像数据类型，本身不是 UI 控件。Loader 不持有 `ZoomableImage`、`Page`、`ViewModel` 等引用，不调用任何 XAML 方法。UI 层拿到 `BitmapImage` 后自行决定渲染方式。

**Decoder 层与 UI 层的解耦边界**：Decoder 返回 `DecodedImageData`（纯 `byte[]` + 元数据），完全不涉及 WinRT UI 类型。

---

## ZoomableImage 变更（待实现）

移除后：
- `_pipelineExtensions`、`_decoderPipeline` 字段
- `LoadViaDecoderPipelineAsync`、`LoadSoftwareBitmapFromCacheAsync` 方法
- 对 `ImageDecoderPipeline` 的依赖

新增：
- `void SetSource(BitmapImage bitmap)`：设置 source，reset zoom，调用 `FitToWindow()`
- 内部监听 `BitmapImage.ImageOpened` 事件，获取 `PixelWidth/PixelHeight` 后更新 `MainImage.Width/Height` 和 fit-zoom
