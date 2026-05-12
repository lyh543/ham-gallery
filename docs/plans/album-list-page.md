# AlbumListPage 交互增强计划

## 背景

对相册列表页面进行全面交互增强：悬浮信息、带图标的右键菜单、多选批量操作、将"新建相册"改为"添加文件夹"。

---

## 1. 悬浮信息（ToolTip）

鼠标悬浮在相册卡片上时显示信息。

### 修改

**`Views/AlbumListPage.xaml`** — `AlbumCardTemplate` 根 Grid 添加 ToolTip：
```xml
<ToolTipService.ToolTip>
    <StackPanel>
        <TextBlock Text="{x:Bind Name, Mode=OneWay}"/>
        <TextBlock Text="{x:Bind CreatedAtFormatted, Mode=OneWay}" Opacity="0.6"/>
        <TextBlock Text="{x:Bind PhotoCountFormatted, Mode=OneWay}" Opacity="0.6"/>
        <TextBlock Text="{x:Bind TotalSizeFormatted, Mode=OneWay}" Opacity="0.6"/>
    </StackPanel>
</ToolTipService.ToolTip>
```

**`ViewModels/AlbumItemViewModel.cs`**：
- 新增 `TotalSize` 属性（`long`）
- 新增格式化属性 `CreatedAtFormatted`、`PhotoCountFormatted`、`TotalSizeFormatted`
- `TotalSize` 在 cover 加载时同步查询

**`Data/DatabaseService.cs`**：
- 新增 `GetAlbumTotalSizeAsync(long albumId, CancellationToken ct)` — `SELECT SUM(FileSize) FROM Photos WHERE AlbumId = @id`

---

## 2. 右键/长按上下文菜单增强

现有菜单（`AlbumListPage.xaml.cs:ShowContextMenu`）需增加菜单项和图标，底部展示信息。

### 菜单结构（从上到下）

| 菜单项 | 图标 Glyph | 说明 |
|--------|-----------|------|
| 重命名 | `\uE8AC` (Rename) | 已有 |
| 置顶/取消置顶 | `\uE840` (Pin) / `\uE77A` (Unpin) | 已有 |
| 移动 | `\uE8DE` (MoveToFolder) | 子菜单：其它相册目录 + "其它..." |
| 复制 | `\uE8C8` (Copy) | 子菜单：其它相册目录 + "其它..." |
| 在文件管理器中打开 | `\uE838` (OpenLocal) | `SHOpenFolderAndSelectItems` |
| ── 分隔线 ── | | |
| 排除目录 | `\uE738` (RemoveFrom) | 从 DB 删除 + 加入 `ExcludeDirectories` |
| 删除 | `\uE74D` (Delete) | 移到回收站（红色文字）|
| ── 分隔线 ── | | |
| 信息区 | 无 | 灰色文字展示名称、创建时间、数量、大小 |

### 移动/复制子菜单结构

移动和复制使用 `MenuFlyoutSubItem`，列出其它相册的目录作为快捷选项，最后一项为"其它..."打开 FolderPicker：

```
移动 →
  ├─ 相册A目录名 (D:\Photos\Travel)
  ├─ 相册B目录名 (D:\Photos\Family)
  ├─ ── 分隔线 ──
  └─ 其它...
```

- 列出所有有 `DirectoryPath` 的相册（排除当前相册）
- 每项显示相册名，目录路径作为二级文字或 tooltip
- "其它..."打开 `FolderPicker`

### 源目录 = 目标目录的处理

子菜单中已排除当前相册，但用户通过"其它..."选择 FolderPicker 时可能选到相同目录。处理方式：
- 选中后检查目标目录是否与当前相册的 `DirectoryPath` 相同（`StringComparer.OrdinalIgnoreCase`）
- 若相同，显示 toast 提示"源目录与目标目录相同，请重新选择"，不执行操作，不关闭 flyout
- 不从子菜单列表中排除，不弹确认弹窗，仅 toast 拒绝

### 图标设置方式

```csharp
var moveItem = new MenuFlyoutSubItem
{
    Text = L10n.Get("AlbumList_Context_Move"),
    Icon = new FontIcon { Glyph = "\uE8DE" },
};
```

### 长按支持

在 `AlbumGridView` 上处理 `Holding` 事件（触屏长按），复用 `ShowContextMenu`。

### 菜单底部信息

使用 `MenuFlyoutSeparator` + 不可点击的 `MenuFlyoutItem`（`IsEnabled = false`）展示信息：

```csharp
flyout.Items.Add(new MenuFlyoutSeparator());
flyout.Items.Add(new MenuFlyoutItem
{
    Text = $"{vm.Name}\n{vm.PhotoCountFormatted}  ·  {vm.TotalSizeFormatted}",
    IsEnabled = false,
    Foreground = (Brush)App.Current.Resources["TextFillColorSecondaryBrush"],
});
```

### 修改文件

**`Views/AlbumListPage.xaml.cs`** — 重写 `ShowContextMenu` 方法：
- 添加所有新菜单项（带图标）
- 移动/复制使用 `MenuFlyoutSubItem`，动态填充相册列表 + "其它..."
- 底部添加信息区
- 新增 `Holding` 事件处理

**`ViewModels/AlbumListViewModel.cs`**：
- 新增 `ExcludeAlbumsAsync(IReadOnlyList<AlbumItemViewModel> items, CancellationToken ct)`（批量接口）：
  - 加载设置，将每个相册的 `DirectoryPath` 加入 `ExcludeDirectories`，保存设置
  - 批量删除 DB 记录
  - 从 `Albums` 集合中移除
- 相册的移动/复制实际上是移动/复制目录内的所有照片文件，复用照片级别的 `MovePhotosToDirectoryAsync` / `CopyPhotosToDirectoryAsync`
- 新增 `MoveAlbumPhotosAsync(AlbumItemViewModel album, string targetDir, CancellationToken ct)`：
  - 获取相册内所有照片
  - 调用 `MovePhotosToDirectoryAsync` 批量移动文件
  - 更新相册的 `DirectoryPath`
- 新增 `CopyAlbumPhotosAsync(AlbumItemViewModel album, string targetDir, CancellationToken ct)`：
  - 获取相册内所有照片
  - 调用 `CopyPhotosToDirectoryAsync` 批量复制文件
- 新增 `DeleteAlbumsAsync(IReadOnlyList<AlbumItemViewModel> items, CancellationToken ct)`（批量接口）：
  - 对每个相册内的照片：移动文件到回收站 + 删除 DB 记录
  - 删除相册 DB 记录
  - 从 `Albums` 集合中移除
- 现有 `DeleteAlbumAsync` 改为调用批量接口的单条封装
- 新增 `OpenAlbumInExplorer(AlbumItemViewModel vm)` — 调用 `SHOpenFolderAndSelectItems`
- 新增 `GetAlbumDirectoriesAsync(CancellationToken ct)` — 获取所有相册目录列表（供子菜单使用）

---

## 3. 确认弹窗 & 完成 Toast

所有文件操作前显示确认弹窗，完成后显示 toast。

### 确认弹窗格式

| 操作 | 标题 | 内容 | 确认按钮 | 样式 |
|------|------|------|----------|------|
| 删除相册 | 删除相册 | 确认将「{相册名}」目录下的 {N} 张照片移到回收站吗？ | 移到回收站 | Danger |
| 排除目录 | 排除目录 | 确认将「{相册名}」的目录加入排除列表？照片将从库中移除，但文件不会被删除。 | 排除 | Primary |
| 移动相册照片 | 移动照片 | 确认将「{相册名}」目录下的 {N} 张照片移动到「{目标目录名}」吗？ | 移动 | Primary |
| 复制相册照片 | 复制照片 | 确认将「{相册名}」目录下的 {N} 张照片复制到「{目标目录名}」吗？ | 复制 | Primary |
| 批量删除 | 删除相册 | 确认将 {M} 个相册共 {N} 张照片移到回收站吗？ | 移到回收站 | Danger |
| 批量排除 | 排除目录 | 确认将 {M} 个相册的目录加入排除列表？ | 排除 | Primary |
| 批量移动 | 移动照片 | 确认将 {M} 个相册共 {N} 张照片移动到「{目标目录名}」吗？ | 移动 | Primary |
| 批量复制 | 复制照片 | 确认将 {M} 个相册共 {N} 张照片复制到「{目标目录名}」吗？ | 复制 | Primary |

### 完成 Toast

操作成功后显示 toast（复用现有的 `ShowCardSizeToastAsync` 模式）：
- "已移到回收站"
- "已排除 {N} 个目录"
- "已移动 {N} 张照片到 {目标目录名}"
- "已复制 {N} 张照片到 {目标目录名}"

**实现**：通用 toast 方法，接受文本参数，使用 `CardSizeToast` 同样的 overlay 动画。可提取为共享的 `ToastHelper` 或在 page 级别复用现有 toast 方法。

---

## 4. 多选模式

排序按钮右边添加多选切换。

### CommandBar 按钮布局

非多选模式可见：
- 添加文件夹
- 排序
- 多选切换
- 缩放

多选模式可见（`Visibility` 绑定 `IsMultiSelectMode`）：
- 全选
- 删除
- 排除目录
- 移动
- 复制
- 多选切换（保持可见）
- ── 分隔线 ──
- 缩放（保持可见）

### 批量移动/复制的目标选择

与右键菜单一致：使用 `MenuFlyout`（非 SubItem）列出相册目录 + "其它..."：

```
点击"移动"按钮 → 弹出 Flyout：
  ├─ 相册A目录名
  ├─ 相册B目录名
  ├─ ── 分隔线 ──
  └─ 其它...
```

### 修改文件

**`Views/AlbumListPage.xaml`**：
- 在 Sort 按钮后添加 `AppBarToggleButton`（多选）
- 添加批量操作按钮组（全选、删除、排除目录、移动、复制），`Visibility` 绑定 `IsMultiSelectMode`
- 非多选模式的按钮（添加文件夹、排序）添加反向绑定（`IsMultiSelectMode` 为 false 时可见）

**`Views/AlbumListPage.xaml.cs`**：
- `ApplySelectionMode()` 切换 `AlbumGridView.SelectionMode` 在 `None` 和 `Multiple` 之间
- `SelectAll_Click` → `AlbumGridView.SelectAll()`
- 各批量操作 Click 处理：获取 `SelectedItems`，显示确认弹窗，调用 ViewModel 批量接口，显示 toast

**`ViewModels/AlbumListViewModel.cs`**：
- 新增 `[ObservableProperty] IsMultiSelectMode`
- 新增 `[RelayCommand] ToggleMultiSelectMode()`

---

## 5. "添加相册"改为"添加文件夹"

### 修改

**`Views/AlbumListPage.xaml`**：
- 按钮 `x:Uid` 从 `AlbumList_CreateAlbumButton` 改为 `AlbumList_AddFolderButton`
- 图标改为 `FolderAdd`（`\uE8DE` 或 `\uE8F4`）

**`Views/AlbumListPage.xaml.cs`**：
- 重写 `CreateAlbumButton_Click` → `AddFolder_Click`：
  - 使用 `FolderPicker`（复用 `MultiFolderPicker` 的模式）选择一个或多个文件夹
  - 将选中路径添加到 `AppSettings.ScanDirectories`
  - 保存设置
  - 触发 `ScanService` 重新扫描

**`ViewModels/AlbumListViewModel.cs`**：
- 新增 `AddScanDirectoriesAsync(IEnumerable<string> paths, CancellationToken ct)` — 添加到设置并触发扫描

---

## 6. 本地化

7 个 `.resw` 文件需新增的键：

| 键名 | zh-CN | en-US |
|------|-------|-------|
| `AlbumList_AddFolderButton.Label` | 添加文件夹 | Add Folder |
| `AlbumList_MultiSelectToggle.Label` | 多选 | Multi-select |
| `AlbumList_SelectAllButton.Label` | 全选 | Select All |
| `AlbumList_BatchDeleteButton.Label` | 删除 | Delete |
| `AlbumList_BatchExcludeButton.Label` | 排除目录 | Exclude Directory |
| `AlbumList_BatchMoveButton.Label` | 移动 | Move |
| `AlbumList_BatchCopyButton.Label` | 复制 | Copy |
| `AlbumList_Context_Move` | 移动 | Move |
| `AlbumList_Context_Copy` | 复制 | Copy |
| `AlbumList_Context_Exclude` | 排除目录 | Exclude Directory |
| `AlbumList_Context_OpenInExplorer` | 在文件管理器中打开 | Show in Explorer |
| `AlbumList_Context_Other` | 其它... | Other... |
| `AlbumList_ExcludeConfirm_Title` | 排除目录 | Exclude Directory |
| `AlbumList_ExcludeConfirm_Content` | 确认将「{0}」的目录加入排除列表？照片将从库中移除，但文件不会被删除。 | Exclude the directory of "{0}"? Photos will be removed from the library but files won't be deleted. |
| `AlbumList_ExcludeConfirm_Confirm` | 排除 | Exclude |
| `AlbumList_DeleteConfirm_Content_WithCount` | 确认将「{0}」目录下的 {1} 张照片移到回收站吗？ | Move {1} photos from "{0}" to Recycle Bin? |
| `AlbumList_MoveConfirm_Title` | 移动照片 | Move Photos |
| `AlbumList_MoveConfirm_Content` | 确认将「{0}」目录下的 {1} 张照片移动到「{2}」吗？ | Move {1} photos from "{0}" to "{2}"? |
| `AlbumList_MoveConfirm_Confirm` | 移动 | Move |
| `AlbumList_CopyConfirm_Title` | 复制照片 | Copy Photos |
| `AlbumList_CopyConfirm_Content` | 确认将「{0}」目录下的 {1} 张照片复制到「{2}」吗？ | Copy {1} photos from "{0}" to "{2}"? |
| `AlbumList_CopyConfirm_Confirm` | 复制 | Copy |
| `AlbumList_Toast_Deleted` | 已移到回收站 | Moved to Recycle Bin |
| `AlbumList_Toast_Excluded` | 已排除 {0} 个目录 | Excluded {0} directories |
| `AlbumList_Toast_Moved` | 已移动 {0} 张照片到「{1}」 | Moved {0} photos to "{1}" |
| `AlbumList_Toast_Copied` | 已复制 {0} 张照片到「{1}」 | Copied {0} photos to "{1}" |
| `AlbumList_BatchExcludeConfirm_Content` | 确认将 {0} 个相册的目录加入排除列表？ | Exclude directories of {0} albums? |
| `AlbumList_BatchDeleteConfirm_Content` | 确认将 {0} 个相册共 {1} 张照片移到回收站吗？ | Move {1} photos from {0} albums to Recycle Bin? |
| `AlbumList_BatchMoveConfirm_Content` | 确认将 {0} 个相册共 {1} 张照片移动到「{2}」吗？ | Move {1} photos from {0} albums to "{2}"? |
| `AlbumList_BatchCopyConfirm_Content` | 确认将 {0} 个相册共 {1} 张照片复制到「{2}」吗？ | Copy {1} photos from {0} albums to "{2}"? |
| `AlbumList_Toast_SameDirectory` | 源目录与目标目录相同，请重新选择 | Source and target directory are the same, please choose another |

---

## 7. 文件汇总

| 文件 | 修改内容 |
|------|----------|
| `Views/AlbumListPage.xaml` | ToolTip、多选按钮组、改"新建"为"添加文件夹" |
| `Views/AlbumListPage.xaml.cs` | 增强右键菜单（图标+子菜单+信息区）、Holding、多选处理、确认弹窗、toast |
| `ViewModels/AlbumListViewModel.cs` | 多选模式、ExcludeAlbums/MoveAlbumPhotos/CopyAlbumPhotos/DeleteAlbums 批量接口、AddScanDirectories、GetAlbumDirectories |
| `ViewModels/AlbumItemViewModel.cs` | TotalSize、格式化属性 |
| `Data/DatabaseService.cs` | GetAlbumTotalSize、UpdatePhotoPathBatch |
| `Strings/*/Resources.resw` (x7) | 新增本地化键 |

---

## 8. 验证

1. `make build` — 编译通过
2. 悬浮相册卡片显示信息（名称、时间、数量、大小）
3. 右键菜单：所有项有图标、移动/复制有子菜单（相册目录列表 + "其它..."）、底部有信息区
4. 移动/复制/删除/排除前有确认弹窗，完成后有 toast
5. 长按触发同样菜单
6. 多选模式：切换后显示批量按钮、全选可用、批量操作可用
7. "添加文件夹"打开 picker 并触发扫描
