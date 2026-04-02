# 后台扫描服务实现
_Exported on 4/2/2026 at 17:54:46 GMT+8 from Cursor (2.6.22)_

---

**User**

请阅读 PROMPT.md 以后，逐步实现这个项目。

请按「第十二章·交付顺序」逐步推进，每次只需要完成一步，避免一次生成过多代码导致质量下降。

继续参照 PROMPT.md，实现 6.2 后台扫描服务（ScanService）

---

**Cursor**

现在让我看看项目当前的结构，了解已经完成的部分。

让我查看 GalleryDbContext 和已有的 ViewModel 来全面了解现有实现。

现在我对项目结构有了完整了解。需要创建三个文件来实现 6.2 后台扫描服务：`ExifService.cs`、`ThumbnailService.cs` 和 `ScanService.cs`，并在 `App.xaml.cs` 中注册。

现在更新 `App.xaml.cs`，注册三个新服务并在启动后自动触发扫描。

现在在 `OnLaunched` 中，数据库初始化完成后自动启动后台扫描：

还需要补充对应的 `using` 语句：

现在检查 lint 错误，然后尝试编译验证：

编译报一个错误：`TryEnqueue` 需要 `DispatcherQueueHandler` 委托而非 `System.Action`。修复 `Dispatch` 辅助方法：

编译零错误零警告。

编译通过，所有任务完成。以下是本次实现的详细说明：

---

## 实现总结：6.2 后台扫描服务

### 新增文件

**`Data/ExifService.cs`**
- 使用 `MetadataExtractor` 读取 JPEG/PNG 等格式的元数据
- 提取：图片尺寸（优先 JPEG Header，次选 EXIF SubIFD）、拍摄时间（DateTimeOriginal → DateTime 降级）、相机品牌/型号、EXIF Orientation、GPS 坐标
- 所有异常均被捕获并记录日志，始终返回部分结果而不抛出

**`Data/ThumbnailService.cs`**
- 使用 WIC (`Windows.Graphics.Imaging`) 解码原图、缩放、编码为 JPEG
- 生成尺寸 256×256（保持宽高比，使用 Fant 插值），JPEG 质量 0.80
- 自动尊重 EXIF Orientation（`ExifOrientationMode.RespectExifOrientation`）
- 缩略图路径规则：`{MD5(FilePath)}.jpg` → `%LocalAppData%\FluentGallery\Thumbnails\`
- 先查数据库缓存（比对 `SourceModifiedAt`），一致且文件存在则直接复用

**`Data/ScanService.cs`**
- 启动方式：`StartAsync(settings, dispatcher)` — 先取消旧扫描，再启动新任务
- 枚举阶段：遍历 `ScanDirectories`，过滤支持格式、排除路径，支持递归/非递归
- 处理阶段：`Channel<string>` 有界队列，`max(1, CPU/2)` 个并发 Worker
  - 新文件 → 读 EXIF → 插入 DB → 触发缩略图生成 → 触发 `PhotoDiscovered` 事件
  - 已存在且 `ModifiedAt` 变化 → 更新 EXIF → 更新 DB → 触发 `PhotoUpdated` 事件
  - 未变化 → 跳过
- 清理阶段：扫描结束后调用 `DeleteStalePhotosAsync` 删除磁盘已不存在的记录
- UI 回调：所有事件通过 `DispatcherQueue.TryEnqueue` 投递到 UI 线程
- 全程支持 `CancellationToken`，`StopAsync()` 可干净中止

**`App.xaml.cs` 更新**
- 注册三个新服务（`ExifService`、`ThumbnailService`、`ScanService`）为 Singleton
- `OnLaunched` 中先激活窗口展示 UI，再后台执行 `InitAndScanAsync`（初始化 DB → 加载设置 → 启动扫描），符合"先从数据库展示，再后台扫描"的要求

---

**User**

后台扫描的时候，前段很卡

---

**Cursor**

让我先分析一下当前 `ScanService` 的性能瓶颈。

找到了三个主要性能瓶颈：

1. **每处理一个文件都做一次 DB 查询**（`GetPhotoByPathAsync`）——万张图库 = 万次 SQLite 往返，这是主因
2. **每个文件都触发一次 UI 事件**（`ProgressChanged` + `PhotoDiscovered`）——高频 `TryEnqueue` 把 UI 线程淹没
3. **缩略图无并发上限**——fire-and-forget 导致大量并发 I/O 争磁盘

一次性修复这三个问题。

现在在 `DatabaseService.cs` 末尾添加 `PhotoScanMeta` 记录类型（放在文件末尾 `NowIso` 方法后面）：

现在重写 `ScanService.cs`：

现在让 `GetOrCreateThumbnailAsync` 在生成时使用信号量：

零错误零警告。

全部修复完成，以下是三个性能问题的具体改动：

---

## 修复了什么

### 问题 1：每文件一次 DB 查询（最大瓶颈）→ 改为一次性预取

**旧方案**：每处理一个文件都调用 `GetPhotoByPathAsync` → 1 万张图 = 1 万次 SQLite 往返。

**新方案**：
- `DatabaseService` 新增 `GetAllPhotoMetadataAsync()`，一条 SQL 把全部 `FilePath + Id + ModifiedAt` 加载进 `Dictionary<string, PhotoScanMeta>`
- `ScanService` 扫描开始时调用一次，之后所有"是否已存在/是否变更"判断全在内存里完成，零 DB 往返
- 只有当文件确实发生变化时（少数情况），才按 Id 加载完整 Photo 行做更新

### 问题 2：每文件触发一次 UI 更新 → 节流 + 批量

**ProgressChanged 节流**：`DispatchProgress` 内用 `Environment.TickCount64` 检查，每 250 ms 最多派发一次，完成时强制触发一次。

**Photo 事件批量 flush**：
- `PhotoDiscovered` / `PhotoUpdated` 改为只往 `ConcurrentQueue` 里入队，不直接 dispatch
- 独立的 flush 协程每 500 ms 把队列 Drain 出来，**一次性**通过 `PhotosBatchDiscovered` / `PhotosBatchUpdated` 事件发给 UI
- ViewModels 拿到 List 后可用 `foreach` 批量 `Add`，触发一次布局而非 N 次

### 问题 3：缩略图无并发上限 → SemaphoreSlim(2)

`ThumbnailService` 加了 `SemaphoreSlim(MaxConcurrent=2)`，同时最多 2 个缩略图在生成，不再因为 fire-and-forget 积压大量并发 I/O 占满磁盘带宽。同时在等到信号量后二次检查缓存，避免重复生成。

---

**User**

现在 photos 表有数据了，但是 album 表还没有数据

---

**Cursor**

让我先了解当前 Album 和 Photo 的数据关系，以及扫描服务的逻辑。

原因很清楚：`ScanService` 只把照片写入 `Photos` 表，没有为扫描目录自动创建对应的 `Album` 记录，也没有把 `Photo.AlbumId` 赋值。需要三步修复：

1. `DatabaseService`：新增「按目录路径获取或创建相册」方法
2. `ScanService`：扫描开始时建立 `目录 → AlbumId` 映射，每张照片写入时带上 `AlbumId`
3. 补救：对已入库但 `AlbumId = null` 的孤立照片做一次批量补齐

现在修改 `ScanService.cs`，在扫描开始时建立映射，处理每个文件时带上 `AlbumId`：

更新注释编号并把 `ResolveAlbumId` 传入 worker：

现在修改 `ProcessFileAsync`、`InsertNewPhotoAsync`、`UpdateExistingPhotoAsync` 接收和传递 `albumId`：

零错误。以下是本次变更的完整说明：

---

## 改动总结

### `DatabaseService.cs` 新增两个方法

**`GetOrCreateDirectoryAlbumAsync(string dirPath)`**
- 根据 `DirectoryPath` 查找已有相册，找不到则自动创建
- 相册名取目录末段（如 `C:\Photos\2024旅行` → 名称 `2024旅行`）
- 幂等：重复调用无副作用

**`RepairOrphanAlbumIdsAsync(dirAlbumMap)`**
- 查出所有 `AlbumId = null` 的照片
- 按最长前缀匹配规则，补齐 `AlbumId`
- 用 `ExecuteUpdateAsync` 批量更新，不逐行 SaveChanges

---

### `ScanService.cs` 改动

**扫描开始时（步骤 1）**：为每个配置的扫描目录调用 `GetOrCreateDirectoryAlbumAsync`，建立 `(Dir, AlbumId)` 有序列表（路径越长越靠前，保证最具体的目录优先匹配）。

**处理每个文件**：通过 `ResolveAlbumId(filePath)` 找到所属相册 ID，在 `InsertNewPhotoAsync` / `UpdateExistingPhotoAsync` 里写入 `photo.AlbumId`。

**扫描结束时（步骤 8）**：调用 `RepairOrphanAlbumIdsAsync`，一次性补齐**已经在库里**但 `AlbumId = null` 的历史照片（包括本次修复前扫描进去的那批）。

下次重新运行应用或触发扫描，Albums 表会自动生成记录，所有照片也会正确关联到对应相册。

---

**User**

扫描到图片就应该创建相册

扫描图片、创建相册的时候写一下日志，然后你可以根据日志去 debug。在应用程序的设置也添加一个日志入口

---

**Cursor**

先看看现有的设置页和 ViewModel，再决定如何统一修改。

三个包安装成功。现在配置 App.xaml.cs 启用 Serilog 文件日志：

现在增强 ScanService 的日志（新建/更新/跳过计数器，扫描汇总）：

现在在 `ProcessFileAsync` 里递增计数器，并改善每文件的日志：

把每个文件的 `LogDebug` 改成包含相册信息的更详细输出：

在设置页「缓存与数据」组末尾加入「查看日志」卡片：

`WriteTo.Debug()` 需要单独的 Serilog 包，安装它：

零错误。

全部完成。以下是本次改动的详细说明：

---

## 改动总结

### 日志基础设施（Serilog）

新增 4 个 NuGet 包：`Serilog`、`Serilog.Extensions.Logging`、`Serilog.Sinks.File`、`Serilog.Sinks.Debug`

**`App.xaml.cs`** 中配置 Serilog：
- 日志文件路径：`%LocalAppData%\FluentGallery\logs\app-yyyy-MM-dd.log`（每天滚动一个文件）
- 保留最近 7 天，自动清理旧文件
- EF Core 日志级别提升到 Warning，屏蔽大量 SQL 噪声
- Debug 模式下同时输出到 Visual Studio 输出窗口
- 日志格式：`2026-04-02 14:30:05.123 [INF] FluentGallery.Data.ScanService: 消息内容`

---

### ScanService 日志增强

**扫描开始**：
```
═══ 扫描开始 ═══  目录数: 2  递归: True
  扫描目录: C:\Users\Photos
  扫描目录: C:\Desktop\截图
```

**相册创建/就绪**（来自 DatabaseService）：
```
相册就绪 AlbumId=1  路径: C:\Users\Photos
Created directory album 'Photos' for C:\Users\Photos
```

**每张照片**（Debug 级，不影响性能）：
```
[新增] IMG_001.jpg  AlbumId=1  Id=42
[更新] IMG_002.jpg  AlbumId=1  Id=7
```

**扫描结束汇总**：
```
═══ 扫描完成 ═══  合计: 1024  新增: 15  更新: 3  跳过(未变化): 1006
```

---

### 设置页「应用日志」入口

在「缓存与数据」组末尾新增一个 `SettingsCard`，点击「打开日志目录」按钮后，用 Windows 文件管理器直接打开 `%LocalAppData%\FluentGallery\logs\` 目录，方便查阅当天的 `.log` 文件排查问题。

---

**User**

相册目录不应该传入的扫描目录，而是照片所在的目录。例如 "C:\Users\lyh54\git\github\ham-gallery\data\手机照片\Camera_archived\2018-2022.11.4\-1bb0b2ee55c3f981.jpg" 这张照片应该在 2018-2022.11.4 这个相册里，而不是我添加的 Camera_archived 里面你 

---

**Cursor**

理解了——相册应该对应**照片所在的直接父目录**，而不是扫描根目录。扫描目录只是告诉扫描器去哪里找文件，不决定相册结构。

需要改动两处：`DatabaseService.RepairOrphanAlbumIdsAsync`（改成从照片路径自动推导目录）和 `ScanService`（移除按扫描目录建相册的逻辑，改为按每张照片的父目录懒建相册）。

现在看 ScanService 中需要改的部分：

清楚了。先改 `DatabaseService`，再改 `ScanService`。

**DatabaseService** — 把 `RepairOrphanAlbumIdsAsync` 改成无参数版，直接从每张照片路径的父目录推导相册：

**ScanService** — 移除「按扫描目录建相册」逻辑，改为每张照片按其父目录懒建相册，加并发安全缓存：

现在替换 `RunScanAsync` 中步骤 1（建 dirAlbumMap）和步骤 8（调用旧 Repair）：

现在改 `ProcessFileAsync`（移除 `resolveAlbumId` 参数，改为内部调用新方法）：

现在在 `FlushPendingPhotos` 之前（在「Dispatch helper」区域之前）添加新的 album 解析方法：

零错误。改动说明：

---

## 改动总结

### 核心逻辑变化

**之前（错误）**：扫描开始时，为每个**扫描根目录**创建一个相册，所有子目录的照片都归入这个相册。

**现在（正确）**：相册 = 照片的**直接父目录**。

| 照片路径 | 之前归入相册 | 现在归入相册 |
|---|---|---|
| `Camera_archived/2018-2022.11.4/photo.jpg` | `Camera_archived` | `2018-2022.11.4` |
| `Camera_archived/2023/photo.jpg` | `Camera_archived` | `2023` |
| `Camera_archived/photo.jpg` | `Camera_archived` | `Camera_archived` |

---

### 实现细节

**`ScanService` — 懒建相册 + 线程安全缓存**

新增 `GetOrCreateAlbumForFileAsync(filePath)` 方法：
- 取 `Path.GetDirectoryName(filePath)` 作为相册目录
- 先查 `ConcurrentDictionary<string, long> _albumIdCache`（快路径，零锁）
- 未命中时用 `SemaphoreSlim(1)` 串行化数据库写入（防止多 worker 同时创建同一目录的相册）
- 将结果写入缓存，后续该目录的照片全部复用

扫描目录（`settings.ScanDirectories`）不再预建相册，仅用于枚举文件。

**`DatabaseService.RepairOrphanAlbumIdsAsync`** — 无需传参：
- 自己查出所有 `AlbumId = null` 的照片
- 按 `Path.GetDirectoryName(FilePath)` 分组
- 为每个唯一父目录调用 `GetOrCreateDirectoryAlbumAsync`
- 批量 `ExecuteUpdateAsync` 补齐 `AlbumId`

下次启动扫描，已入库的老照片（`AlbumId = null`）会在步骤 8 一次性修复到正确的相册。

---

**User**

扫描到图片以后，相册列表的统计数量并没有增加，仍然是0张图片

---

**Cursor**

让我看一下 `AlbumListViewModel` 和相关的 View，了解相册列表如何展示和更新。

问题根源很清楚：

1. `AlbumListViewModel` 没有订阅 `ScanService` 的 `PhotosBatchDiscovered` 事件
2. 扫描新增照片/相册时，UI 完全不知道

修复方案：
- `AlbumListViewModel` 改为 **Singleton**（整个 App 生命周期只有一个实例）
- 注入 `ScanService`，订阅扫描事件
- 收到批量新照片时：按 `AlbumId` 分组 → 更新已有相册的 `PhotoCount`，或将新相册从 DB 加载并插入列表

把 `AlbumListViewModel` 从 `Transient` 改为 `Singleton`：

零错误。改动说明：

---

## 根本原因

`ScanService` 扫描到新照片时会触发 `PhotosBatchDiscovered` 事件，但 `AlbumListViewModel` 从未订阅这个事件。每次页面加载时只做一次 `LoadAsync()`，扫描过程中发现的新照片/新相册不会推送到 UI。

---

## 修复内容

### `AlbumListViewModel` 

**注入 `ScanService`**，在构造函数里订阅两个扫描事件：

```
PhotosBatchDiscovered → OnPhotosBatchDiscovered
PhotosBatchUpdated    → OnPhotosBatchUpdated（元数据更新，相册数量不变，空处理即可）
```

**`OnPhotosBatchDiscovered` 逻辑**（在 UI 线程上执行）：
- 按 `AlbumId` 分组
- 若相册已在 `Albums` 列表中 → `PhotoCount += 本批数量`（直接更新，触发绑定刷新）
- 若相册还不在列表中（本次扫描刚创建的） → 从 DB 加载该相册 → 按当前排序规则插入到正确位置

实现 `IDisposable`，取消事件订阅（防止内存泄漏）。

### `App.xaml.cs`

`AlbumListViewModel` 改为 `Singleton`：
- 整个 App 生命周期只有一个实例
- 事件订阅只发生一次，无重复订阅问题
- 用户在相册页和其他页之间来回导航时，`Albums` 集合内容保持一致，不会因重建 ViewModel 而丢失扫描期间增量更新的数据

