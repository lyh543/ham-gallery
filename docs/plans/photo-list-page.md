# PhotoListPage / AllPhotosPage 交互增强计划

## 背景

对照片列表页面（PhotoListPage 和 AllPhotosPage）进行交互增强：悬浮信息、带图标的右键菜单、多选改为隐藏/显示、文件系统级移动/复制，以及移除部分旧功能。

---

## 1. 悬浮信息（ToolTip）

鼠标悬浮在照片卡片上时显示信息。

### 修改

**`Views/PhotoListPage.xaml`、`Views/AllPhotosPage.xaml`** — `PhotoCardTemplate` 根 Grid 添加 ToolTip：
```xml
<ToolTipService.ToolTip>
    <StackPanel>
        <TextBlock Text="{x:Bind FileName, Mode=OneWay}"/>
        <TextBlock Text="{x:Bind TakenAtFormatted, Mode=OneWay}" Opacity="0.6"/>
        <TextBlock Text="{x:Bind FileSizeFormatted, Mode=OneWay}" Opacity="0.6"/>
        <TextBlock Text="{x:Bind ResolutionFormatted, Mode=OneWay}" Opacity="0.6"/>
    </StackPanel>
</ToolTipService.ToolTip>
```

**`ViewModels/PhotoItemViewModel.cs`**：
- 暴露 `Width` / `Height`（当前 Photo 模型有，但 VM 未暴露）
- 新增格式化属性：
  - `TakenAtFormatted` → "2024-12-15 10:30" 或 "未知"
  - `FileSizeFormatted` → "12.3 MB"
  - `ResolutionFormatted` → "4032 × 3024"

---

## 2. 右键/长按上下文菜单（新增）

当前照片卡片没有右键菜单，需新增。

### 菜单结构

| 菜单项 | 图标 Glyph | 说明 |
|--------|-----------|------|
| 移动 | `\uE8DE` (MoveToFolder) | 子菜单：相册目录列表 + "其它..." |
| 复制 | `\uE8C8` (Copy) | 子菜单：相册目录列表 + "其它..." |
| 在文件管理器中打开 | `\uE838` (OpenLocal) | `SHOpenFolderAndSelectItems` |
| ── 分隔线 ── | | |
| 删除 | `\uE74D` (Delete) | 移到回收站（红色文字）|
| ── 分隔线 ── | | |
| 信息区 | 无 | 灰色文字：文件名、拍摄时间、大小、分辨率 |

### 移动/复制子菜单结构

使用 `MenuFlyoutSubItem`，列出所有相册目录作为快捷选项：

```
移动 →
  ├─ 相册A (D:\Photos\Travel)
  ├─ 相册B (D:\Photos\Family)
  ├─ ── 分隔线 ──
  └─ 其它...
```

- 列出所有有 `DirectoryPath` 的相册（排除当前照片所在目录）
- 每项显示相册名
- "其它..."打开 `FolderPicker`

### 源目录 = 目标目录的处理

子菜单中已排除当前照片所在目录，但用户通过"其它..."选择 FolderPicker 时可能选到相同目录。处理方式：
- 选中后检查目标目录是否与照片所在目录相同（`Path.GetDirectoryName(photo.FilePath)`，`StringComparer.OrdinalIgnoreCase`）
- 若相同，显示 toast 提示"源目录与目标目录相同，请重新选择"，不执行操作
- 不弹确认弹窗，仅 toast 拒绝，让用户重新选择

### 实现方式

**`Views/PhotoListPage.xaml.cs`、`Views/AllPhotosPage.xaml.cs`**：

```csharp
protected override void OnRightTapped(RightTappedRoutedEventArgs e)
{
    if (e.OriginalSource is not FrameworkElement src) return;
    var vm = FindPhotoVm(src);
    if (vm is null) return;
    e.Handled = true;
    ShowPhotoContextMenu(vm, src, e.GetPosition(src));
}
```

- `FindPhotoVm` — 沿 VisualTree 向上查找 `PhotoItemViewModel`（参考 `AlbumListPage.FindAlbumVm`）
- `ShowPhotoContextMenu` — 构建 `MenuFlyout`，每个 `MenuFlyoutItem` 设置 `Icon = new FontIcon { Glyph = "..." }`
- 移动/复制使用 `MenuFlyoutSubItem`，动态填充相册目录列表 + "其它..."
- 处理 `Holding` 事件（触屏长按），复用同一菜单方法

### 菜单底部信息

```csharp
flyout.Items.Add(new MenuFlyoutSeparator());
flyout.Items.Add(new MenuFlyoutItem
{
    Text = $"{vm.FileName}\n{vm.TakenAtFormatted}  ·  {vm.FileSizeFormatted}  ·  {vm.ResolutionFormatted}",
    IsEnabled = false,
    Foreground = (Brush)App.Current.Resources["TextFillColorSecondaryBrush"],
});
```

---

## 3. 确认弹窗 & 完成 Toast

所有文件操作前显示确认弹窗，完成后显示 toast。

### 确认弹窗格式

| 操作 | 标题 | 内容 | 确认按钮 | 样式 |
|------|------|------|----------|------|
| 单张删除 | 删除照片 | 确认将「{文件名}」移到回收站吗？ | 移到回收站 | Danger |
| 批量删除 | 删除照片 | 确认将「{文件名1}」等 {N} 张照片移到回收站吗？ | 移到回收站 | Danger |
| 单张移动 | 移动照片 | 确认将「{文件名}」移动到「{目标目录名}」吗？ | 移动 | Primary |
| 批量移动 | 移动照片 | 确认将「{文件名1}」等 {N} 张照片移动到「{目标目录名}」吗？ | 移动 | Primary |
| 单张复制 | 复制照片 | 确认将「{文件名}」复制到「{目标目录名}」吗？ | 复制 | Primary |
| 批量复制 | 复制照片 | 确认将「{文件名1}」等 {N} 张照片复制到「{目标目录名}」吗？ | 复制 | Primary |

### 完成 Toast

操作成功后显示 toast（复用现有 `ShowCardSizeToastAsync` 模式的 overlay 动画）：
- "已将 {N} 张照片移到回收站"
- "已移动 {N} 张照片到「{目标目录名}」"
- "已复制 {N} 张照片到「{目标目录名}」"

### 确认弹窗实现

确认弹窗在 code-behind（Page 层）调用，在调用 ViewModel 方法之前：

```csharp
// 右键菜单单张删除
deleteItem.Click += async (_, _) =>
{
    if (!await ConfirmDialogHelper.ShowAsync(
        XamlRoot,
        L10n.Get("PhotoList_DeleteConfirm_Title"),
        L10n.Format("PhotoList_DeleteConfirm_Single", vm.FileName),
        L10n.Get("PhotoList_DeleteConfirm_Confirm"),
        confirmStyle: DialogButtonStyle.Danger)) return;

    await ViewModel.DeletePhotosAsync([vm], _pageCts.Token);
    await ShowToastAsync(L10n.Format("PhotoList_Toast_Deleted", 1));
};
```

---

## 4. 多选模式交互改进

当前：Delete/MoveToAlbum 按钮在非多选模式下 `IsEnabled=false`。
改为：**非多选模式下 `Visibility=Collapsed`**，多选模式下显示。

### CommandBar 按钮布局

非多选模式可见：
- 多选切换
- 排序
- 缩放
- 搜索

多选模式可见（`Visibility` 绑定 `IsMultiSelectMode`）：
- 全选
- 删除
- 移动
- 复制
- ── 分隔线 ──
- 多选切换（保持可见）
- 缩放（保持可见）
- 搜索（保持可见）

### 批量移动/复制的目标选择

与右键菜单一致：点击按钮弹出 `MenuFlyout`，列出相册目录 + "其它..."：

```
点击"移动"按钮 → 弹出 Flyout：
  ├─ 相册A (D:\Photos\Travel)
  ├─ 相册B (D:\Photos\Family)
  ├─ ── 分隔线 ──
  └─ 其它...
```

### 修改

**`Views/PhotoListPage.xaml`**：
- 删除 `AddPhotosButton`（添加照片）
- 删除 `RenameAlbumButton`（重命名相册）
- 将 Delete/Move 按钮的 `IsEnabled` 改为 `Visibility` 绑定
- Move 按钮从 `MoveToAlbumButton` 改为 `MoveButton`（文件系统移动）
- 新增 Copy 按钮（`\uE8C8`）
- 新增 SelectAll 按钮
- 多选切换位置移到排序按钮左边

**`Views/AllPhotosPage.xaml`**：
- 同上（无需删除 AddPhotos/Rename，AllPhotosPage 本来就没有）
- Delete/Move `IsEnabled` → `Visibility`
- 新增 Copy、SelectAll 按钮

**`Views/PhotoListPage.xaml.cs`**：
- 删除 `AddPhotos_Click`、`RenameAlbum_Click`、`AlbumRenameBox_KeyDown`、`AlbumRenameBox_LostFocus` 等方法
- 新增 `SelectAll_Click` → `PhotoGridView.SelectAll()`
- 新增 `CopyPhotos_Click`：弹出相册目录 Flyout → 确认弹窗 → 调用 ViewModel → toast
- 修改 `MoveToAlbum_Click` → `MovePhotos_Click`：弹出相册目录 Flyout → 确认弹窗 → 调用 ViewModel → toast
- 修改 `Delete_Click`：增加 toast

**`Views/AllPhotosPage.xaml.cs`**：
- 新增 `SelectAll_Click`、`CopyPhotos_Click`
- 修改 `MoveToAlbum_Click` → `MovePhotos_Click`
- 修改 `Delete_Click`：增加 toast

---

## 5. 移动和复制改为文件系统操作

移动和复制都通过子菜单选择目标目录（相册目录 + FolderPicker）。

### 移动

- `File.Move(source, dest)` 移动文件到目标目录
- 更新 DB 中的 `FilePath` 和 `FileName`
- 从当前视图集合中移除
- 处理文件名冲突（追加 `(1)` 后缀）

### 复制

- `File.Copy(source, dest, overwrite: false)` 复制文件到目标目录
- 不修改数据库（复制的文件如在扫描目录中会被下次扫描发现）
- 处理文件名冲突

### 文件名冲突处理

```csharp
private static string GetUniqueFilePath(string targetDir, string fileName)
{
    var dest = Path.Combine(targetDir, fileName);
    if (!File.Exists(dest)) return dest;
    var name = Path.GetFileNameWithoutExtension(fileName);
    var ext = Path.GetExtension(fileName);
    for (int i = 1; ; i++)
    {
        dest = Path.Combine(targetDir, $"{name} ({i}){ext}");
        if (!File.Exists(dest)) return dest;
    }
}
```

### 修改

**`ViewModels/PhotoListViewModel.cs`**：
- 删除现有 `MoveToAlbumAsync`、`AddPhotosAsync`、`BeginRenameAlbum`、`CommitRenameAlbumAsync`、`CancelRenameAlbum`、`IsRenamingAlbum`、`EditAlbumName`
- 新增 `MovePhotosToDirectoryAsync(IReadOnlyList<PhotoItemViewModel> items, string targetDir, CancellationToken ct)`（批量接口）：
  - 批量移动文件 + 更新 DB 路径
  - 从 `Photos` 集合中移除
- 新增 `CopyPhotosToDirectoryAsync(IReadOnlyList<PhotoItemViewModel> items, string targetDir, CancellationToken ct)`（批量接口）：
  - 批量复制文件
  - 不修改 DB 和集合
- 新增 `GetAlbumDirectoriesAsync(CancellationToken ct)` — 获取所有相册目录列表（供子菜单使用）
- 单张操作的右键菜单调用批量接口：`MovePhotosToDirectoryAsync([vm], targetDir, ct)`

**`ViewModels/AllPhotosViewModel.cs`**：
- 删除现有 `MoveToAlbumAsync`
- 新增 `MovePhotosToDirectoryAsync`（同上，额外处理 `_allPhotos` 缓存和分组清理）
- 新增 `CopyPhotosToDirectoryAsync`
- 新增 `GetAlbumDirectoriesAsync`

**`Data/DatabaseService.cs`**：
- 新增 `UpdatePhotoPathAsync(long id, string newPath, string newFileName, CancellationToken ct)` — 更新单条照片路径

---

## 6. 移除旧功能

**从 PhotoListPage 中删除**：
- "添加照片"按钮及 `AddPhotos_Click` 处理
- "重命名相册"按钮及 `RenameAlbum_Click`、`AlbumRenameBox_KeyDown`、`AlbumRenameBox_LostFocus`
- XAML 中的 `AlbumRenameBox` 文本框和 inline rename Grid
- ViewModel 中的 `AddPhotosAsync`、`BeginRenameAlbum`、`CommitRenameAlbumAsync`、`CancelRenameAlbum`、`IsRenamingAlbum`、`EditAlbumName`

---

## 7. PhotoItemViewModel 增强

**`ViewModels/PhotoItemViewModel.cs`**：

```csharp
// 新增属性
public int?   Width  => _photo.Width;
public int?   Height => _photo.Height;

public string TakenAtFormatted => string.IsNullOrEmpty(_photo.TakenAt)
    ? L10n.Get("Common_Unknown")
    : _photo.TakenAt[..Math.Min(16, _photo.TakenAt.Length)].Replace('T', ' ');

public string FileSizeFormatted => _photo.FileSize switch
{
    < 1024       => $"{_photo.FileSize} B",
    < 1048576    => $"{_photo.FileSize / 1024.0:F1} KB",
    < 1073741824 => $"{_photo.FileSize / 1048576.0:F1} MB",
    _            => $"{_photo.FileSize / 1073741824.0:F2} GB",
};

public string ResolutionFormatted =>
    _photo.Width.HasValue && _photo.Height.HasValue
        ? $"{_photo.Width} × {_photo.Height}"
        : L10n.Get("Common_Unknown");
```

---

## 8. 本地化

7 个 `.resw` 文件需新增的键：

| 键名 | zh-CN | en-US |
|------|-------|-------|
| `Common_Unknown` | 未知 | Unknown |
| `Common_Other` | 其它... | Other... |
| `PhotoList_SelectAllButton.Label` | 全选 | Select All |
| `PhotoList_MoveButton.Label` | 移动 | Move |
| `PhotoList_CopyButton.Label` | 复制 | Copy |
| `PhotoList_Context_Move` | 移动 | Move |
| `PhotoList_Context_Copy` | 复制 | Copy |
| `PhotoList_Context_Delete` | 删除 | Delete |
| `PhotoList_Context_OpenInExplorer` | 在文件管理器中打开 | Show in Explorer |
| `PhotoList_DeleteConfirm_Single` | 确认将「{0}」移到回收站吗？ | Move "{0}" to Recycle Bin? |
| `PhotoList_MoveConfirm_Title` | 移动照片 | Move Photos |
| `PhotoList_MoveConfirm_Single` | 确认将「{0}」移动到「{1}」吗？ | Move "{0}" to "{1}"? |
| `PhotoList_MoveConfirm_Batch` | 确认将「{0}」等 {1} 张照片移动到「{2}」吗？ | Move "{0}" and {1} other photos to "{2}"? |
| `PhotoList_MoveConfirm_Confirm` | 移动 | Move |
| `PhotoList_CopyConfirm_Title` | 复制照片 | Copy Photos |
| `PhotoList_CopyConfirm_Single` | 确认将「{0}」复制到「{1}」吗？ | Copy "{0}" to "{1}"? |
| `PhotoList_CopyConfirm_Batch` | 确认将「{0}」等 {1} 张照片复制到「{2}」吗？ | Copy "{0}" and {1} other photos to "{2}"? |
| `PhotoList_CopyConfirm_Confirm` | 复制 | Copy |
| `PhotoList_Toast_Deleted` | 已将 {0} 张照片移到回收站 | Moved {0} photos to Recycle Bin |
| `PhotoList_Toast_Moved` | 已移动 {0} 张照片到「{1}」 | Moved {0} photos to "{1}" |
| `PhotoList_Toast_Copied` | 已复制 {0} 张照片到「{1}」 | Copied {0} photos to "{1}" |
| `PhotoList_Toast_SameDirectory` | 源目录与目标目录相同，请重新选择 | Source and target directory are the same, please choose another |
| AllPhotos 页面同理，使用 `AllPhotos_` 前缀 | | |

---

## 9. 文件汇总

| 文件 | 修改内容 |
|------|----------|
| `ViewModels/PhotoItemViewModel.cs` | 暴露 Width/Height，新增格式化属性 |
| `ViewModels/PhotoListViewModel.cs` | 删除 AddPhotos/Rename/MoveToAlbum，新增 MoveToDir/CopyToDir 批量接口 |
| `ViewModels/AllPhotosViewModel.cs` | 删除 MoveToAlbum，新增 MoveToDir/CopyToDir 批量接口 |
| `Views/PhotoListPage.xaml` | ToolTip、删除旧按钮、多选改 Visibility、新增 Copy/SelectAll |
| `Views/PhotoListPage.xaml.cs` | 右键菜单（带图标+子菜单+信息区）、Holding、确认弹窗、toast、删除旧处理 |
| `Views/AllPhotosPage.xaml` | ToolTip、多选改 Visibility、新增 Copy/SelectAll |
| `Views/AllPhotosPage.xaml.cs` | 右键菜单（带图标+子菜单+信息区）、Holding、确认弹窗、toast |
| `Data/DatabaseService.cs` | 新增 UpdatePhotoPathAsync |
| `Strings/*/Resources.resw` (x7) | 新增本地化键 |

---

## 10. 验证

1. `make build` — 编译通过
2. 照片悬浮显示信息（文件名、拍摄时间、大小、分辨率）
3. 右键菜单：所有项有图标、移动/复制有子菜单（相册目录 + "其它..."）、底部有信息区
4. 所有文件操作前有确认弹窗（显示具体文件名和目标目录）
5. 所有文件操作完成后有 toast
6. 长按触发同样菜单
7. 多选模式：非多选时批量按钮隐藏、多选时显示、全选可用
8. 移动：文件实际被移动、DB 路径已更新
9. 复制：文件实际被复制
10. 确认"添加照片"和"重命名相册"按钮已移除
