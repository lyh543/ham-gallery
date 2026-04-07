# GIF 缩略图、Info 面板、FileGuard 防误删机制

> Saved: 2026-04-06

## 对于 gif 图，缩略图也使用 gif 格式

For GIF source files, copy the source file directly as the thumbnail instead of decoding/re-encoding (which would lose animation).

Changes to `ThumbnailService.cs`:

- `GetThumbPath` — returns `{hash}.gif` for `.gif` sources, `{hash}.jpg` for everything else
- `GenerateThumbnailAsync` (new dispatcher) — for GIF files, copies the source as-is (preserving animation); for other formats, uses the decoder pipeline to produce JPEG
- Both `GetOrCreateThumbnailAsync` and `GenerateMissingAsync` delegate to `GenerateThumbnailAsync`

`BitmapImage` in `PhotoItemViewModel` plays animated GIFs natively — no further UI changes needed.

## 在 GIF 的 Info 面板中显示时长、帧数、帧率（时长和帧数同行）

不需要持久化到数据库，在加载时通过 WIC 实时读取。

**PhotoDetailViewModel.cs** — 新增 `InfoGifDuration`、`InfoGifFrames`、`InfoGifFrameRate` 三个 observable 属性。`UpdateInfoPanelFast` 清空它们；`LoadExtendedExifAsync` 对 `.gif` 文件调用 `LoadGifInfoAsync`。`LoadGifInfoAsync` 通过 WIC 遍历每帧的 `/grctlext/Delay`（单位百分之一秒，值为 0 时默认按 10cs 计），累加得总时长，再计算帧率。

**PhotoDetailPage.xaml** — 在 GPS 块之后新增 `GifSection`（StackPanel，默认 Collapsed）：第一行用横向 StackPanel 显示时长与帧数（中间加 `/` 分隔符），第二行单独显示帧率。

**PhotoDetailPage.xaml.cs** — 在属性变更的 `switch` 中接入三个新属性；`GifSection.Visibility` 由 `InfoGifDuration` 变为非 null 时触发（最后赋值，确保所有数据就绪后才显示该 section）。

## 在 GIF 缩略图处添加注释，并修复清除缓存时遗漏 GIF 文件的问题

1. 在 `ThumbnailService.cs` 的 GIF 复制处添加注释，说明直接复制而不重新编解码的原因（重新编码性能差且有损）。
2. `ClearThumbnailCacheAsync` 只删除 `*.jpg` 文件，会遗漏 `.gif` 缩略图。修复方案：在 `DatabaseService` 中添加 `GetAllThumbnailPathsAsync`（返回所有非空 `ThumbPath`），添加 `ClearThumbnailsAsync` 清除数据库缩略图记录，`ClearThumbnailCacheAsync` 改为从数据库读取路径后删除对应文件。

## 对于 GIF 图片，不复制文件，改为在 Thumbnail 表中添加 `thumbnail_disabled` 标志位，删除时不能删除原图，数据库中 `thumbPath` 置空

放弃 GIF 复制方案，改为标志位方案：

- **Thumbnail.cs** — `ThumbPath` 改为 `string?`，新增 `bool ThumbnailDisabled`
- **GalleryDbContext.cs** — `ThumbPath` 标记为可选，`ThumbnailDisabled` 默认值为 `false`
- **ThumbnailService.cs** — GIF 分支写入 `ThumbnailDisabled=true, ThumbPath=null`（不复制文件），快速路径和双重检查锁处理 disabled 情况，`GetThumbPath` 恢复为普通 `.jpg`
- **DatabaseService.cs** — `GetAllThumbnailPathsAsync` 过滤 `WHERE ThumbPath IS NOT NULL AND ThumbPath != ''`，确保清除缓存时不会触及源文件
- **PhotoItemViewModel.cs** — 当 `GetOrCreateThumbnailAsync` 返回 null 且文件为 `.gif` 时，回退到源文件路径，使动态 GIF 仍可在网格中显示

## 为防止误删用户文件，将所有删除文件和移动文件到回收站的操作封装到 helper 中，直接删除只能作用于应用数据目录，移动到回收站可作用于任意文件

创建 `Helpers/FileGuard.cs`，包含三个静态 guard 方法，均在调用 `File.Delete` 前验证路径：

| 方法 | 允许路径 | 用途 |
|------|---------|------|
| `DeleteAppDataFile(path)` | `AppDataPaths.RootDirectory` 下 | 删除缩略图及应用数据文件 |
| `DeleteTempFile(path)` | `Path.GetTempPath()` 下 | EXIF 回写时删除临时文件 |
| `DeleteRecycleBinMetadata(path)` | 包含 `\$Recycle.Bin\` | 还原后清理 `$I*` 元数据 |

路径通过 `Path.GetFullPath` 规范化以防止 `..` 路径穿越攻击，验证失败时抛出 `InvalidOperationException`。

迁移所有 `File.Delete` 调用点：
- **SettingsViewModel.cs** — `ClearThumbnailCacheAsync` 和 `ClearAllDataAsync` 改用 `FileGuard.DeleteAppDataFile`
- **RecycleBinHelper.cs** — `$I` 元数据删除改用 `FileGuard.DeleteRecycleBinMetadata`
- **PhotoDetailViewModel.cs** — 临时文件删除改用 `FileGuard.DeleteTempFile`

`RecycleBinHelper.MoveToRecycleBinAsync`（用户主动删除照片）设计上不受限制。

## WriteExifOrientationAsync 告诉我这个函数在做什么

`WriteExifOrientationAsync` rewrites the EXIF Orientation tag in-place without re-compressing pixels:

1. Opens source file via `BitmapDecoder` (WIC)
2. Creates transcoding encoder (`BitmapEncoder.CreateForTranscodingAsync`) — no pixel re-encoding
3. Sets `System.Photo.Orientation` property (EXIF tag 0x0112) via `BitmapPropertySet`
4. Flushes to `InMemoryRandomAccessStream` (original file not yet touched)
5. Overwrites original file with `File.Create(filePath)` + `CopyToAsync`
6. On error: deletes `tempPath` (defensive, was never actually written to in the original implementation)

Potential issue: `File.Create(filePath)` truncates first, so a crash mid-write would destroy the file.

## 修复 WriteExifOrientationAsync 的原子写问题

**Why write-to-temp-then-move is safer:**

`File.Create(filePath)` truncates the file to 0 bytes before writing. If the process crashes mid-write, the photo is gone. Write to a temp file first, then `File.Move(..., overwrite: true)` — on the same volume, NTFS rename is atomic: either the original is fully replaced or it remains untouched.

**Fix — two line change:**
```csharp
// Before: overwrites directly (truncate-then-write)
await using var dstStream = File.Create(filePath);
await memRas.AsStreamForRead().CopyToAsync(dstStream, ct);

// After: write to temp, then atomic replace
await using (var dstStream = File.Create(tempPath))
    await memRas.AsStreamForRead().CopyToAsync(dstStream, ct);
File.Move(tempPath, filePath, overwrite: true);
```

Success path: `tempPath` is consumed by `File.Move` (no leftover). Failure path: `catch` block calls `FileGuard.DeleteTempFile(tempPath)`, original file untouched.

## WriteExifOrientationAsync 这个函数有什么用？

Called when the user clicks the **rotate button** in the photo detail page. Flow (`PhotoDetailViewModel.cs`):
1. Compute `newOrientation` from current EXIF value + rotation direction (lookup table)
2. Call `WriteExifOrientationAsync` to persist the new value into the file
3. Update `CurrentPhoto.Orientation` in memory
4. Update the database

Effect: rotation is persisted to the file itself so other apps also show the correct orientation. Pixels are not touched — no quality loss.

## DeleteRecycleBinMetadata 这个函数有什么用

Called in `RecycleBinHelper.RestoreFromRecycleBinAsync` to clean up the orphaned `$I*` metadata file after restoring a file from the recycle bin.

Windows recycle bin stores two files per deleted item:

| File | Purpose |
|------|---------|
| `$Rxxxxxx.jpg` | Actual content — moved back by `File.Move` during restore |
| `$Ixxxxxx.jpg` | Metadata (original path, deletion time) — orphaned after `$R` is moved |

Without deleting `$I`, the recycle bin icon still shows content until refreshed. `DeleteRecycleBinMetadata` removes the orphan immediately.

The path guard (must be under `$Recycle.Bin`) prevents accidental deletion of user files if a wrong path is passed.
