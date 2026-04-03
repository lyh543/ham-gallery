# Search functionality for file names
_Exported on 4/3/2026 at 18:04:52 GMT+8 from Cursor (2.6.22)_

---

**User**

添加搜索功能，从数据库搜索文件名；也支持搜索时间范围（用拍摄时间、修改时间、创建时间去匹配）

搜索可以从导航栏进入（搜索全局），也可以在相册详情右上角放一个搜索的按钮（搜索当前相册下的图片）

---

**Cursor**

好的，我已经了解了项目结构。现在让我读取几个关键文件来详细规划实现。

现在我对整个项目结构有了清晰了解。开始实现搜索功能。

Now update `MainWindow.xaml.cs` to handle Search navigation and sync selection:

在 `PhotoListPage.xaml` 的 CommandBar 中添加搜索按钮：

在 `PhotoListPage.xaml.cs` 中添加对应的 `using` 和点击处理函数：

`PhotoListViewModel` 中 `_albumId` 是 `private` 字段，需要暴露它。让我检查一下：

需要在 `PhotoListViewModel` 中将 `_albumId` 暴露为公共属性：

现在尝试编译，检查有没有错误：

编译成功，0 个错误，只有 2 个与 `System.CommandLine` 资源文件相关的预先存在的警告（与本次改动无关）。

让我最后检查一下几个关键文件的 lint 状态：

零 linter 错误。让我对实现做一个完整总结。

---

## 实现完成

所有改动均已完成，编译 0 错误。以下是完整说明：

### 新增文件

| 文件 | 说明 |
|------|------|
| `Models/SearchArgs.cs` | 导航参数，携带可选的 `AlbumId`（相册搜索）和 `AlbumName` |
| `ViewModels/SearchViewModel.cs` | 搜索页的 ViewModel，管理关键词、时间字段、日期范围、结果集 |
| `Views/SearchPage.xaml` | 搜索页 UI：筛选面板 + 结果网格 |
| `Views/SearchPage.xaml.cs` | 搜索页代码后端，含缩略图懒加载、捏合缩放、状态管理 |

### 修改文件

| 文件 | 改动 |
|------|------|
| `Data/DatabaseService.cs` | 新增 `SearchPhotosAsync`：支持文件名模糊匹配（SQLite LIKE）+ 内存日期范围过滤 |
| `ViewModels/PhotoListViewModel.cs` | 暴露 `AlbumId` 公共属性供页面跳转用 |
| `MainWindow.xaml` | 在导航栏 MenuItems 末尾添加"搜索"入口（搜索图标 &#xE721;）|
| `MainWindow.xaml.cs` | 处理 `Tag="Search"` 导航逻辑；`SyncNavSelection` 增加 `SearchPage` 分支 |
| `Views/PhotoListPage.xaml` | 工具栏右侧添加"搜索"按钮（带分隔线）|
| `Views/PhotoListPage.xaml.cs` | `Search_Click` 跳转到 `SearchPage`，传入当前 `AlbumId + AlbumName` |
| `App.xaml.cs` | DI 容器注册 `SearchViewModel`（`Transient`）|

### 功能说明

**两个入口：**
- **导航栏"搜索"** → 打开全库搜索（无相册限制）
- **相册详情页工具栏右上角"搜索"按钮** → 打开相册内搜索，页面顶部会提示「搜索范围：相册「xxx」」

**支持的搜索条件（可任意组合）：**
- **文件名关键词**：模糊匹配，不区分大小写
- **时间字段**：可选"拍摄时间（TakenAt）"、"修改时间（ModifiedAt）"、"创建时间（CreatedAt）"
- **时间范围**：选择开始日期和/或结束日期（均含边界）

**交互细节：**
- 按 `Enter` 或点击"搜索"按钮均可触发
- 支持捏合手势调整网格列数
- 点击结果图片跳转至 `PhotoDetailPage`（带左右切换）
- 搜索前显示引导提示；搜索后显示「在 xxx 找到 N 张照片」
- 切换搜索条件后自动取消上次正在进行的搜索请求（`CancellationToken`）

