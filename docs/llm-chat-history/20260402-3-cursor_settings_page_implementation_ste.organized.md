# 设置页实现与 WinUI 3 常见问题修复

> Saved: 2026-04-02

## 继续参照 PROMPT.md，实现 5.6 设置页（SettingsPage）

按以下五个步骤完成实现：

**步骤 1：创建 `Helpers/AppDataPaths.cs`**
统一管理所有应用数据路径（`%LocalAppData%\FluentGallery\`），避免路径字符串散落各处。

**步骤 2：创建 `ViewModels/SettingsViewModel.cs`**
完整的 MVVM ViewModel，含：
- 从 `DatabaseService` 加载/保存 `AppSettings`（JSON 序列化）
- 5 个设置组的可绑定属性，用 `_isInitialized` 标志防止加载期误触发自动保存
- 主题切换立即生效（修改 `root.RequestedTheme`）
- 语言切换保存并提示重启
- 三个缓存清除命令（`ClearThumbnailCache` / `ClearDatabaseCache` / `ClearAllData`）

**步骤 3：实现 `Views/SettingsPage.xaml`**
使用 `CommunityToolkit.WinUI.Controls.SettingsCard` / `SettingsExpander` 构建 Windows 11 风格 UI，分 5 组：

| 分组 | 控件 |
|------|------|
| 扫描目录 | `SettingsExpander` + 动态目录列表 + `ToggleSwitch` |
| 外观 | `ComboBox`（语言/主题） |
| 行为 | `ToggleSwitch` + `Slider`（预加载张数） |
| 缓存与数据 | 只读显示 + 三个清除按钮 |
| 关于 | 版本、GitHub 链接、许可证 |

用页面资源转换器替代静态方法引用，用 `IsWarningStatus` 驱动成功态/警告态两个独立 InfoBar。

**步骤 4：实现 `Views/SettingsPage.xaml.cs`**
- `FolderPicker` + `WindowNative.GetWindowHandle` 关联窗口句柄（非打包应用必需）
- 三个破坏性操作前弹出 `ContentDialog` 确认
- 目录列表从 code-behind 动态填充 `SettingsExpander.Items`

**步骤 5：更新 `App.xaml.cs`**
- 暴露 `public Window? MainWindow` 属性（供 ViewModel 切换主题用）
- DI 注册 `SettingsViewModel`

构建成功，0 个错误，仅有 2 个 PRI 警告（EF Core 设计工具本地化资源问题，与本次改动无关）。

## 我使用设置-扫描目录-添加目录，添加了一个目录，但是前端并没有反馈。修复这个问题，然后加一组测试，并自测通过

**根本原因**：`SettingsExpander.Items` 是 CommunityToolkit 内部维护的集合，通过 `Items.Clear()` + `Items.Add()` 命令式操作并不能可靠地触发 `ItemsRepeater` 重绘。正确做法是直接绑定 `ItemsSource`，让 `ObservableCollection` 的 `CollectionChanged` 自动驱动 UI。

同时，`SettingsViewModel` 直接引用了 `Microsoft.UI.Xaml`（用于 `ElementTheme`），导致无法在纯 .NET 测试项目中测试。需要先抽出 `IThemeService` 接口解耦。

**修复步骤：**

1. 创建 `IThemeService` 接口和 `WinUiThemeService` 实现
2. 更新 `SettingsViewModel` 注入 `IThemeService`，移除 `using Microsoft.UI.Xaml`
3. XAML 改用 `ItemsSource="{x:Bind ViewModel.ScanDirectories, Mode=OneWay}"` + `ItemTemplate`
4. 简化 code-behind，移除所有手动 `Items.Clear()/Add()` 操作
5. 升级测试项目至 `net10.0`（C# 14），编写 `SettingsViewModelTests`

**所有改动：**

| 文件 | 变更内容 |
|------|--------|
| `Services/IThemeService.cs` *(新建)* | 主题服务接口，解耦 ViewModel 对 WinUI 的直接依赖 |
| `Services/WinUiThemeService.cs` *(新建)* | WinUI 实现，修改 `root.RequestedTheme` |
| `ViewModels/SettingsViewModel.cs` | 注入 `IThemeService`，移除 `using Microsoft.UI.Xaml` |
| `Views/SettingsPage.xaml` | 扫描/排除目录改用 `ItemsSource` + `ItemTemplate` |
| `Views/SettingsPage.xaml.cs` | 删除 `RebuildScanDirsItems` 等手动操作，代码缩减约 60 行 |
| `App.xaml.cs` | DI 注册 `WinUiThemeService` |
| `FluentGallery.Tests.csproj` | 升级到 `net10.0`，添加 `CommunityToolkit.Mvvm`，引入源文件 |
| `SettingsViewModelTests.cs` *(新建)* | 20 个测试覆盖 Add/Remove/Load/Save/默认值/语言映射/清除数据 |

**测试结果：41 个测试全部通过（20 个新 SettingsViewModel 测试 + 21 个原有 DatabaseService 测试）。**

## 扫描目录支持多选目录

`FolderPicker` 原生不支持多选，需要通过 Win32 `IFileOpenDialog` COM API（`FOS_ALLOWMULTISELECT | FOS_PICKFOLDERS`）实现。

**新增/修改文件：**

| 文件 | 变更 |
|------|------|
| `Helpers/MultiFolderPicker.cs` *(新)* | Win32 COM 封装，STA 线程 + `IFileOpenDialog` + `IShellItemArray` vtable 完整定义 |
| `ViewModels/SettingsViewModel.cs` | 新增 `AddScanDirectories(IEnumerable<string>)` 和 `AddExcludeDirectories(IEnumerable<string>)` 批量方法，一次只触发一次 `SaveAsync` |
| `Views/SettingsPage.xaml.cs` | 两个"添加"按钮均改用 `MultiFolderPicker.PickAsync`，返回 `IReadOnlyList<string>` 后调用批量 Add |
| `SettingsViewModelTests.cs` | 新增 5 个测试（批量添加、去重、跨批去重、空列表、持久化） |

用户在资源管理器风格的对话框中按住 **Ctrl** 即可多选文件夹，点击"添加"后所有选中目录一次性写入列表。**46 个测试全部通过。**

## 去掉设置"递归扫描子目录"，这个选项就应该为 True

改动非常简洁，一共三处：

| 文件 | 修改内容 |
|------|--------|
| `SettingsPage.xaml` | 删除"递归扫描子目录"的 `SettingsCard` |
| `SettingsViewModel.cs` | 删除 `RecursiveScan` 可观察属性和 `OnRecursiveScanChanged` 回调；`SaveAsync` 中将 `_settings.RecursiveScan` 硬编码为 `true` |
| `SettingsViewModelTests.cs` | 移除对 `RecursiveScan` 的读写断言，保留对 `loaded.RecursiveScan == true` 的验证（确保持久化时始终写入 `true`） |

`AppSettings.RecursiveScan` 字段本身保留，供后续 `ScanService` 使用，只是从用户界面中隐藏并固定为 `true`。46 个测试全部通过。

## 第一次用户打开应用时，默认的窗口太小了。调大一点。其它桌面应用是如何设置默认窗口大小的？

已有 `AppWindow.Resize(new SizeInt32(1200, 800))` 这行代码，但问题在于 `AppWindow.Resize` 接受的是**物理像素**，而非逻辑像素。在 150% DPI 缩放下，1200 物理像素只有 800 逻辑像素宽，看起来很小。

**修复**：用 DPI 将逻辑尺寸换算成物理像素，并参考屏幕工作区大小做上限限制，最后居中显示：

```
目标逻辑尺寸 1400×900
    → × DPI比例 → 物理像素
    → Min(物理像素, 工作区×90%) → 防止溢出屏幕
    → 居中 MoveAndResize        → 窗口出现在屏幕正中
```

```csharp
double scale = GetDpiForWindow(hwnd) / 96.0;
int physW = (int)Math.Round(1400 * scale);  // 逻辑 → 物理
```

**其他桌面应用的标准做法：**

| 方案 | 适用场景 | 示例 |
|------|---------|------|
| **固定逻辑尺寸** | 功能固定的工具类应用 | 本次实现：1400×900 逻辑像素 |
| **屏幕工作区百分比** | 内容类/媒体类应用 | Windows 文件资源管理器 ≈ 75% 工作区 |
| **记住上次大小/位置** | 文档/编辑器类应用 | VS Code、Notepad++ |
| **自适应内容** | 设置页、对话框 | 根据内容自动 FitToContent |

如果未来想记住上次大小，可以将 `AppWindow.Size` 和 `AppWindow.Position` 存入 `AppSettings` 并在启动时恢复。

## 设置页面的右边超出边界了

问题在于 `ScrollViewer` 设置了 `Padding="32,16,32,32"`，但滚动条本身渲染在 Padding **之外**（紧贴窗口右边框），导致视觉上"溢出"。

**修复**：去掉 ScrollViewer 的右侧 padding，改为内部 StackPanel 的 `Margin`，让滚动条自然靠在窗口右边框内，同时加 `HorizontalScrollBarVisibility="Disabled"` 防止横向溢出。

- **之前**：`ScrollViewer Padding="…32"` → 内容区域缩进 32px，但垂直滚动条在 padding 外侧渲染，导致滚动条超出窗口可见区域
- **之后**：ScrollViewer 右侧 padding 去掉，滚动条自然落在窗口右边框；内容区域的右边距改为 StackPanel 的 `Margin="0,0,32,32"`，视觉间距不变

## 左边有大量空白，右边超出边界了

`StackPanel` 有 `MaxWidth="860"` 但 `HorizontalAlignment` 没有指定，在容器宽于 860 时 WinUI 会将其**水平居中**（Stretch + MaxWidth 的默认行为），造成左侧空白；与此同时 `SettingsExpander` 里的按钮又宽于计算好的宽度，导致右侧溢出。

**修复**：去掉 MaxWidth 和多余约束，让 StackPanel 自然 Stretch 填满可用宽度，ScrollViewer 不加任何 Padding（改为 StackPanel 的 Margin）。

- **ScrollViewer 无 Padding**：垂直滚动条自然贴着页面右边框，不会"溢出"
- **StackPanel 用 Margin 留边距**：四边留白 32px，滚动条在 Margin 外侧，视觉正确
- **去掉 MaxWidth**：StackPanel 用默认 `HorizontalAlignment="Stretch"` 填满可用宽度，不会产生居中留白，也不会溢出右侧

## `dotnet watch run` 这个命令热更新不了

这是 WinUI 3 开发的已知限制，有两个根本原因：

**原因一**：`dotnet watch` 的 hot reload 只处理 C# 代码。XAML 文件变更时它输出 `No C# changes to apply`，然后进入"等待下一次变更"状态，而不是重启应用。

**原因二**：XAML Hot Reload 需要 Visual Studio 的调试桥。WinUI 3 的 XAML 热更新走的是 VS 调试器协议，`dotnet watch` 无法替代。

**解决方案：**

- **方案 A（推荐）**：加 `--no-hot-reload`，任何文件（`.cs` / `.xaml`）变更都触发全量重建 + 重启：
  ```powershell
  dotnet watch run --no-hot-reload --project FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
  ```

- **方案 B**：用 Visual Studio 2022（安装 Windows App SDK 扩展），支持真正的 XAML Hot Reload，修改 `.xaml` 后无需重启即可在运行中的窗口看到效果。

## 在 README 说明这一点（关于热更新）

更新 README 的"Watch mode"一节，加入以下说明：

> **必须加 `--no-hot-reload`。** `dotnet watch` 默认开启 C# hot reload，但 WinUI 3 的 XAML 变更无法通过该机制应用：检测到 `.xaml` 文件变更时会输出 `No C# changes to apply`，然后进入等待状态而**不重启应用**，导致改动看不到效果。加上 `--no-hot-reload` 后，任何文件（`.cs` / `.xaml`）变更都会触发全量重建并自动重启，行为符合预期。
>
> **XAML 实时预览**需要 Visual Studio 2022（安装 **Windows App SDK** 扩展），在调试模式下修改 `.xaml` 可通过 XAML Hot Reload 工具栏即时刷新，无需重启。

## 为什么窗口关闭以后，进程还在运行，导致每次需要去任务管理器结束进程才能继续构建

**根本原因**：WinUI 3 与 WPF/WinForms 不同，`Window.Closed` 只销毁窗口对象，**宿主进程不会自动退出**。这就导致每次关闭窗口后 `FluentGallery.exe` 仍然占用文件锁，下次构建时 `dotnet build` 无法覆盖已加载的 DLL，必须手动结束进程。

**修复**：在 `MainWindow.xaml.cs` 构造函数里加一行：

```csharp
this.Closed += (_, _) => Application.Current.Exit();
```

关闭窗口时 `Exit()` 会正常走完应用生命周期（触发 `UnhandledException` 清理等），进程随即退出，`dotnet watch` 能立即检测到退出并触发下一次重建。同步在 README 中记录了此说明。
