# Settings page implementation steps
_Exported on 4/2/2026 at 15:34:07 GMT+8 from Cursor (2.6.22)_

---

**User**

请阅读 PROMPT.md 以后，逐步实现这个项目。

请按「第十二章·交付顺序」逐步推进，每次只需要完成一步，避免一次生成过多代码导致质量下降。

继续参照 PROMPT.md，实现 5.6 设置页（SettingsPage）

---

**Cursor**

现在让我查看现有的文件，了解项目的当前状态。

现在我对项目结构有了清晰的了解。让我创建任务列表并逐步实现设置页。

**步骤 1：创建 `Helpers/AppDataPaths.cs`**

**步骤 2：创建 `SettingsViewModel.cs`**

**步骤 3：实现 `SettingsPage.xaml`**

XAML 中有一个静态方法引用需要修正，改为在 ViewModel 中直接暴露 severity 属性。先更新 SettingsViewModel，再修正 XAML：

现在修正 XAML，去掉静态方法调用，用 `IsWarningStatus` 驱动两个独立 InfoBar（成功态 / 警告态）：

用页面资源中的转换器替代静态方法引用，这样更简洁可靠：

需要更新转换器支持 `Invert` 属性，或者直接用 `ConverterParameter`。先检查转换器并更新 XAML：

**步骤 4：实现 `SettingsPage.xaml.cs`**

**步骤 5：更新 `App.xaml.cs` — 暴露 MainWindow 属性并注册 SettingsViewModel**

需要在 `App.xaml.cs` 添加 `SettingsViewModel` 的 using：

`FluentGallery.ViewModels` 命名空间已经引入，`SettingsViewModel` 在其中，无需额外 using。

现在检查是否需要给 `SettingsPage.xaml.cs` 添加 `GetRequiredService` 扩展的 using：

需要修正 `SettingsPage.xaml.cs` 中的几个问题 — 添加缺失的 using，修正 `BuildDirSettingsCard` 中的语法错误：

需要清理 XAML 中的 `SettingsExpander.Items` 占位符（code-behind 完全控制其内容）：

现在检查 lint 错误，同时也确认 ViewModel 中没有遗漏的问题：

无 lint 错误。现在做一次快速的构建验证：

**构建成功，0 个错误，仅有 2 个 PRI 警告**（是 EF Core 设计工具的本地化资源问题，与本次改动无关，此前即存在）。

所有任务完成，构建通过。以下是本次实现的完整总结：

---

## 5.6 设置页实现总结

### 新增文件

**`Helpers/AppDataPaths.cs`**
统一管理所有应用数据路径（`%LocalAppData%\FluentGallery\`），避免路径字符串散落各处。

**`ViewModels/SettingsViewModel.cs`**
完整的 MVVM ViewModel，含：
- 从 `DatabaseService` 加载/保存 `AppSettings`（JSON 序列化）
- 5 个设置组的可绑定属性，用 `_isInitialized` 标志防止加载期误触发自动保存
- 主题切换立即生效（修改 `root.RequestedTheme`）
- 语言切换保存并提示重启
- 三个缓存清除命令（`ClearThumbnailCache` / `ClearDatabaseCache` / `ClearAllData`）

### 修改文件

**`Views/SettingsPage.xaml`**
使用 `CommunityToolkit.WinUI.Controls.SettingsCard` / `SettingsExpander` 构建 Windows 11 风格 UI，分 5 组：

| 分组 | 控件 |
|------|------|
| 扫描目录 | `SettingsExpander` + 动态目录列表 + `ToggleSwitch` |
| 外观 | `ComboBox`（语言/主题） |
| 行为 | `ToggleSwitch` + `Slider`（预加载张数） |
| 缓存与数据 | 只读显示 + 三个清除按钮 |
| 关于 | 版本、GitHub 链接、许可证 |

**`Views/SettingsPage.xaml.cs`**
- `FolderPicker` + `WindowNative.GetWindowHandle` 关联窗口句柄（非打包应用必需）
- 三个破坏性操作前弹出 `ContentDialog` 确认
- 目录列表从 code-behind 动态填充 `SettingsExpander.Items`

**`App.xaml.cs`**
- 暴露 `public Window? MainWindow` 属性（供 ViewModel 切换主题用）
- DI 注册 `SettingsViewModel`

---

**User**

111

---

**Cursor**

您好，请问需要我做什么？如果是想继续按 PROMPT.md 的「第十二章·交付顺序」推进，下一步应该是：

- **第 5 步**：后台扫描服务（`ScanService` + `ThumbnailService`）
- 或者继续完善某个已实现的页面

请告诉我您的需求。

---

**User**

我使用设置-扫描目录-添加目录，添加了一个目录，但是前端并没有反馈。是没有加上吗？修复这个问题，然后加一组测试，并自测通过

---

**Cursor**

先看一下终端输出，了解运行时的实际情况：

问题已定位。让我先看看测试项目和现有的测试结构：

找到根本原因了。

**问题所在**：`SettingsExpander.Items` 是 CommunityToolkit 内部维护的集合，通过 `Items.Clear()` + `Items.Add()` 命令式操作并不能可靠地触发 `ItemsRepeater` 重绘。正确做法是直接绑定 `ItemsSource`，让 `ObservableCollection` 的 `CollectionChanged` 自动驱动 UI。

同时，`SettingsViewModel` 直接引用了 `Microsoft.UI.Xaml`（用于 `ElementTheme`），导致无法在纯 .NET 测试项目中测试。需要先抽出 `IThemeService` 接口解耦。

**步骤 1：创建 `IThemeService` 和 `WinUiThemeService`**

**步骤 2：更新 `SettingsViewModel` 注入 `IThemeService`**

注册 `WinUiThemeService` 到 DI，同时更新 `SettingsViewModel` 注册：

**步骤 3：修改 XAML — 用 `ItemsSource` 绑定替代命令式 `Items.Clear()/Add()`**

**步骤 4：简化 code-behind — 移除所有手动 `Items` 操作**

**步骤 5：更新测试项目并编写 `SettingsViewModelTests`**

现在编写测试：

现在运行测试自测：

问题是测试项目使用 `net9.0`（C# 13），而 `[ObservableProperty]` 的 `partial` 属性特性需要 C# 14（.NET 10）。需要升级测试项目的目标框架：

同步升级测试项目中的 EF Core 包版本：

**41 个测试全部通过（20 个新 SettingsViewModel 测试 + 21 个原有 DatabaseService 测试）。**

再确认主项目也正常编译：

---

## 总结

### Bug 根本原因

`SettingsExpander` 是 CommunityToolkit.WinUI 的自定义 `Control`（非 `ItemsControl`），其内部用 `ItemsRepeater` 渲染子项。通过命令式 `Items.Clear()` + `Items.Add()` 操作时，`ItemsRepeater` 不一定能可靠地检测到集合变更并重绘。

**正确做法**：使用 `ItemsSource="{x:Bind ViewModel.ScanDirectories, Mode=OneWay}"` + `ItemTemplate` 数据绑定。`ScanDirectories` 是 `ObservableCollection<string>`，每次 `Add/Remove` 都会自动发出 `CollectionChanged` 通知，`ItemsRepeater` 立即响应更新 UI。

### 所有改动

| 文件 | 变更内容 |
|------|--------|
| `Services/IThemeService.cs` *(新建)* | 主题服务接口，解耦 ViewModel 对 WinUI 的直接依赖 |
| `Services/WinUiThemeService.cs` *(新建)* | WinUI 实现，修改 `root.RequestedTheme` |
| `ViewModels/SettingsViewModel.cs` | 注入 `IThemeService`，移除 `using Microsoft.UI.Xaml` |
| `Views/SettingsPage.xaml` | 扫描/排除目录改用 `ItemsSource` + `ItemTemplate` |
| `Views/SettingsPage.xaml.cs` | 删除 `RebuildScanDirsItems` 等手动操作，代码缩减约 60 行 |
| `App.xaml.cs` | DI 注册 `WinUiThemeService` |
| `FluentGallery.Tests.csproj` | 升级到 `net10.0`，添加 `CommunityToolkit.Mvvm`，引入 `SettingsViewModel.cs` 等源文件 |
| `SettingsViewModelTests.cs` *(新建)* | 20 个测试覆盖 Add/Remove/Load/Save/默认值/语言映射/清除数据 |

### 测试结果

```
测试总数: 41 — 通过: 41
```

---

**User**

扫描目录支持多选目录

---

**Cursor**

`FolderPicker` 原生不支持多选，需要通过 Win32 `IFileOpenDialog` COM API（`FOS_ALLOWMULTISELECT | FOS_PICKFOLDERS`）实现。

**步骤 1：创建 `MultiFolderPicker`**

**步骤 2：`SettingsViewModel` 增加批量 Add 方法**

**步骤 3：更新 `SettingsPage.xaml.cs` — 使用多选 picker**

**步骤 4：补充测试**

运行测试验证：

**46 个测试全部通过。** 最后确认主项目也能编译：

---

## 总结

### 实现思路

WinUI 3 的 `FolderPicker` 只能单选，真正的多选需要通过 Win32 `IFileOpenDialog` COM 接口，同时传入以下两个标志：

| 标志 | 值 | 含义 |
|------|----|------|
| `FOS_PICKFOLDERS` | `0x20` | 选择目录而非文件 |
| `FOS_ALLOWMULTISELECT` | `0x200` | 支持 Ctrl+单击多选 |

### 新增/修改文件

| 文件 | 变更 |
|------|------|
| `Helpers/MultiFolderPicker.cs` *(新)* | Win32 COM 封装，STA 线程 + `IFileOpenDialog` + `IShellItemArray` vtable 完整定义 |
| `ViewModels/SettingsViewModel.cs` | 新增 `AddScanDirectories(IEnumerable<string>)` 和 `AddExcludeDirectories(IEnumerable<string>)` 批量方法，一次只触发一次 `SaveAsync` |
| `Views/SettingsPage.xaml.cs` | 两个"添加"按钮均改用 `MultiFolderPicker.PickAsync`，返回 `IReadOnlyList<string>` 后调用批量 Add |
| `SettingsViewModelTests.cs` | 新增 5 个测试（批量添加、去重、跨批去重、空列表、持久化） |

用户在资源管理器风格的对话框中按住 **Ctrl** 即可多选文件夹，点击"添加"后所有选中目录一次性写入列表。

---

**User**

去掉设置“”递归扫描子目录”，这个选项就应该为 True

---

**Cursor**

需要修改的地方：SettingsPage.xaml（移除 UI）、SettingsViewModel（移除属性和相关方法）、SettingsViewModelTests（更新测试）。

更新测试——移除依赖 `RecursiveScan` 的断言：

跑测试确认：

46 个测试全部通过。

改动非常简洁，一共三处：

| 文件 | 修改内容 |
|------|--------|
| `SettingsPage.xaml` | 删除"递归扫描子目录"的 `SettingsCard` |
| `SettingsViewModel.cs` | 删除 `RecursiveScan` 可观察属性和 `OnRecursiveScanChanged` 回调；`SaveAsync` 中将 `_settings.RecursiveScan` 硬编码为 `true` |
| `SettingsViewModelTests.cs` | 移除对 `RecursiveScan` 的读写断言，保留对 `loaded.RecursiveScan == true` 的验证（确保持久化时始终写入 `true`） |

`AppSettings.RecursiveScan` 字段本身保留，供后续 `ScanService` 使用，只是从用户界面中隐藏并固定为 `true`。

---

**User**

第一次用户打开应用时，默认的窗口太小了。调大一点。其它桌面应用是如何设置默认窗口大小的？

---

**Cursor**

已有 `AppWindow.Resize(new SizeInt32(1200, 800))` 这行代码，但问题在于 `AppWindow.Resize` 接受的是**物理像素**，而非逻辑像素。在 150% DPI 缩放下，1200 物理像素只有 800 逻辑像素宽，看起来很小。

**其他桌面应用的标准做法**是：用 DPI 将逻辑尺寸换算成物理像素，并参考屏幕工作区大小做上限限制，最后居中显示。

验证编译：

编译通过。下面回答你的第二个问题：

---

## 其他桌面应用如何设置默认窗口大小？

### 常见模式

| 方案 | 适用场景 | 示例 |
|------|---------|------|
| **固定逻辑尺寸** | 功能固定的工具类应用 | 本次实现：1400×900 逻辑像素 |
| **屏幕工作区百分比** | 内容类/媒体类应用 | Windows 文件资源管理器 ≈ 75% 工作区 |
| **记住上次大小/位置** | 文档/编辑器类应用 | VS Code、Notepad++ |
| **自适应内容** | 设置页、对话框 | 根据内容自动 FitToContent |

### 关键点：必须用物理像素

`AppWindow.Resize` / `AppWindow.MoveAndResize` 接受的是**物理像素**。在 150% 缩放的屏幕上，如果直接写 `1200, 800`，实际逻辑尺寸只有 800×533，视觉上很小。正确做法：

```csharp
double scale = GetDpiForWindow(hwnd) / 96.0;
int physW = (int)Math.Round(1400 * scale);  // 逻辑 → 物理
```

### 本次实现的完整逻辑

```
目标逻辑尺寸 1400×900
    → × DPI比例 → 物理像素
    → Min(物理像素, 工作区×90%) → 防止溢出屏幕
    → 居中 MoveAndResize        → 窗口出现在屏幕正中
```

如果未来想**记住上次大小**，可以将 `AppWindow.Size` 和 `AppWindow.Position` 存入 `AppSettings` 并在启动时恢复。

---

**User**

设置页面的右边超出边界了

---

**Cursor**

问题清楚了：`ScrollViewer` 设置了 `Padding="32,16,32,32"`，但滚动条本身渲染在 Padding **之外**（紧贴窗口右边框），导致视觉上"溢出"。

修复方式：去掉 ScrollViewer 的右侧 padding，改为内部 StackPanel 的 `Margin`，让滚动条自然靠在窗口右边框内，同时加 `HorizontalScrollBarVisibility="Disabled"` 防止横向溢出。

原理说明：

- **之前**：`ScrollViewer Padding="…32"` → 内容区域缩进 32px，但垂直滚动条在 padding 外侧渲染，导致滚动条超出窗口可见区域
- **之后**：ScrollViewer 右侧 padding 去掉，滚动条自然落在窗口右边框；内容区域的右边距改为 StackPanel 的 `Margin="0,0,32,32"`，视觉间距不变；`HorizontalScrollBarVisibility="Disabled"` 防止水平方向出现溢出滚动条

---

**User**

左边有大量空白，右边超出边界了

---

**Cursor**

看截图，问题很明显：`StackPanel` 有 `MaxWidth="860"` 但 `HorizontalAlignment` 没有指定，在容器宽于 860 时 WinUI 会将其**水平居中**（Stretch + MaxWidth 的默认行为），造成左侧空白；与此同时 `SettingsExpander` 里的按钮又宽于计算好的宽度，导致右侧溢出。

最简单、最正确的修法：**去掉 MaxWidth 和多余约束，让 StackPanel 自然 Stretch 填满可用宽度，ScrollViewer 不加任何 Padding（改为 StackPanel 的 Margin）。**

这样：
- **ScrollViewer 无 Padding**：垂直滚动条自然贴着页面右边框，不会"溢出"
- **StackPanel 用 Margin 留边距**：四边留白 32px，滚动条在 Margin 外侧，视觉正确
- **去掉 MaxWidth**：StackPanel 用默认 `HorizontalAlignment="Stretch"` 填满可用宽度，不会产生居中留白，也不会溢出右侧

---

**User**

dotnet watch run --project FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug

这个命令热更新不了

@c:\Users\lyh54\.cursor\projects\c-Users-lyh54-git-github-ham-gallery\terminals\1.txt:394-433 

---

**Cursor**

这是 WinUI 3 开发的已知限制，有两个根本原因：

**原因一：`dotnet watch` 的 hot reload 只处理 C# 代码**
XAML 文件变更时它输出 `No C# changes to apply`，然后进入"等待下一次变更"状态，而不是重启应用。

**原因二：XAML Hot Reload 需要 Visual Studio 的调试桥**
WinUI 3 的 XAML 热更新走的是 VS 调试器协议，`dotnet watch` 无法替代。

---

## 解决方案

### 方案 A：加 `--no-hot-reload`（推荐，立即生效）

任何文件（`.cs` / `.xaml`）变更都触发**全量重建 + 重启**，逻辑最简单：

```powershell
dotnet watch run --no-hot-reload --project FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
```

### 方案 B：用 Visual Studio 调试（同时获得 XAML Hot Reload）

VS 2022 + Windows App SDK 扩展支持真正的 XAML 热更新，修改 `.xaml` 后无需重启即可在运行中的窗口看到效果。

---

## 现在直接切到方案 A

终端里还在运行。直接在终端里按 `Ctrl+C` 停掉旧命令，换成新的：

```powershell
dotnet watch run --no-hot-reload --project FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
```

---

顺便说一下那条 `MSB3026` 警告（**无法复制 apphost.exe，文件被进程锁定**）也是正常的——这是因为旧进程还在运行，下一次重建时文件已释放就不会出现了，不影响功能。

---

**User**

在 readme 说明这一点

---

**Cursor**

README 里第 47–54 行已经有"Hot reload"一节，但用的仍是**旧命令**（没有 `--no-hot-reload`），说明也不够详细。直接更新那一节：

更新后的效果：

```47:63:c:\Users\lyh54\git\github\ham-gallery\README.md
### Watch mode（文件变更自动重建 + 重启）

```powershell
dotnet watch run --no-hot-reload --project FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
```

> **必须加 `--no-hot-reload`。**  
> `dotnet watch` 默认开启 C# hot reload，但 WinUI 3 的 XAML 变更无法通过该机制应用：检测到 `.xaml` 文件变更时会输出 `No C# changes to apply`，然后进入等待状态而**不重启应用**，导致改动看不到效果。  
> 加上 `--no-hot-reload` 后，任何文件（`.cs` / `.xaml`）变更都会触发全量重建并自动重启，行为符合预期。

> **XAML 实时预览**需要 Visual Studio 2022（安装 **Windows App SDK** 扩展），在调试模式下修改 `.xaml` 可通过 XAML Hot Reload 工具栏即时刷新，无需重启。
```

---

**User**

为什么窗口关闭以后，进还在运行，导致每次需要去任务管理器结束进程才能继续构建

---

**Cursor**

这是 WinUI 3 的一个常见陷阱：**关闭窗口不等于退出进程**。WinUI 3 的 `Window.Closed` 只销毁窗口对象，进程本身继续存活，直到显式调用 `Application.Current.Exit()`。WPF/WinForms 的"主窗口关闭 → 进程退出"行为在 WinUI 3 里需要手动实现。

修复很简单，在 `MainWindow` 构造函数里监听 `Closed` 事件：

顺手在 README 里也记一句，方便以后查：

完成。改动说明：

**根本原因**  
WinUI 3 与 WPF/WinForms 不同，`Window.Closed` 只销毁窗口对象，**宿主进程不会自动退出**。这就导致每次关闭窗口后 `FluentGallery.exe` 仍然占用文件锁，下次构建时 `dotnet build` 无法覆盖已加载的 DLL，必须手动结束进程。

**修复**  
在 `MainWindow.xaml.cs` 构造函数里加了一行：

```csharp
this.Closed += (_, _) => Application.Current.Exit();
```

关闭窗口时 `Exit()` 会正常走完应用生命周期（触发 `UnhandledException` 清理等），进程随即退出，`dotnet watch` 能立即检测到退出并触发下一次重建。

