# 测试闪退日志检查
_Exported on 4/3/2026 at 17:02:20 GMT+8 from Cursor (2.6.22)_

---

**User**

测试的时候闪退了。检查一下日志为什么闪退了，并修复

---

**Cursor**

终端里看到 `Error -1073741819`，这是 Windows 的 `STATUS_ACCESS_VIOLATION` (0xC0000005)，表示程序崩溃了。让我查看 Windows 事件日志和相关代码来找到原因。

崩溃发生在 `Windows.Graphics.dll` 中，是访问冲突 (0xc0000005)。这通常与图像/缩略图处理有关。让我查看相关代码。

让我查看应用程序的 Serilog 日志，看看崩溃前是否有更多细节。

找到了！日志中有明确的错误：

```
COMException (0x8001010E): The application called an interface that was marshalled for a different thread
```

这是 **跨线程 UI 访问** 错误 (`RPC_E_WRONG_THREAD`)，`LoadExtendedExifAsync` 从后台线程更新了 UI 属性。让我查看 ViewModel：

找到根本原因了！在 `NavigateToIndexAsync` 第 189 行：

```csharp
_ = Task.Run(() => LoadExtendedExifAsync(filePath, ct), ct);
```

这把 `LoadExtendedExifAsync` 放到了**线程池后台线程**运行。但方法内部在 `await Task.Run(() => _exif.ReadExif(...))` 完成后，没有 SynchronizationContext，会继续在线程池线程上执行，然后直接赋值 `InfoColorSpace`、`InfoBitDepth` 等可观测属性，触发 `PropertyChanged` 事件，最终 `PhotoDetailPage.ViewModel_PropertyChanged` 在后台线程上调用了 `TextBlock.set_Text` —— 这是 WinRT COM 跨线程访问，`0x8001010E` = `RPC_E_WRONG_THREAD`。

重复的 COM 跨线程违规会破坏 COM/WinRT 内部状态，最终导致 `Windows.Graphics.dll` 的原生访问冲突崩溃。

方法注释里也写了「*caller must ensure these are marshalled*」，但调用方反而用 `Task.Run` 把它扔到了错误线程。

修复很简单：去掉外层的 `Task.Run`。`LoadExtendedExifAsync` 内部已经用 `await Task.Run(...)` 做了 I/O 卸载，`await` 完成后会自动通过 WinUI 的 `SynchronizationContext` 回到 UI 线程：

同时把方法内那段过时的注释也更新一下，避免误导：

现在验证改动是否正确：

改动没有引入任何 lint 错误。现在来编译验证：

有个正在运行的 FluentGallery 进程占用了 exe，先杀掉它再构建：

构建成功，0 错误 0 警告。

---

**问题总结**

崩溃原因是**跨线程 UI 访问**（`RPC_E_WRONG_THREAD`，0x8001010E）：

在 `NavigateToIndexAsync` 里，`LoadExtendedExifAsync` 被包在 `Task.Run(...)` 里启动，导致它运行在线程池后台线程上。方法内的 `await Task.Run(() => _exif.ReadExif(...))` 完成后，没有 UI `SynchronizationContext`，继续在线程池线程执行，直接设置了 `InfoColorSpace`、`InfoBitDepth` 等可观测属性，触发 `PropertyChanged` 回调，最终 `PhotoDetailPage.ViewModel_PropertyChanged` 在后台线程上调用 `TextBlock.set_Text`，违反了 WinRT COM 的 STA 线程规则。

反复的 COM 跨线程违规破坏了底层状态，最终导致 `Windows.Graphics.dll` 里的原生访问冲突（0xc0000005），即程序闪退。

**修复**：去掉外层 `Task.Run`，直接调用 `_ = LoadExtendedExifAsync(filePath, ct)`。该方法内部已有 `await Task.Run(...)` 做 EXIF I/O 卸载，`await` 完成后会通过 WinUI DispatcherQueue 的 `SynchronizationContext` 自动回到 UI 线程更新属性。

---

**User**

又闪退了，你再看看是为什么

---

**Cursor**

同样的崩溃地址：`Windows.Graphics.dll` offset `0x53cca`，原生访问冲突。从日志来看，崩溃发生在最后 4 条 HEIC 缩略图失败（`0xC00D5212`）之后 2 秒。

**根本原因**：某些 HEIC 文件在调用 `BitmapDecoder.GetPixelDataAsync` 时，不是抛出可被捕获的 `COMException`，而是触发 Windows.Graphics.dll 内部的原生访问冲突（SEH `0xc0000005`）。.NET 的 `try/catch` **无法捕获**原生访问冲突，进程直接终止。

修复方案：在 `ThumbnailService` 里提前过滤 HEIC/HEIF 文件，完全跳过不会产生缩略图的格式（它们本来就始终失败），避免进入崩溃的代码路径。

接下来，`ZoomableImage.LoadImageAsync` 也会在用户点击 HEIC 照片时调用 `BitmapImage.SetSourceAsync`，同样可能触发崩溃。对它也加一层保护：

还需要检查 `ExifService.ReadExif` 有没有同样的 BitmapDecoder 调用：

`ExifService` 用的是纯托管的 `MetadataExtractor`，没问题。

现在验证 ThumbnailService 的改动是否编译通过：

编译成功。确认一下最终的改动：

---

**这次闪退的根本原因**：

这是**第二个独立 bug**，与上次修复的线程问题不同。

某些 HEIC 文件（没有安装 HEVC 解码器）在调用 `BitmapDecoder.GetPixelDataAsync(..., ExifOrientationMode.RespectExifOrientation, ...)` 时，`Windows.Graphics.dll` 内部触发了 SEH 原生访问冲突（`0xc0000005`）。.NET 的 `try/catch` 对 SEH 访问冲突**完全无效**——异常直接杀死进程，所以日志里只能看到崩溃之前成功抛出托管异常（`0xC00D5212`）的那些文件，而真正导致崩溃的那个 HEIC 文件甚至来不及写日志就崩了。

**修复**：在 `ThumbnailService.GetOrCreateThumbnailAsync` 入口处提前过滤 `.heic` / `.heif` 扩展名直接返回 `null`，跳过整个 WIC 代码路径。这些文件本来就无法生成缩略图（codec 未装），只是以前在确认失败的过程中会概率性崩溃。现在它们会直接显示占位符，不再触碰 `Windows.Graphics.dll` 的崩溃路径。

