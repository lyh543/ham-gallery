# fluent-gallery

Fluent Gallery is being implemented incrementally from [PROMPT.md](PROMPT.md).

## Current status

- Step 1 completed: WinUI 3 solution scaffold
- Includes solution structure, dependency injection bootstrap, localized resources, and a basic `NavigationView` shell
- Data and feature services are present as placeholders for the next steps

## Prerequisites

To build the app locally, install:

- .NET 9 SDK
- Windows App SDK 1.8 runtime (bundled via NuGet; no separate installer required)
- Windows 10 SDK 10.0.19041.0 or newer (comes with Visual Studio 2022)

## Build & Run

All commands are run from the `FluentGallery/` directory (solution root).

### Restore packages

```powershell
dotnet restore --runtime win-x64
```

### Build (Debug, x64)

```powershell
dotnet build -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
```

### Run directly after build

```powershell
.\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FluentGallery.exe
```

### Build & run in one step

```powershell
dotnet build -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug ; .\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FluentGallery.exe
```

### Watch mode（文件变更自动重建 + 重启）

任何文件（`.cs` / `.xaml`）变更都会触发全量重建并自动重启。

```powershell
dotnet watch run --no-hot-reload --project FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
```

> **XAML 实时预览**需要 Visual Studio 2022（安装 **Windows App SDK** 扩展），在调试模式下修改 `.xaml` 可通过 XAML Hot Reload 工具栏即时刷新，无需重启。

> **关闭窗口后进程仍在运行？** WinUI 3 关闭窗口不会自动退出进程（与 WPF/WinForms 不同），需在代码中显式调用 `Application.Current.Exit()`。本项目已在 `MainWindow.Closed` 事件里处理，关闭窗口即退出进程，`dotnet watch` 可正常检测到退出并重启。

> **Note:** The project uses `WindowsPackageType=None` (unpackaged) so no MSIX packaging or sideloading is needed during development.  
> You can also open `FluentGallery/FluentGallery.sln` in Visual Studio 2022, set the platform to **x64**, and press **F5**.

## Test

All commands are run from the `FluentGallery/` directory (solution root).

### Run all tests

```powershell
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug
```

### Run with detailed output

```powershell
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug --logger "console;verbosity=normal"
```

### Run a single test by name

```powershell
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug --filter "FullyQualifiedName~DeletePhoto_CascadeDeletesThumbnail"
```

## 运行时数据目录

应用以非打包模式（unpackaged）运行，所有运行时数据存放在 `%LocalAppData%\FluentGallery\` 下：

| 路径 | 用途 |
|------|------|
| `%LocalAppData%\FluentGallery\gallery.db` | SQLite 主数据库（相册、照片、设置） |
| `%LocalAppData%\FluentGallery\Thumbnails\` | 生成的缩略图 JPEG 文件 |
| `%LocalAppData%\FluentGallery\logs\` | 滚动日志文件 |
| `%LocalAppData%\FluentGallery\Temp\` | 临时文件（如裁剪前备份） |

在资源管理器地址栏输入 `%LocalAppData%\FluentGallery` 可直接打开该目录。

## Solution layout

- [FluentGallery.sln](FluentGallery.sln)
- [FluentGallery/FluentGallery.csproj](FluentGallery/FluentGallery.csproj)
- [FluentGallery.Tests/FluentGallery.Tests.csproj](FluentGallery.Tests/FluentGallery.Tests.csproj)