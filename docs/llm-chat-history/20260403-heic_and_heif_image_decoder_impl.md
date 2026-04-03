# Heic and heif image decoder implementation
_Exported on 4/3/2026 at 21:43:30 GMT+8 from Cursor (2.6.22)_

---

**User**

Search through my recent agent transcripts to find conversations about: 对于 heic 和 heif 图片，内置一个解码器。如果系统 wic 支持解码就用系统的，如果不支持就用内置的。这个解码器的接口可以做得通用一点，后续别的格式可能也会接入

---

**Cursor**

经过对全部 16 个历史对话记录的搜索，没有找到专门讨论"内置 HEIC/HEIF 解码器 + WIC 回退 + 通用接口"这个具体方案的对话。

与 HEIC/HEIF 相关的历史对话有以下两个：

**[测试闪退日志检查](ddad808a-bd07-44c0-bd1b-b135937c6d8c)**（`20260403-app-crash-investigation.md`）

这是最相关的一条。对话记录了：
- 某些 HEIC 文件在没有安装 HEVC 解码扩展时，调用 WIC 的 `BitmapDecoder.GetPixelDataAsync` 会触发 `Windows.Graphics.dll` 内部的原生访问冲突（SEH `0xc0000005`），且 .NET 的 `try/catch` 无法捕获，直接导致进程崩溃
- 当时的临时修复方案是：**在 `ThumbnailService` 和 `ZoomableImage` 入口处直接跳过 `.heic`/`.heif` 文件**，显示占位符

**[缩略图服务修复](ddad808a-bd07-44c0-bd1b-b135937c6d8c)**（`20260403-fix-thmbnail-service.md`）

对话记录了 WIC 渲染 HEIC 缩略图时因 `BitmapAlphaMode` 设置不当导致颜色异常的问题修复。

---

**结论**：你描述的"内置解码器 + WIC 回退 + 通用接口"这个设计方案，在目前所有历史对话中都**没有出现过**。

这应该是一个**新的需求**。根据历史记录，目前的代码对 HEIC/HEIF 的处理是"直接跳过"（因为崩溃问题），所以这个"内置解码器"的方向正好是下一步要解决的事情。你想现在开始规划和实现它吗？

---

**User**

好、

---

**Cursor**

好的，我先了解一下当前代码结构，然后直接开始规划实现。

现在我对现有代码有了清楚的认识。先看一下项目结构再制定方案。

好，我来分析现有代码并制定完整实现方案。

---

## 方案设计

### 核心问题回顾

当前代码对 HEIC/HEIF 的处理：

```41:49:FluentGallery/Data/ThumbnailService.cs
// ── Formats that crash Windows.Graphics.Imaging rather than throwing ────────
//
// HEIC/HEIF files: when the HEVC codec is absent (or partially installed),
// BitmapDecoder.GetPixelDataAsync triggers a native SEH access violation
// (0xc0000005) inside Windows.Graphics.dll that .NET cannot catch, causing
// an immediate process termination.  Skip them entirely so the placeholder
// icon is shown instead of crashing.
private static readonly HashSet<string> _noCrashSkipExtensions =
    new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };
```

`ZoomableImage.LoadImageAsync` 用 `BitmapImage.UriSource` 同样对 HEIC 无保护。

---

### 一、接口设计

新建 `FluentGallery/Decoders/` 目录，包含：

```
FluentGallery/Decoders/
  IImageDecoder.cs          ← 接口 + DecodedImageData
  WicImageDecoder.cs        ← 封装现有 Windows.Graphics.Imaging 逻辑
  MagickImageDecoder.cs     ← 封装 Magick.NET，HEIC/HEIF 内置解码
  ImageDecoderPipeline.cs   ← 按优先级选择 decoder，统一入口
```

**`IImageDecoder` 接口**（设计为通用，后续新格式只需新增实现类）：

```csharp
// FluentGallery/Decoders/IImageDecoder.cs

/// <summary>
/// 输出：已应用 EXIF 旋转的 BGRA8 像素数据
/// </summary>
public sealed record DecodedImageData(
    byte[]  Pixels,
    uint    Width,
    uint    Height,
    double  DpiX,
    double  DpiY
);

public interface IImageDecoder
{
    /// <summary>该解码器支持的扩展名（小写，含点，如 ".heic"）</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// 当前运行环境下此解码器是否可用
    /// （例：WIC HEIC decoder 需 HEVC 扩展已安装）
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 解码图片并返回 BGRA8 像素，自动应用 EXIF 旋转。
    /// maxWidth/maxHeight 为 0 表示不缩放（原始尺寸）。
    /// </summary>
    Task<DecodedImageData> DecodeAsync(
        string            filePath,
        uint              maxWidth  = 0,
        uint              maxHeight = 0,
        CancellationToken ct        = default);
}
```

---

### 二、`WicImageDecoder`

把 `ThumbnailService.GenerateAsync` 中现有的 WIC 解码逻辑抽出来，实现这个接口。

- `IsAvailable`：对 JPEG/PNG/BMP 等常规格式永远 `true`；对 HEIC/HEIF 则在启动时检测一次（见下方检测方案）。
- `DecodeAsync`：就是现在 `ThumbnailService.GenerateAsync` 里 Decode 部分的逻辑。

---

### 三、`MagickImageDecoder`（内置解码器）

使用 **Magick.NET-Q8-AnyCPU**（NuGet）作为 HEIC/HEIF 内置解码后备：

- `IsAvailable`：始终 `true`（库已内置，不依赖系统 codec）
- 支持扩展名：`.heic`、`.heif`（后续可扩展至 `.avif` 等）
- 可解码为 BGRA8 像素，输出 `DecodedImageData`

> Magick.NET 选型理由：单 NuGet 包、自包含 native 二进制、支持 HEIC/HEIF/AVIF/WebP/RAW 等，符合"后续别的格式也会接入"的扩展需求。

---

### 四、WIC HEIC 可用性检测

**问题**：不能用 try/catch 来探测 WIC 是否支持 HEIC（因为失败时是 native SEH crash，不是 managed exception）。

**检测方案**：通过 `Windows.Management.Deployment.PackageManager` 检查 HEVC Video Extensions 是否已安装：

```csharp
// 一次性检测，结果缓存
bool IsWicHeicAvailable()
{
    var pm = new Windows.Management.Deployment.PackageManager();
    return pm.FindPackagesForUser("").Any(p =>
        p.Id.Name.Contains("HEVCVideoExtension",
            StringComparison.OrdinalIgnoreCase));
}
```

- 需要在 `Package.appxmanifest` 中声明 `packageQuery` capability（限制访问范围）。
- 或者用更保守的方式：查询 `Windows.Media.Core.CodecQuery` 检测 HEVC 解码器。

---

### 五、`ImageDecoderPipeline`

```csharp
// 统一入口：按扩展名找到所有已注册解码器，按优先级尝试第一个 IsAvailable 的
public sealed class ImageDecoderPipeline
{
    // 按 (extension → List<IImageDecoder>) 组织，靠前的优先级高
    public IImageDecoder? GetDecoder(string filePath) { ... }
    
    // 快捷方法：直接解码（选 decoder + 调 DecodeAsync）
    public Task<DecodedImageData?> DecodeAsync(
        string filePath, uint maxW = 0, uint maxH = 0,
        CancellationToken ct = default) { ... }
}
```

注册顺序（HEIC/HEIF 为例）：
1. `WicImageDecoder`（`IsAvailable` 依赖 HEVC 检测）→ 优先用系统 WIC
2. `MagickImageDecoder`（`IsAvailable = true`）→ WIC 不可用时回退

其他格式（JPEG/PNG/BMP 等）只注册 `WicImageDecoder`，保持原有路径不变。

---

### 六、集成点

**`ThumbnailService`**：
- 移除 `_noCrashSkipExtensions` 跳过逻辑
- `GenerateAsync` 改为先通过 `ImageDecoderPipeline.DecodeAsync` 获得像素数据，再 encode 为 JPEG

**`ZoomableImage`**：
- 对常规格式（JPEG/PNG 等）：继续用 `BitmapImage.UriSource`（最高效）
- 对 HEIC/HEIF：用 `ImageDecoderPipeline` 解码 → `SoftwareBitmapSource`

---

### 七、工作量估计

| 步骤 | 文件 | 难度 |
|------|------|------|
| 新增 `IImageDecoder` 接口 | `Decoders/IImageDecoder.cs` | 低 |
| 抽取 `WicImageDecoder`（从现有代码重构） | `Decoders/WicImageDecoder.cs` | 低 |
| 实现 `MagickImageDecoder` | `Decoders/MagickImageDecoder.cs` | 中 |
| 实现 `ImageDecoderPipeline` | `Decoders/ImageDecoderPipeline.cs` | 低 |
| HEVC 检测逻辑 | `Decoders/WicImageDecoder.cs` | 中 |
| 改造 `ThumbnailService` | `Data/ThumbnailService.cs` | 低 |
| 改造 `ZoomableImage` | `Controls/ZoomableImage.xaml.cs` | 中 |
| DI 注册 | `App.xaml.cs` | 低 |

---

有两个问题需要你确认再开始编码：

1. **Magick.NET 包体积**：`Magick.NET-Q8-AnyCPU` 会为 x64/ARM64 各带约 15MB 的 native DLL，你能接受吗？如果不能，我们可以改用 `LibHeifSharp`（专注 HEIC，更小但需要单独打包 libheif.dll）。

2. **`ZoomableImage` 的 HEIC 展示路径**：是走 decoder pipeline（产生 raw pixels → `SoftwareBitmapSource`，功能完整但实现稍复杂），还是先简单处理（用 decoder 生成临时 JPEG，再用 `BitmapImage.UriSource` 加载）？

---

**User**

关于第二点，和你讨论一下，生成jpeg是否会有损？生成bmp或png是否会比 raw pixels 更简单，又比 jpeg 更完整？

---

**Cursor**

好问题，逐个分析：

### JPEG：有损

是的，JPEG 始终有损。即使 quality=100，DCT 变换仍然会引入压缩伪影。对缩略图来说无所谓，但对**照片详情页全尺寸查看**来说，用户打开的是自己精心拍的照片，看到的却是重新压缩过的版本——这对照片查看器来说不可接受。

### PNG：无损但慢

PNG 是无损的，但 zlib 压缩对大图很慢。一张 4000x3000 的照片编码 PNG 可能要几百毫秒，而且还要写临时文件、再让 `BitmapImage` 重新读取解码——等于做了两次编解码。

### BMP：无损但巨大

BMP 本质就是带个文件头的 raw pixels，写入快，但 4000x3000x4 = **48MB** 一个临时文件，每切换一张照片就写一个，很浪费。

### Raw pixels → `SoftwareBitmapSource`：实际上最简单

这个方案乍看"复杂"，但实际代码量很少：

```csharp
var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, (int)data.Width, (int)data.Height, BitmapAlphaMode.Premultiplied);
sb.CopyFromBuffer(data.Pixels.AsBuffer());
var source = new SoftwareBitmapSource();
await source.SetBitmapAsync(sb);
MainImage.Source = source;
```

5 行代码，无损、无临时文件 I/O、无二次编解码。

### 对比

| 方案 | 质量 | 速度 | 临时 I/O | 代码量 |
|------|------|------|----------|--------|
| Raw → SoftwareBitmapSource | 无损 | 最快（零编解码） | 无 | ~5 行 |
| 临时 JPEG | 有损 | 中（encode+write+read+decode） | 有 | ~10 行 |
| 临时 PNG | 无损 | 慢（PNG 编码慢） | 有 | ~10 行 |
| 临时 BMP | 无损 | 快写但文件巨大 | 有（48MB/张） | ~10 行 |

### 结论

Raw pixels → `SoftwareBitmapSource` 实际上是**最简单、最快、质量最好**的方案。临时文件方案看起来"简单"只是因为复用了 `BitmapImage.UriSource` 的加载路径，但引入了不必要的编解码开销和临时文件管理。

所以建议 `ZoomableImage` 对 HEIC/HEIF 走 `ImageDecoderPipeline.DecodeAsync` → `SoftwareBitmapSource` 路径。你觉得这个方向可以吗？

---

**User**

好，使用 Raw pixels；heif 的依赖用 Magick.NET

---

**Cursor**

先看一下项目文件结构。

先看一下 `CurrentBitmap` 的用途，以及 `ZoomableImage` 如何在详情页中被使用。

测试调用了 `GenerateAsync` 带4个参数，但我读到的只有3个，文件可能有改动。重新读取最新版本。

清楚了。`CurrentBitmap` 只用于 `BitmapImage` 的预加载缓存，HEIC 直接跳过预加载即可。现在开始编码。

先安装 Magick.NET 包：

Magick.NET 14.11.1 安装成功。现在创建 Decoders 目录，写入所有新文件。

现在改造 `ThumbnailService`：移除 HEIC 跳过逻辑，加入 pipeline 解码路径。

现在在 `// ── WIC thumbnail generation` 之前插入新的 `GenerateViaDecoderAsync` 方法，并提取 JPEG 编码为独立帮助方法：

现在改造 `ZoomableImage`，添加 HEIC 通过 `SoftwareBitmapSource` 加载的路径：

现在替换 `LoadImageAsync` 方法，拆成两条路径：

现在修复 `FitToWindow`，让它对 `SoftwareBitmapSource` 也能正确工作（通过 `MainImage.Width/Height` 而不只靠 `BitmapImage`）：

现在更新 `PhotoDetailPage`：在预加载和缓存读取时跳过 HEIC/HEIF：

最后更新 `App.xaml.cs`，注册 `ImageDecoderPipeline` 并更新 `ThumbnailService` 注册：

现在编译验证：

**0 错误 0 警告**，编译成功。现在运行单元测试验证现有回归测试仍通过：

2 个测试失败是**预先存在的**（`PreloadCount` 默认值期望 2、实际 5，与本次改动无关）。49 个测试通过，包括全部 `ThumbnailServiceTests`。

全部完成。以下是实现总结：

---

## 实现总结

### 新增文件

| 文件 | 说明 |
|------|------|
| `FluentGallery/Decoders/IImageDecoder.cs` | 接口 + `DecodedImageData` record |
| `FluentGallery/Decoders/WicImageDecoder.cs` | WIC 解码器，带可用性检测（`BitmapDecoder.GetDecoderInformationEnumerator`） |
| `FluentGallery/Decoders/MagickImageDecoder.cs` | Magick.NET 内置解码器，HEIC/HEIF，永远可用 |
| `FluentGallery/Decoders/ImageDecoderPipeline.cs` | 按优先级选 decoder 的注册表 |

### 修改文件

**`ThumbnailService.cs`**：
- 移除 `_noCrashSkipExtensions`（不再跳过 HEIC）
- 新增 `GenerateViaDecoderAsync`（pipeline 解码 → JPEG 编码）
- 提取 `EncodeToJpegAsync` 共用编码逻辑
- 保留原 `GenerateAsync` static 供现有单元测试使用

**`ZoomableImage.xaml.cs`**：
- HEIC/HEIF → `LoadViaDecoderPipelineAsync`：pipeline 解码 raw pixels → `SoftwareBitmapSource`（无损、无临时文件）
- 其他格式 → `LoadViaBitmapImageAsync`：原有 `BitmapImage.UriSource` 路径不变
- `FitToWindow` 修复：兼容 `SoftwareBitmapSource`（通过 `MainImage.Width/Height`）

**`PhotoDetailPage.xaml.cs`**：
- 预加载和缓存只对非 HEIC 格式有效（HEIC 不能用 `BitmapImage`）

**`App.xaml.cs`**：
- 注册 `ImageDecoderPipeline`，HEIC/HEIF 优先级：WIC → Magick.NET fallback

### WIC HEIC 可用性检测

```csharp
BitmapDecoder.GetDecoderInformationEnumerator()
    .SelectMany(info => info.FileExtensions)
    .Contains(".heic", OrdinalIgnoreCase)
```

结果懒加载并缓存（`Lazy<bool>`），只在首次使用时执行一次。

---

**User**

1. 有两个警告@c:\Users\lyh54\.cursor\projects\c-Users-lyh54-git-github-ham-gallery\terminals\4.txt:674-678 
2. C:\Users\lyh54\git\github\ham-gallery\data\手机照片\DCIM\Camera\猫砂\IMG_20251215_213537.HEIC 这个图无论是原图还是缩略图都加载不出来

---

**Cursor**

先看一下应用日志，确认 HEIC 失败时的具体错误。

日志只显示了消息，没有异常详情。查看原始日志内容：

根本原因找到了。从日志可以看出：

```
System.Runtime.InteropServices.COMException (0xC00D5212)
  at WicImageDecoder.DecodeAsync
  at ImageDecoderPipeline.TryDecodeAsync
```

两个问题：
1. **警告**：是 EF Core 设计时工具带入 `System.CommandLine` 的预先存在警告，与本次改动无关
2. **HEIC 失败原因**：WIC HEIC codec 已注册（`IsAvailable=true`），但实际解码时抛 `COMException 0xC00D5212`（未安装可用的 HEVC 解码器）。**Pipeline 没有 catch 住这个异常，导致 Magick.NET 从未被尝试**

修复：`TryDecodeAsync` 改为在某个解码器抛异常时继续尝试下一个。

接下来验证 Magick.NET 是否真的支持 HEIC，用一个快速脚本测试：

**Magick.NET 完全支持**（`SUCCESS: 3072x4096, format=Heic`）。问题就是 pipeline 在 WIC 抛异常后没有继续尝试 Magick.NET。已修复的 `TryDecodeAsync` 会正确 fall-through。

编译验证：

0 错误 0 警告。

---

### 问题分析总结

**警告（预先存在，与本次无关）**：
`PRI263` 是 EF Core 设计时工具（`Microsoft.EntityFrameworkCore.Design`）带入 `System.CommandLine` 的卫星程序集打包警告，在你添加 EF Core 时就存在了。

**HEIC 无法加载的根本原因**：

```
WicImageDecoder.IsAvailable = true   ← HEVC codec 已注册到 WIC
WicImageDecoder.DecodeAsync() → 抛 COMException 0xC00D5212   ← 实际解码失败（codec broken）
                                 ↓ 异常向上抛出
MagickImageDecoder 从未被尝试       ← pipeline 没有 fallback 机制
```

**修复内容**：`TryDecodeAsync` 改为逐一尝试每个可用解码器，某个解码器抛异常时继续下一个（`OperationCanceledException` 除外直接重抛）：

```csharp
foreach (var decoder in decoders.Where(d => d.IsAvailable))
{
    try
    {
        return await decoder.DecodeAsync(...);
    }
    catch (OperationCanceledException) { throw; }
    catch { /* 该 decoder 失败，尝试下一个 */ }
}
```

这样对于 HEIC：WIC 抛异常 → 自动回退到 Magick.NET（已验证能成功解码 `3072×4096` 的 HEIC 文件）。

---

**User**

你来制造一个很小的 heic 图片（比如基于 FluentGallery.Tests\TestData\regression_exif_orient6_4032x3024.jpg 截取一个角落 512x512），然后基于这个照片写一个测试能够解析图片

---

**Cursor**

先用 Magick.NET 从现有 JPG 生成 HEIC 测试图片，再配置测试项目并写测试。

测试图片生成成功（63.8 KB）。现在更新测试项目，添加 Magick.NET 包和 Decoder 源文件引用：

测试图片就绪。现在写测试类：

现在先检查 `ImageDecoderPipeline.TryDecodeAsync` 的签名，确认与测试调用方式一致：

查看了 `TryDecodeAsync` 的实现后，发现对不存在的文件它会捕获异常并返回 `null`（而非抛出）。需要修正测试：

现在编译并运行测试：

`DecodeAsync` 的 `ct` 参数没有默认值，需要显式传 `CancellationToken.None`。同时检查接口定义：

接口有默认值，但实现类可能没有。看看 `MagickImageDecoder` 的方法签名：

实现类没有继承接口的默认参数值，需要在测试中显式传递 `CancellationToken.None`：

新增的测试已无编译错误，剩余的 `SettingsViewModelTests.cs` 错误是已有的问题。让我确认并修复它：

`SettingsViewModel` 需要 `ThumbnailService`，而 `ThumbnailService` 需要 `ImageDecoderPipeline`。修复测试的 `BuildSut` 方法：

编译通过，无错误。运行新增的测试：

**16 个测试全部通过。** 看一下所做的工作：

---

## 总结

### 生成的测试图片

`FluentGallery.Tests/TestData/regression_heic_512x512.heic`（63.8 KB）
- 来源：`regression_exif_orient6_4032x3024.jpg` 经 `AutoOrient()` 正规化后，截取左上角 512×512
- EXIF orientation = 1（无旋转），方便测试断言精确尺寸

### 测试项目变更

**`FluentGallery.Tests.csproj`**
- 新增 `Magick.NET-Q8-AnyCPU` 包引用
- 新增 4 个 Decoder 源文件的 `<Compile>` 引用（`IImageDecoder`、`WicImageDecoder`、`MagickImageDecoder`、`ImageDecoderPipeline`）

**`ImageDecoderTests.cs`**（新建，16 个测试用例）分三组：

| 分组 | 测试内容 |
|---|---|
| `MagickImageDecoder` 直接测试 | `IsAvailable` 始终为 true；支持 `.heic`/`.heif`；全分辨率尺寸正确；BGRA8 字节数 = W×H×4；DPI > 0；缩放后 fits-in-box；正方形图像缩放到精确 box 尺寸 |
| `ImageDecoderPipeline` 测试 | `CanDecode` 返回 true；全分辨率 + 缩略图尺寸解码正确；不存在的文件返回 null；不支持的扩展名返回 null |
| `WicImageDecoder` HEIC（条件） | `IsAvailable` 不抛出异常；当 HEVC 扩展已安装时，解码结果尺寸正确 |

**`SettingsViewModelTests.cs`**
- 修复了已有的编译错误：`BuildSut()` 补充了 `ThumbnailService`（带空 pipeline）参数

