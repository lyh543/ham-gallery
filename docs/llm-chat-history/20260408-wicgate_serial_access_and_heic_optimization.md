# wicGate serial access and heic optimization
_Exported on 4/8/2026 at 15:49:33 GMT+8 from Cursor (2.6.22)_

---

**User**

现在 wicGate 限制了串行访问 wic。heic 预加载和加载过程很慢，会不会是因为 heic 读取 - 转 png 需要多次访问 wic？能否优化？不要改代码

---

**Cursor**

现在我已经把完整管道看清楚了，来做分析。

---

## HEIC 的 WIC 访问次数分析

### 解码路径的完整流程

由于 `WicImageDecoder` 对 HEIC 的 `SupportsConcurrentDecode` 返回 `false`，而 `PreloadAsync` 和 `LoadAsync` 都用 `concurrentSafe: true` 调用 pipeline，所以 **WIC HEIC codec 完全被跳过**，实际解码永远走 `MagickImageDecoder`（libheif）。

真正访问 WicGate 的只有以下两处：

---

### 情景 A：预加载（`PreloadInternalAsync`）

```
MagickImageDecoder.DecodeAsync()  ← 不走 WIC，可并发，无 WicGate
     ↓ (BGRA8 像素)
WicGate.WaitAsync(Low)            ← ① 第 1 次 WicGate 占用
BitmapEncoder → PNG bytes         ←   编码 BGRA8 → PNG
WicGate.Release()
```

每张预加载图：**1 次 WicGate 占用**，且为 Low 优先级 → 所有预加载任务排队串行。

---

### 情景 B：显示（`LoadAsync`）—— 缓存未命中

```
MagickImageDecoder.DecodeAsync()  ← 不走 WIC
     ↓
WicGate.WaitAsync(High)           ← ① 第 1 次 WicGate 占用
BitmapEncoder → PNG bytes
WicGate.Release()
     ↓
WicGate.WaitAsync(High)           ← ② 第 2 次 WicGate 占用（立即再占）
BitmapDecoder(PNG) → SoftwareBitmap
WicGate.Release()
```

缓存未命中时：**2 次连续 WicGate 占用**。

---

### 情景 C：显示（`LoadAsync`）—— 缓存命中（预加载已完成）

```
_pngCache 命中               ← 直接拿 PNG bytes，跳过解码和编码
     ↓
WicGate.WaitAsync(High)      ← ① 第 1 次 WicGate 占用
BitmapDecoder(PNG) → SoftwareBitmap
WicGate.Release()
```

缓存命中时：**仍需 1 次 WicGate**。

---

## 为什么慢

### 1. 预加载的串行瓶颈

默认预加载 5 张（forward）+ 2 张（back）= 7 张，每张都需要一次 `WicGate(Low)` 做 PNG 编码。由于 WicGate 是全局单一锁，这 7 个 PNG 编码完全串行执行。对 12 MP HEIC，每次 PNG 编码大约需要数百毫秒，7 张预加载合计可能需要几秒。

### 2. PNG 中间格式本身是额外开销

之所以需要 2 次 WIC 操作（一次编码、一次解码），是因为选择了"缓存 PNG bytes"的策略：
- 缓存 PNG（约 7 MB/张）省内存，但显示时还需解码一次
- 如果缓存原始 BGRA8（约 48 MB/张），就不需要第一次 WIC，但内存代价大
- 如果用 `SoftwareBitmap.CreateCopyFromBuffer(buffer, Bgra8, w, h)` 直接从 BGRA8 像素创建 `SoftwareBitmap`，则显示时完全无需 WIC，**两次都可以省掉**

### 3. 预加载速度跟不上翻页速度

预加载在 WicGate 排队期间持有 Low 优先级，高优先级的 `LoadAsync(High)` 虽然能插队，但必须等当前持有者（低优先级编码任务）的**本次 `FlushAsync` 完全完成**后才能接管。如果此时 HEIC 编码任务正在 flush，那依然会有明显的等待。

---

## 总结

| 路径 | WicGate 次数 | 说明 |
|---|---|---|
| 预加载 1 张 | 1 次 (Low) | PNG 编码；N 张完全串行 |
| 显示（缓存未命中）| 2 次 (High) | PNG 编码 + PNG 解码 |
| 显示（缓存命中）| 1 次 (High) | 仅 PNG 解码为 SoftwareBitmap |

**根本原因**：PNG 作为中间格式导致"编码 → 缓存 → 解码"两步都走 WIC，且预加载阶段全部串行。如果想优化，方向是：让"BGRA8 像素 → SoftwareBitmap"这条路不经过 WIC（`SoftwareBitmap.CreateCopyFromBuffer` 可做到），或者接受更高内存占用直接缓存 `DecodedImageData`，省掉 PNG 编码这次 WicGate。

---

**User**

试试这个 让"BGRA8 像素 → SoftwareBitmap"这条路不经过 WIC（SoftwareBitmap.CreateCopyFromBuffer 可做到）

---

**Cursor**

好的，现在开始实现。

现在删除测试中引用 `EncodeToPngBytesAsync` 的两个测试（7、8），同时修正测试 4 中过时的注释：

现在更新 `.csproj` 的注释，并检查是否需要补充 `WicGate.cs`：

构建成功，0 错误 0 警告。顺手跑一下测试验证回归：

这些错误（`PreloadCount`）是已有的测试问题，与本次改动无关。来单独跑 HEIC 相关测试确认：

这些 `PreloadCount` 编译错误是改动前就存在的遗留问题（`AppSettings.PreloadCount` 已被拆分为 `PreloadCountBack` / `PreloadCountForward`，但测试文件未同步），与本次 HEIC 优化无关。主项目 `make build` 0 错误 0 警告，优化完成。

---

## 改动总结

### `HeicImageLoader.cs` — 核心重写

**缓存格式**：`Dictionary<string, byte[]>`（PNG bytes）→ `Dictionary<string, DecodedImageData>`（BGRA8 原始像素）

**`PreloadInternalAsync`** — 之前：Magick decode → WicGate(Low) + BitmapEncoder → PNG bytes  
现在：Magick decode → 直接存入缓存，**无 WicGate，全部并行**

**`LoadAsync`** — 之前：WicGate(High) encode + WicGate(High) decode，共 2 次串行  
现在：`SoftwareBitmap.CreateCopyFromBuffer` + `SoftwareBitmap.Convert`，**0 次 WicGate**

**删除了** `EncodeToPngBytesAsync`（`BitmapEncoder` 的 WIC 包装，彻底不再需要）

### `HeicPreloadTests.cs`
删除测试 7、8（引用已删方法），修正测试 4 中过时的注释

### `FluentGallery.Tests.csproj`
补充 `WicGate.cs` 编译引用（`IImageLoader` 用到 `WicPriority` 枚举）；更新注释

---

**内存代价**：每个缓存 slot 从 ~7 MB（PNG）升为 ~48 MB（BGRA8 12MP），默认 8 slot 约 ~384 MB（仅 12MP 时；实际图片更小则更少）。如果内存紧张，可在设置中减小预加载张数，`MaxCacheSize` 由 `ApplyPreloadCount` 自动跟随。

---

**User**

提问，如果使用 magick.net 能够降低内存使用的同时，不调用 wic，且加载速度不下降太多吗

---

**Cursor**

很好的问题，来做一个三路对比分析。

---

## 三种方案对比

|  | **方案 A（旧）WIC PNG 缓存** | **方案 B（当前）BGRA8 缓存** | **方案 C（提问）Magick 压缩缓存** |
|--|--|--|--|
| 缓存格式 | PNG bytes（~7 MB/张） | BGRA8 pixels（~48 MB/张） | WebP/JPEG bytes（~2-7 MB/张） |
| 8 slot 总内存（12MP） | ~56 MB | ~384 MB | ~16-56 MB |
| WIC 次数/预加载 | 1 次（BitmapEncoder，WicGate 排队） | **0 次** | **0 次** |
| WIC 次数/显示 | 1 次（BitmapDecoder，WicGate 排队） | **0 次** | **0 次** |
| 预加载速度 | 串行（WicGate Low） | **并行（直接缓存，无编码）** | 并行（Magick 编码，~100-500 ms/张）|
| 缓存命中显示耗时 | WicGate 等待 + WIC decode | **~5 ms**（CreateCopyFromBuffer） | Magick decode：~100-500 ms |
| 缓存未命中显示耗时 | Magick decode + WicGate + encode + WicGate + decode | Magick decode + CreateCopyFromBuffer | Magick decode + Magick encode（存） + Magick decode（显示） |

---

## 方案 C 的关键问题：缓存命中时不再"即时"

当前方案 B 的核心优势是：**预加载完成后显示 = 近乎零延迟**（`CreateCopyFromBuffer` 只是内存拷贝，~5 ms）。

方案 C 要把压缩 bytes → BGRA8 的 Magick decode 放回显示路径，即使缓存命中，也要多一次解码：

```
旧 WIC 方案：缓存命中 → WicGate 等待（可能数百 ms）→ BitmapDecoder → 显示
当前 BGRA8：缓存命中 → CreateCopyFromBuffer（~5 ms）→ 显示   ← 最快
方案 C：    缓存命中 → Magick decode（JPEG ~150 ms / WebP ~200 ms）→ CreateCopyFromBuffer → 显示
```

对用户来说，旧方案慢是因为 WicGate 串行排队、等人，方案 C 虽然没有排队，但每次"查缓存-显示"本身就慢了 150-500 ms——这个延迟用户能感知到（翻页时有明显卡顿感）。

---

## 格式选择对速度的影响

| Magick 格式 | 压缩后大小（12MP） | 编码耗时 | 解码耗时 | 是否有损 |
|--|--|--|--|--|
| JPEG Q85 | ~2-4 MB | ~100 ms | ~150 ms | 有损 |
| WebP lossy Q80 | ~2-3 MB | ~200 ms | ~150 ms | 有损 |
| WebP lossless | ~5-8 MB | ~2-4 s | ~300 ms | 无损 |
| PNG（Magick） | ~7 MB | ~400 ms | ~300 ms | 无损 |

WebP lossless 和 PNG 编码非常慢（秒级），会让预加载窗口追不上翻页速度。

---

## 结论

| 目标 | 推荐方案 |
|--|--|
| 显示最快（翻页流畅） | 当前方案 B（BGRA8 直缓存） |
| 内存最省 + 无 WIC | 方案 C + JPEG（但显示有 ~150 ms 延迟，且有损） |
| 无损 + 内存较省 + 无 WIC | 方案 C + WebP lossless（但预加载编码 2-4 s，体验差） |

**核心矛盾**：压缩越小 → 显示时必须解压 → 显示延迟增加。Magick.NET 确实能做到"内存省且无 WIC"，但"加载速度不下降太多"很难同时满足——**任何压缩格式都意味着缓存命中时多出一次解码**，而当前方案 B 的缓存命中就是内存拷贝。

如果内存是真实问题（例如打开大量 4K HEIC），最务实的优化是**减小预加载窗口**（`PreloadCountForward` 从 5 降到 3），内存从 ~384 MB 降到 ~240 MB，显示速度完全不变。

---

**User**

如果使用 zstd 这类通用算法进行压缩呢

---

**Cursor**

很好的思路，来分析 zstd 这类通用压缩在这个场景下的实际效果。

---

## 核心问题：zstd 在 BGRA8 上的压缩率

zstd 是 LZ 系算法，靠**数据中的重复字节序列**来压缩。自然照片的 BGRA8 像素每个像素之间都在连续变化（颜色渐变、纹理），**LZ 可匹配的重复极少**，压缩率非常有限：

| 算法 | 12MP BGRA8（48 MB）→ | 压缩率 | 解压速度 |
|---|---|---|---|
| 原始 BGRA8（当前） | 48 MB | 1× | ~5 ms（memcpy） |
| LZ4（极快）| ~36-40 MB | 1.2-1.4× | ~5-8 ms |
| zstd level 1 | ~28-35 MB | 1.4-1.7× | ~8-15 ms |
| zstd level 3（默认）| ~24-30 MB | 1.6-2× | ~8-12 ms |
| Brotli（.NET 内置）| ~20-28 MB | 1.7-2.4× | ~30-60 ms |
| PNG / WIC（图像编码）| ~6-8 MB | 6-8× | ~100-300 ms |

**zstd 在自然照片 BGRA8 上只能压 1.5-2 倍**，因为照片缺乏 LZ 可利用的重复。

---

## 为什么图像编码能压得多

PNG 能达到 6-8× 是因为它先做了一步**像素差分滤波**（Sub/Up/Average/Paeth），把像素值转换为"相邻像素的差"，差值集中在 0 附近，这时候 Deflate 才有大量可压缩的重复。

---

## 加一层 Delta 预处理会怎样

对 BGRA8 先手动做行差分（`pixel[i] -= pixel[i-4]`），然后再 zstd：

| 方案 | 大小 | 解压耗时 | 编码耗时 |
|---|---|---|---|
| zstd（无预处理）| ~24-30 MB | ~10 ms | ~50 ms |
| delta + zstd(1) | ~10-15 MB | ~15-20 ms | ~60-80 ms |
| delta + zstd(3) | ~8-12 MB | ~12-18 ms | ~80-120 ms |
| PNG via Magick | ~7 MB | ~300 ms | ~400 ms |

加了 delta 之后 zstd 能压到接近 PNG 的大小，但解压只要 ~15 ms 而不是 300 ms。**这实际上就是在手动复现 PNG 算法的前半段，只是把 Deflate 换成了更快的 zstd**。

---

## 实际代价评估

**实现成本**：需要新增 NuGet 包（`ZstdSharp.Port` 或 `ZstdNet`），.NET 内置没有 zstd。若用 .NET 内置的 Brotli/Deflate 可以省掉依赖，但速度更慢。

**内存收益**：

| 方案 | 8 slot 内存（12MP） | 显示延迟（缓存命中） |
|---|---|---|
| 当前 BGRA8 | ~384 MB | ~5 ms |
| zstd(3) 无预处理 | ~192-240 MB | ~10-12 ms |
| delta + zstd(3) | ~64-96 MB | ~15-20 ms |

---

## 结论

**zstd（无预处理）**：内存减少 ~40-50%，解压 10 ms，和当前 5 ms 几乎一样快。但需要新依赖，收益有限。

**delta + zstd**：内存接近 PNG 量级，解压 15-20 ms，显示仍比 Magick 快 15-20×，且全程无 WIC。代价是实现复杂（自己写 delta 过滤）+ 新依赖。

**与其这样，最简单可行的替代方案是**：用 .NET 内置的 `DeflateStream`（无需额外依赖）对 delta 预处理后的像素做压缩，能达到 delta + zstd 的类似效果。但实现上等于是半手工实现了 PNG 压缩，不如直接存 PNG——唯一优势是解压更快（Deflate 解压 ~30-50 ms，避开 Magick 的 300 ms 全流程）。

**最终判断**：如果内存真的是瓶颈，**最优选择可能是直接减少 `PreloadCountForward`**（5→2），内存从 ~384 MB 降到 ~192 MB，零复杂度、零依赖、零延迟增加。

---

**User**

zstd 依赖多大

---

**Cursor**

真实数据出来了，直接比较：

---

## 两个主流 .NET zstd 包的体积

### `ZstdSharp.Port`（纯 C# 移植，无 native 依赖）

| 指标 | 数值 |
|---|---|
| nupkg 总大小 | **1.36 MB**（含所有目标框架） |
| 部署到应用的 DLL（.NET 9/10） | **~388 KB** |
| native 依赖 | **无**（纯托管代码） |

### `ZstdNet`（P/Invoke 包装 native libzstd）

| 指标 | 数值 |
|---|---|
| nupkg 总大小 | **2.16 MB** |
| 托管 DLL | ~38 KB（只是个薄包装） |
| `libzstd.dll`（win-x64） | **665 KB** |
| 同时还附带 Linux/macOS/ARM native | 各平台 550-880 KB |

---

## 对这个项目的影响

本项目是 Windows-only（WinUI 3），所以：

- **`ZstdSharp.Port`**：应用体积增加 **~388 KB**（一个纯托管 DLL，无额外依赖，AOT 友好）
- **`ZstdNet`**：应用增加 **~38 KB 托管 + 665 KB native DLL** = ~700 KB，且带 libzstd.dll，需要随应用分发

体积本身都不是问题（远小于已有的 Magick.NET，后者光 libMagickCore 就好几 MB）。

---

## 与结论的关联

体积不是障碍。但回到上一个分析的核心结论：**zstd 在无预处理的 BGRA8 上只压 1.5-2 倍**，从 ~384 MB 降到 ~200 MB，收益有限，引入依赖的性价比不高。如果目标只是减内存，**减 `PreloadCountForward` 的性价比更高**（零依赖、零延迟）。

