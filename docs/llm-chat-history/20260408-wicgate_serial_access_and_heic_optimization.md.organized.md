# WicGate 串行访问与 HEIC 加载优化

> Saved: 2026-04-08

## WicGate 限制了串行访问 WIC，HEIC 预加载和加载过程很慢，会不会是因为 HEIC 读取转 PNG 需要多次访问 WIC？能否优化？不要改代码

### HEIC 的 WIC 访问次数分析

由于 `WicImageDecoder` 对 HEIC 的 `SupportsConcurrentDecode` 返回 `false`，而 `PreloadAsync` 和 `LoadAsync` 都用 `concurrentSafe: true` 调用 pipeline，所以 WIC HEIC codec 完全被跳过，实际解码永远走 `MagickImageDecoder`（libheif）。

**情景 A：预加载（`PreloadInternalAsync`）**

```
MagickImageDecoder.DecodeAsync()  ← 不走 WIC，可并发，无 WicGate
     ↓ (BGRA8 像素)
WicGate.WaitAsync(Low)            ← ① 第 1 次 WicGate 占用
BitmapEncoder → PNG bytes         ←   编码 BGRA8 → PNG
WicGate.Release()
```

每张预加载图：**1 次 WicGate 占用**，且为 Low 优先级 → 所有预加载任务排队串行。

**情景 B：显示（`LoadAsync`）—— 缓存未命中**

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

**情景 C：显示（`LoadAsync`）—— 缓存命中**

```
_pngCache 命中               ← 直接拿 PNG bytes，跳过解码和编码
     ↓
WicGate.WaitAsync(High)      ← ① 第 1 次 WicGate 占用
BitmapDecoder(PNG) → SoftwareBitmap
WicGate.Release()
```

缓存命中时：**仍需 1 次 WicGate**。

### 为什么慢

1. **预加载的串行瓶颈**：默认预加载 7 张，每张都需要一次 `WicGate(Low)` 做 PNG 编码，完全串行，合计可能需要几秒。
2. **PNG 中间格式本身是额外开销**：选择"缓存 PNG bytes"策略导致编码 + 解码各走一次 WIC。如果用 `SoftwareBitmap.CreateCopyFromBuffer(buffer, Bgra8, w, h)` 直接从 BGRA8 像素创建 `SoftwareBitmap`，则显示时完全无需 WIC。
3. **预加载速度跟不上翻页速度**：低优先级编码任务正在 flush 时，高优先级请求仍需等待其完成。

| 路径 | WicGate 次数 | 说明 |
|---|---|---|
| 预加载 1 张 | 1 次 (Low) | PNG 编码；N 张完全串行 |
| 显示（缓存未命中）| 2 次 (High) | PNG 编码 + PNG 解码 |
| 显示（缓存命中）| 1 次 (High) | 仅 PNG 解码为 SoftwareBitmap |

**根本原因**：PNG 作为中间格式导致"编码 → 缓存 → 解码"两步都走 WIC，且预加载阶段全部串行。优化方向：让"BGRA8 像素 → SoftwareBitmap"不经过 WIC（`SoftwareBitmap.CreateCopyFromBuffer` 可做到），或直接缓存 `DecodedImageData`，省掉 PNG 编码这次 WicGate。

---

## 试试让"BGRA8 像素 → SoftwareBitmap"这条路不经过 WIC（SoftwareBitmap.CreateCopyFromBuffer 可做到）

实现完成，构建成功，0 错误 0 警告。

### 改动总结

**`HeicImageLoader.cs` — 核心重写**

- **缓存格式**：`Dictionary<string, byte[]>`（PNG bytes）→ `Dictionary<string, DecodedImageData>`（BGRA8 原始像素）
- **`PreloadInternalAsync`**：之前 Magick decode → WicGate(Low) + BitmapEncoder → PNG bytes；现在 Magick decode → 直接存入缓存，**无 WicGate，全部并行**
- **`LoadAsync`**：之前 WicGate(High) encode + WicGate(High) decode 共 2 次串行；现在 `SoftwareBitmap.CreateCopyFromBuffer` + `SoftwareBitmap.Convert`，**0 次 WicGate**
- **删除了** `EncodeToPngBytesAsync`（彻底不再需要）

**`HeicPreloadTests.cs`**：删除测试 7、8（引用已删方法），修正测试 4 中过时的注释

**`FluentGallery.Tests.csproj`**：补充 `WicGate.cs` 编译引用；更新注释

**内存代价**：每个缓存 slot 从 ~7 MB（PNG）升为 ~48 MB（BGRA8 12MP），默认 8 slot 约 ~384 MB。如果内存紧张，可在设置中减小预加载张数，`MaxCacheSize` 由 `ApplyPreloadCount` 自动跟随。

---

## 如果使用 Magick.NET 能够降低内存使用的同时，不调用 WIC，且加载速度不下降太多吗

### 三种方案对比

|  | **方案 A（旧）WIC PNG 缓存** | **方案 B（当前）BGRA8 缓存** | **方案 C Magick 压缩缓存** |
|--|--|--|--|
| 缓存格式 | PNG bytes（~7 MB/张） | BGRA8 pixels（~48 MB/张） | WebP/JPEG bytes（~2-7 MB/张） |
| 8 slot 总内存（12MP） | ~56 MB | ~384 MB | ~16-56 MB |
| WIC 次数/预加载 | 1 次 | **0 次** | **0 次** |
| WIC 次数/显示 | 1 次 | **0 次** | **0 次** |
| 缓存命中显示耗时 | WicGate 等待 + WIC decode | **~5 ms**（CreateCopyFromBuffer） | Magick decode：~100-500 ms |

### 方案 C 的关键问题：缓存命中时不再"即时"

```
旧 WIC 方案：缓存命中 → WicGate 等待（可能数百 ms）→ BitmapDecoder → 显示
当前 BGRA8：缓存命中 → CreateCopyFromBuffer（~5 ms）→ 显示   ← 最快
方案 C：    缓存命中 → Magick decode（JPEG ~150 ms / WebP ~200 ms）→ CreateCopyFromBuffer → 显示
```

| 格式 | 压缩后大小（12MP） | 编码耗时 | 解码耗时 | 是否有损 |
|--|--|--|--|--|
| JPEG Q85 | ~2-4 MB | ~100 ms | ~150 ms | 有损 |
| WebP lossy Q80 | ~2-3 MB | ~200 ms | ~150 ms | 有损 |
| WebP lossless | ~5-8 MB | ~2-4 s | ~300 ms | 无损 |
| PNG（Magick） | ~7 MB | ~400 ms | ~300 ms | 无损 |

**核心矛盾**：压缩越小 → 显示时必须解压 → 显示延迟增加。Magick.NET 确实能做到"内存省且无 WIC"，但"加载速度不下降太多"很难同时满足——任何压缩格式都意味着缓存命中时多出一次解码，而当前方案 B 的缓存命中就是内存拷贝。

如果内存是真实问题，最务实的优化是**减小预加载窗口**（`PreloadCountForward` 从 5 降到 3），内存从 ~384 MB 降到 ~240 MB，显示速度完全不变。

---

## 如果使用 zstd 这类通用算法进行压缩呢

zstd 是 LZ 系算法，靠数据中的重复字节序列来压缩。自然照片 BGRA8 像素连续变化，LZ 可匹配的重复极少，压缩率非常有限：

| 算法 | 12MP BGRA8（48 MB）→ | 压缩率 | 解压速度 |
|---|---|---|---|
| 原始 BGRA8（当前） | 48 MB | 1× | ~5 ms（memcpy） |
| LZ4（极快）| ~36-40 MB | 1.2-1.4× | ~5-8 ms |
| zstd level 1 | ~28-35 MB | 1.4-1.7× | ~8-15 ms |
| zstd level 3（默认）| ~24-30 MB | 1.6-2× | ~8-12 ms |
| PNG / WIC（图像编码）| ~6-8 MB | 6-8× | ~100-300 ms |

**zstd 在自然照片 BGRA8 上只能压 1.5-2 倍**，因为照片缺乏 LZ 可利用的重复。PNG 能达到 6-8× 是因为先做了**像素差分滤波**，把像素转换为相邻差值，集中在 0 附近，Deflate 才有大量可压缩的重复。

加了 delta 预处理后 zstd 能压到接近 PNG 的大小，但解压只要 ~15 ms 而不是 300 ms：

| 方案 | 8 slot 内存（12MP） | 显示延迟（缓存命中） |
|---|---|---|
| 当前 BGRA8 | ~384 MB | ~5 ms |
| zstd(3) 无预处理 | ~192-240 MB | ~10-12 ms |
| delta + zstd(3) | ~64-96 MB | ~15-20 ms |

**最终判断**：如果内存真的是瓶颈，**最优选择可能是直接减少 `PreloadCountForward`**（5→2），内存从 ~384 MB 降到 ~192 MB，零复杂度、零依赖、零延迟增加。

---

## zstd 依赖多大

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

体积本身都不是问题（远小于已有的 Magick.NET，后者光 libMagickCore 就好几 MB）。但回到核心结论：**zstd 在无预处理的 BGRA8 上只压 1.5-2 倍**，从 ~384 MB 降到 ~200 MB，收益有限，引入依赖的性价比不高。如果目标只是减内存，**减 `PreloadCountForward` 的性价比更高**（零依赖、零延迟）。
