# Fluent Gallery — LLM 开发提示词

## 角色定义

你是一名精通 Windows 桌面应用开发的高级工程师，擅长 WinUI 3、Windows App SDK、C#、MVVM 架构、SQLite 数据库、图像处理和性能优化。请根据以下规范，从零开始开发一个 Windows 平台的现代相册应用。

---

## 一、项目概述

开发一款适用于 Windows 平台的相册应用，名为 **Fluent Gallery**。
- 风格类似 Windows 11 自带相册，采用 Fluent Design System 设计语言
- 主打**高性能**：利用数据库缓存和缩略图缓存，避免每次打开都全量扫描文件系统
- 对**触屏友好**：支持捏合缩放、滑动手势
- 支持**中文 / 英文**双语

---

## 二、技术栈

| 层级 | 技术选型 | 说明 |
|------|----------|------|
| UI 框架 | WinUI 3 (Windows App SDK 最新稳定版) | 原生 Fluent UI，支持触屏、Mica/Acrylic 材质 |
| 语言 | C# 12+ / .NET 8+ | |
| 架构模式 | MVVM（推荐 CommunityToolkit.Mvvm） | 视图与逻辑解耦 |
| 数据库 | SQLite（通过 Microsoft.Data.Sqlite） | 存储照片元数据、相册信息 |
| ORM | Dapper 或 EF Core（SQLite provider） | |
| 图像解码 | Windows Imaging Component (WIC) + Microsoft.Windows.CsWin32 | 原生高性能解码，支持 HEIC/HEIF |
| EXIF 读取 | ExifLibrary 或 MetadataExtractor | 读取拍摄时间、GPS、相机型号等 |
| 缩略图 | WIC（BitmapDecoder + BitmapTransform） | 生成并缓存缩略图 |
| 自然排序 | StrCmpLogicalW（Shell32 P/Invoke） | 与 Windows Explorer 排序一致 |
| 国际化 | WinUI ResourceDictionary + .resw 文件 | 支持运行时切换语言 |
| 依赖注入 | Microsoft.Extensions.DependencyInjection | |
| 设置持久化 | Windows.Storage.ApplicationData.Current.LocalSettings | |
| 回收站 | Shell32 SHFileOperation / IFileOperation COM | |

> **不要使用 WPF、Electron 或任何非原生框架。**

---

## 三、项目结构

```
FluentGallery/
├── FluentGallery.sln
├── FluentGallery/                        # 主项目（WinUI 3 App）
│   ├── App.xaml / App.xaml.cs
│   ├── Assets/
│   ├── Strings/
│   │   ├── en-US/Resources.resw
│   │   └── zh-CN/Resources.resw
│   ├── Models/                           # 数据模型
│   │   ├── Album.cs
│   │   ├── Photo.cs
│   │   └── AppSettings.cs
│   ├── Data/                             # 数据层
│   │   ├── DatabaseService.cs            # SQLite CRUD
│   │   ├── ThumbnailService.cs           # 缩略图生成与缓存
│   │   ├── ScanService.cs                # 后台目录扫描
│   │   └── ExifService.cs                # EXIF 读取
│   ├── ViewModels/                       # MVVM ViewModel
│   │   ├── AlbumListViewModel.cs
│   │   ├── PhotoListViewModel.cs
│   │   ├── PhotoDetailViewModel.cs
│   │   ├── AllPhotosViewModel.cs
│   │   └── SettingsViewModel.cs
│   ├── Views/                            # 页面
│   │   ├── AlbumListPage.xaml
│   │   ├── PhotoListPage.xaml
│   │   ├── PhotoDetailPage.xaml
│   │   ├── AllPhotosPage.xaml
│   │   └── SettingsPage.xaml
│   ├── Controls/                         # 自定义控件
│   │   ├── ThumbnailGridView.xaml        # 通用照片网格控件
│   │   ├── PhotoCropControl.xaml         # 裁剪控件
│   │   └── ZoomableImage.xaml            # 支持触屏缩放的图片控件
│   ├── Converters/                       # 值转换器
│   ├── Helpers/
│   │   ├── NaturalSortHelper.cs
│   │   ├── RecycleBinHelper.cs
│   │   └── LocalizationHelper.cs
│   └── MainWindow.xaml / MainWindow.xaml.cs
└── FluentGallery.Tests/                  # 单元测试
```

---

## 四、数据库 Schema

```sql
-- 照片表
CREATE TABLE IF NOT EXISTS Photos (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    FilePath    TEXT    NOT NULL UNIQUE,
    FileName    TEXT    NOT NULL,
    FileSize    INTEGER NOT NULL,
    Width       INTEGER,
    Height      INTEGER,
    TakenAt     TEXT,           -- EXIF 拍摄时间，ISO 8601
    CreatedAt   TEXT    NOT NULL,
    ModifiedAt  TEXT    NOT NULL,  -- 文件系统修改时间，用于判断是否需要更新
    Latitude    REAL,
    Longitude   REAL,
    CameraModel TEXT,
    CameraMake  TEXT,
    Orientation INTEGER,        -- EXIF Orientation
    AlbumId     INTEGER REFERENCES Albums(Id) ON DELETE SET NULL,
    IsPinned    INTEGER DEFAULT 0
);

-- 相册表
CREATE TABLE IF NOT EXISTS Albums (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT    NOT NULL,
    CoverPath   TEXT,           -- 封面照片路径
    DirectoryPath TEXT,         -- 对应的文件系统目录（可为空，表示手动相册）
    CreatedAt   TEXT    NOT NULL,
    ModifiedAt  TEXT    NOT NULL,
    IsPinned    INTEGER DEFAULT 0,
    SortOrder   INTEGER DEFAULT 0
);

-- 设置（Key-Value 补充，复杂结构用 JSON 存储）
CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);

-- 缩略图路径表（与 Photos 分离，避免主表过大）
CREATE TABLE IF NOT EXISTS Thumbnails (
    PhotoId       INTEGER PRIMARY KEY REFERENCES Photos(Id) ON DELETE CASCADE,
    ThumbPath     TEXT    NOT NULL,
    GeneratedAt   TEXT    NOT NULL,
    SourceModifiedAt TEXT  NOT NULL   -- 生成缩略图时的源文件修改时间
);

-- 索引
CREATE INDEX IF NOT EXISTS idx_photos_album ON Photos(AlbumId);
CREATE INDEX IF NOT EXISTS idx_photos_takenAt ON Photos(TakenAt);
CREATE INDEX IF NOT EXISTS idx_photos_modifiedAt ON Photos(ModifiedAt);
```

---

## 五、页面规范

### 5.1 主窗口布局（MainWindow）

- 使用 `NavigationView`（左侧导航），启用 `Mica` 背景材质
- 导航项（固定顺序）：
  1. **相册** （`SymbolIcon: Album`）— 对应 AlbumListPage
  2. 动态钉住的相册目录（可折叠/展开，支持用户在相册页固定）
  3. **所有照片** （`SymbolIcon: Pictures`）— 对应 AllPhotosPage
  4. 底部：**设置** （`SymbolIcon: Setting`）— 对应 SettingsPage
- 顶部显示当前页面标题，支持后退按钮（Frame 导航）
- 窗口最小尺寸：`800 × 600`

---

### 5.2 相册列表页（AlbumListPage）

**功能：**
- 以网格形式展示所有相册，每格显示：
  - 相册封面（取相册中第一张或最新照片的缩略图）
  - 相册名称
  - 照片数量
  - 最近修改时间
- 右键菜单 / 悬浮操作按钮：
  - 重命名（内联编辑，按 Enter 确认，按 Esc 取消）
  - 删除（弹出 `ContentDialog` 确认，仅删除相册记录，不删除文件）
  - 固定到导航栏 / 取消固定
- 顶部工具栏：
  - 「新建相册」按钮 → 弹出 `ContentDialog`，输入名称后创建
  - 排序下拉菜单：**名称 / 创建时间 / 修改时间 / 照片数量 / 最新拍摄时间**，支持升序/降序
  - 视图切换（大图 / 小图网格）
- 点击相册 → 导航到 PhotoListPage，传递 `AlbumId`

---

### 5.3 照片列表页（PhotoListPage）

**功能：**
- 标题显示相册名称，支持内联重命名
- 网格展示该相册内的照片缩略图
  - 缩略图懒加载，使用 `VirtualizingStackPanel` 或 `ItemsRepeater` + 虚拟化
- 多选模式：长按或右键进入，支持全选
- 操作：
  - 「添加照片」→ 打开 `FileOpenPicker`，支持多选，支持格式见第七节
  - 「删除照片」→ 根据设置决定是否弹出确认，调用系统 API 移入回收站
  - 「移动到相册」→ 子菜单选择目标相册
- 排序下拉菜单：**名称 / 大小 / 创建时间 / 修改时间 / 拍摄时间 / 原生排序（StrCmpLogicalW）**，支持升序/降序
- 触屏手势：双指捏合/展开调整每行照片列数（范围 2–8 列）
- 点击单张照片 → 导航到 PhotoDetailPage，传递当前相册的有序照片列表索引

---

### 5.4 照片详情页（PhotoDetailPage）

**功能：**

#### 基础浏览
- 全屏沉浸式展示当前照片
- 支持左右滑动（触屏）或键盘左右方向键切换上一张/下一张
- 顶部/底部工具栏在鼠标移动或触摸时自动显示，3 秒后淡出
- 进入/退出全屏按钮（`ApplicationView.TryEnterFullScreenMode`）
- 底部缩略图条（FilmStrip），点击快速跳转

#### 缩放与平移
- 使用自定义 `ZoomableImage` 控件，基于 `ScrollViewer` + `Canvas`
- 双击放大至 100%，再次双击还原
- 触屏：捏合缩放、双指平移，手势流畅不卡顿
- 鼠标：`Ctrl + 滚轮` 缩放，拖拽平移
- 缩放范围：10%–1000%

#### 渐进式加载（超大图优化）
- 首先解码到屏幕分辨率的下采样图（BitmapDecoder + BitmapTransform.ScaledWidth/Height）快速显示
- 当用户放大超过 100% 时，异步加载对应区域的原始分辨率瓦片（使用 BitmapDecoder.GetPixelDataAsync 局部解码）
- 所有图像操作在后台线程执行，UI 线程只负责将 SoftwareBitmap 转为 ImageSource
- 保证 UI 线程帧率 ≥ 60fps，不得出现 ANR

#### 照片信息面板
- 右侧可折叠面板（侧边抽屉），展示：
  - 文件名、路径、文件大小
  - 分辨率（宽 × 高）
  - 拍摄时间（EXIF DateTimeOriginal）
  - 相机品牌 & 型号（EXIF Make / Model）
  - 镜头信息（EXIF LensModel）
  - 光圈、快门速度、ISO、焦距
  - GPS 坐标（若有），显示地图缩略图（使用 Bing Maps 静态图 API）
  - 颜色空间、位深、方向

#### 简单编辑
- **旋转**：顺时针 / 逆时针旋转 90°（无损旋转，更新 EXIF Orientation 并重写文件）
- **裁剪**：自定义 `PhotoCropControl`，支持自由比例和常见比例（1:1、4:3、16:9），确认后裁剪并覆盖原文件（保留原文件备份到 `AppData`）
- **复杂编辑**：「在外部应用中编辑」按钮，调用 `Launcher.LaunchFileAsync` 打开系统关联应用

#### 删除
- 调用 `IFileOperation` COM 接口将文件移入回收站（而非直接删除）
- 根据设置决定是否显示确认对话框
- 删除后自动跳转到下一张或返回列表

#### 预加载
- 当前图片加载完成后，后台预加载前 2 张 + 后 2 张的原图（可配置），存入内存缓存（LRU，最大缓存 `N` 张，`N` 可在设置中配置）

---

### 5.5 所有照片页（AllPhotosPage）

**功能：**
- 展示数据库中所有照片，按时间线分组（年/月，显示分组标题）
- 同 PhotoListPage 的缩略图网格 + 虚拟化，复用 `ThumbnailGridView` 控件
- 支持多选、删除、移动到相册
- 顶部搜索框：按文件名模糊搜索，按拍摄时间范围筛选
- 排序同 PhotoListPage
- 点击照片 → PhotoDetailPage，传递当前视图中的有序照片列表

---

### 5.6 设置页（SettingsPage）

使用 `SettingsCard`（WinUI 社区工具包）分组展示以下设置：

#### 扫描目录
- 多选目录列表（`FolderPicker`），每行显示路径 + 删除按钮
- 是否递归扫描子目录（`ToggleSwitch`）
- 排除文件夹列表（同上）

#### 外观
- 语言选择（`ComboBox`）：English / 中文（简体）；选择后立即生效，无需重启
- 主题：跟随系统 / 浅色 / 深色

#### 行为
- 删除照片前是否显示确认对话框（`ToggleSwitch`）
- 预加载张数（`Slider`，范围 1–5，默认 2）

#### 缓存与数据
- 缩略图缓存大小（只读显示）
- 「清除缩略图缓存」按钮（确认后删除缓存目录中的文件，保留数据库记录中的路径直到下次生成）
- 「清除数据库缓存」按钮（确认后清空 `Photos`、`Thumbnails` 表，保留相册结构）
- 「清除全部数据」按钮（确认后删除整个 `AppData` 下的应用数据，恢复出厂状态）

#### 关于
- 应用版本、开源仓库链接、第三方许可证

---

## 六、亮点功能详细规范

### 6.1 缩略图缓存机制

```
缩略图目录：%LocalAppData%\FluentGallery\Thumbnails\
命名规则：{MD5(FilePath)}.jpg（避免路径非法字符）

生成逻辑：
1. 查询 Thumbnails 表，获取 SourceModifiedAt
2. 对比文件系统的 LastWriteTime
3. 若一致 → 直接使用缓存路径
4. 若不一致或不存在 → 后台线程生成，写入磁盘，更新数据库
5. 生成尺寸：512×512（保持宽高比，不拉伸），JPEG 质量 80
6. 使用 WIC BitmapDecoder 解码，BitmapTransform 缩放，BitmapEncoder 编码到磁盘
```

### 6.2 后台扫描服务（ScanService）

```
启动时机：应用启动后，首先从数据库加载数据展示 UI，然后启动后台扫描

扫描流程：
1. 读取设置中的扫描目录列表（含递归与排除规则）
2. 枚举所有满足格式的文件（.jpg/.jpeg/.png/.bmp/.heic/.heif/.webp/.gif）
3. 对每个文件：
   a. 查询数据库中该路径的记录
   b. 若不存在 → 读取 EXIF，插入数据库，加入待生成缩略图队列
   c. 若存在且 ModifiedAt 一致 → 跳过
   d. 若存在且 ModifiedAt 不一致 → 重新读取 EXIF，更新数据库记录，加入缩略图更新队列
4. 扫描结束后，清理数据库中已不存在于磁盘的记录
5. 全程使用 CancellationToken，支持应用退出时中止
6. 使用 Channel<T> 或 ActionBlock 实现生产者-消费者，控制并发度（建议 CPU 核数的一半）

UI 更新：
- 扫描过程中通过 DispatcherQueue 增量更新 ViewModel 的 ObservableCollection
- 状态栏显示「正在扫描... X 张已处理 / Y 张发现」
```

### 6.3 从 Windows Explorer 打开（CommandLine 激活）

```
注册文件关联：在 Package.appxmanifest 中声明对 .jpg/.png/.bmp/.heic/.heif 的文件关联

处理逻辑（App.OnFileActivated）：
1. 获取激活的文件路径
2. 获取该文件所在目录
3. 枚举目录下所有支持格式的文件
4. 使用 StrCmpLogicalW 按文件名自然排序（与 Explorer 默认排序一致）
5. 直接导航到 PhotoDetailPage，传入有序列表和当前文件索引
6. 同时在后台触发完整扫描流程（或仅扫描该目录）

注意：此模式下不强制要求将该目录加入"扫描目录"，仅作临时浏览，不污染数据库
```

### 6.4 导航栏固定目录

```
数据结构：Albums.IsPinned = 1，Albums.SortOrder 控制顺序
NavigationView 的 MenuItems 动态生成：
- 固定项在普通相册之后、「所有照片」之前
- 支持拖拽排序（使用 NavigationView 的 ReorderItems 或自定义实现）
- 右键固定项 → 取消固定
```

### 6.5 自然排序（StrCmpLogicalW）

```csharp
// Helpers/NaturalSortHelper.cs
[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
private static extern int StrCmpLogicalW(string x, string y);

public static IEnumerable<Photo> SortNatural(IEnumerable<Photo> photos)
    => photos.OrderBy(p => p.FileName, Comparer<string>.Create(StrCmpLogicalW));
```

### 6.6 触屏手势

| 页面 | 手势 | 行为 |
|------|------|------|
| 照片详情 | 捏合 / 展开 | 缩放图片 |
| 照片详情 | 单指滑动（缩放比 = 1 时） | 切换上一张/下一张 |
| 照片详情 | 单指双击 | 放大至 100% / 还原 |
| 列表页 | 双指捏合 / 展开 | 减少 / 增加每行列数 |

使用 `ManipulationStarted`、`ManipulationDelta`、`ManipulationCompleted` 事件实现，或使用 `GestureRecognizer` API。

---

## 七、支持的图片格式

| 格式 | 扩展名 | 解码方式 |
|------|--------|----------|
| JPEG | .jpg, .jpeg | WIC 内置 |
| PNG | .png | WIC 内置 |
| BMP | .bmp | WIC 内置 |
| GIF | .gif | WIC 内置（静态显示第一帧） |
| WebP | .webp | WIC 内置（Windows 10 1903+） |
| HEIC/HEIF | .heic, .heif | 需安装「HEVC 视频扩展」，通过 WIC 解码；若未安装，显示格式不支持提示，并提供 Store 跳转链接 |

---

## 八、多语言（国际化）规范

- 所有 UI 文本通过 `ResourceLoader.GetForCurrentView().GetString("Key")` 获取
- **不得**在 XAML 或 C# 中硬编码任何用户可见字符串
- 资源文件路径：
  - `Strings/en-US/Resources.resw`（默认，英文）
  - `Strings/zh-CN/Resources.resw`（简体中文）
- 切换语言时：
  1. 将语言选项写入 `ApplicationData.LocalSettings`
  2. 重新创建 `ResourceLoader` 或使用自定义 `LocalizationHelper` 更新所有绑定
  3. 若框架支持，无需重启即可生效；否则提示重启应用
- Key 命名规范：`PageName_ControlName_Property`，例如 `AlbumList_CreateButton_Text`

---

## 九、性能要求

| 指标 | 要求 |
|------|------|
| 首屏展示时间 | 从数据库加载并渲染 100 张缩略图 ≤ 1 秒 |
| 缩略图帧率 | 滚动列表时 ≥ 60fps，使用虚拟化 + 异步加载 |
| 详情页切换 | 相邻照片（已预加载）切换 ≤ 100ms |
| UI 线程占用 | 所有 IO、图像解码、数据库操作均在后台线程，UI 线程无阻塞 |
| 内存上限 | 图片内存缓存 LRU，默认上限 512MB，可在设置中调整 |
| 数据库查询 | 相册/照片列表查询 ≤ 50ms（1 万张照片规模） |

---

## 十、错误处理与边界情况

- 文件不存在时（已被外部删除）：显示占位图，提示「文件已移动或删除」，提供从列表移除的选项
- HEIC 解码失败（未安装扩展）：显示格式提示卡片，按钮跳转至 Microsoft Store
- 权限不足（受保护目录）：跳过该文件，在扫描日志中记录，设置页提示
- 数据库迁移：使用 Schema 版本号（`PRAGMA user_version`），应用升级时自动执行迁移脚本
- 网络不可用时：地图缩略图显示占位符，不影响其他功能

---

## 十一、代码规范

- 遵循 C# 编码规范（Microsoft 官方风格）
- 所有 `async` 方法必须正确传递 `CancellationToken`
- ViewModel 属性使用 `[ObservableProperty]`（CommunityToolkit.Mvvm 源生成器）
- 使用 `ILogger<T>`（Microsoft.Extensions.Logging）记录日志，输出到文件（`%LocalAppData%\FluentGallery\logs\`）
- 单元测试覆盖：`DatabaseService`、`ScanService`、`NaturalSortHelper`、`ExifService`
- 所有 `IDisposable` 对象正确释放（using 声明）

---

## 十二、交付物

请按以下顺序逐步实现，每步完成后给出可运行的代码：

1. **项目脚手架**：解决方案结构、NuGet 包、依赖注入配置、基础导航框架（空页面）
2. **数据层**：DatabaseService（含建表、迁移）、基础 CRUD
3. **相册列表页**：ViewModel + View，含创建/删除/重命名/排序
4. **照片列表页**：ViewModel + View，含缩略图加载、添加/删除
5. **后台扫描服务**：ScanService + ThumbnailService
6. **照片详情页**：ZoomableImage 控件、EXIF 面板、旋转/裁剪编辑
7. **所有照片页**：时间线分组、搜索
8. **设置页**：所有设置项、语言切换
9. **文件关联激活**：Explorer 打开支持
10. **触屏手势优化**、**国际化**（en-US + zh-CN）
11. **性能调优**：内存缓存 LRU、渐进式加载
12. **错误处理**、**单元测试**、**打包配置**（MSIX）
