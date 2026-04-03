# fluent-gallery

Fluent Gallery is being implemented incrementally from [PROMPT.md](PROMPT.md).

## 运行时数据目录

应用以非打包模式（unpackaged）运行，所有运行时数据存放在 `%LocalAppData%\FluentGallery\` 下：

| 路径 | 用途 |
|------|------|
| `%LocalAppData%\FluentGallery\gallery.db` | SQLite 主数据库（相册、照片、设置） |
| `%LocalAppData%\FluentGallery\Thumbnails\` | 生成的缩略图 JPEG 文件 |
| `%LocalAppData%\FluentGallery\logs\` | 滚动日志文件 |
| `%LocalAppData%\FluentGallery\Temp\` | 临时文件（如裁剪前备份） |

在资源管理器地址栏输入 `%LocalAppData%\FluentGallery` 可直接打开该目录。

## Build & Run Locally

All commands are run from the **project root** directory.

### Prerequisites

To build the app locally, install:

- .NET 10 SDK
- Windows App SDK 1.8 runtime (bundled via NuGet; no separate installer required)
- Windows 10 SDK 10.0.19041.0 or newer (comes with Visual Studio 2022)

### Restore packages

```powershell
dotnet restore FluentGallery\FluentGallery.csproj --runtime win-x64
```

### Build (Debug, x64)

```powershell
make build
# or
dotnet build FluentGallery\FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
```

### Run directly after build

```powershell
make run
# or
.\FluentGallery\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FluentGallery.exe
```

### Build & run in one step

```powershell
make build-run
# or
dotnet build FluentGallery\FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug ; .\FluentGallery\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FluentGallery.exe
```

### Watch mode（文件变更自动重建 + 重启）

任何文件（`.cs` / `.xaml`）变更都会触发全量重建并自动重启。

```powershell
make watch
# or
dotnet watch run --no-hot-reload --project FluentGallery\FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug
```

> **XAML 实时预览**需要 Visual Studio 2022（安装 **Windows App SDK** 扩展），在调试模式下修改 `.xaml` 可通过 XAML Hot Reload 工具栏即时刷新，无需重启。

> **关闭窗口后进程仍在运行？** WinUI 3 关闭窗口不会自动退出进程（与 WPF/WinForms 不同），需在代码中显式调用 `Application.Current.Exit()`。本项目已在 `MainWindow.Closed` 事件里处理，关闭窗口即退出进程，`dotnet watch` 可正常检测到退出并重启。

> **Note:** The project uses `WindowsPackageType=None` (unpackaged) so no MSIX packaging or sideloading is needed during development.  
> You can also open `FluentGallery.sln` in Visual Studio 2022, set the platform to **x64**, and press **F5**.

### Run all tests

```powershell
make test-all
# or
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug
```

### Run with detailed output

```powershell
make test
# or
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug --logger "console;verbosity=normal"
```

### Run a single test by name

```powershell
make test FILTER=DeletePhoto_CascadeDeletesThumbnail
# or
dotnet test FluentGallery.Tests\FluentGallery.Tests.csproj -p:Platform=x64 --runtime win-x64 -c Debug --filter "FullyQualifiedName~DeletePhoto_CascadeDeletesThumbnail"
```

## TODO

[] 照片详情页改为全窗口，在左上角添加一个返回，类似于 Windows 的相册 app。 
[] 相册列表和相册详情页面，右上角添加一个放大和缩小按钮，并且将这个大小存到数据库里。支持双指触屏放缩、ctrl+滚轮缩放。相册列表和相册详情共用一个变量。
[] 图片详情页，图片默认应该放大到撑满窗口的宽或高。双击图片时，可以放大图片，如果图片已经是放大状态则还原到刚才那个大小。
   - 右下角添加一个缩放比的 slider，放大到撑满窗口的宽或高定义为 100%。如果缩放比不变且鼠标3s没动静则消失，缩放或鼠标移动时重新出现。
[] 设置全局的排序顺序和每个相册的排序顺序
[] 支持多种格式 mp4 mov heic heif gif bmp 透明底
[] 缩略图打包
[] 优化流畅度
[] 在新相册打开图片，提示是否需要加入相册
