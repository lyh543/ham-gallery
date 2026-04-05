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

## Release & Install

### 一键构建并安装到本机

```powershell
make && make install
```

默认安装到 `C:\Tools\FluentGallery`，可通过 `INSTALL_DIR` 覆盖：

```powershell
make install INSTALL_DIR="C:\Apps\FluentGallery"
```

### 分步说明

| 命令 | 说明 |
| ---- | ---- |
| `make` | Release 构建（等同于 `make release ENV=prod`） |
| `make install` | 将已构建的文件镜像复制到 `INSTALL_DIR`（默认 `C:\Tools\FluentGallery`） |

安装完成后，在目标目录找到 `FluentGallery.exe`，右键 → **发送到桌面快捷方式** 即可从桌面启动。

> 本应用以 unpackaged 模式运行（`WindowsPackageType=None`），不会注册到开始菜单，也不支持 Windows 控制面板卸载。若要卸载，直接删除安装目录即可。

## TODO


[] 图片修改日期发生变化时，需要重新读取 exif 并重新生成缩略图。
[] 清除数据库缓存以后立即触发扫描磁盘。
[] 照片详情页，改为全窗口，在左上角添加一个返回，类似于 Windows 的相册 app。 
[] 相册列表和相册详情页面，右上角添加一个放大和缩小按钮，并且将这个大小存到数据库里。支持双指触屏放缩、ctrl+滚轮缩放。相册列表和相册详情共用一个变量。
[] 图片详情页，图片默认应该放大到撑满窗口的宽或高。双击图片时，可以放大图片，如果图片已经是放大状态则还原到刚才那个大小。
   - 右下角添加一个缩放比的 slider，放大到撑满窗口的宽或高定义为 100%。如果缩放比不变且鼠标3s没动静则消失，缩放或鼠标移动时重新出现。类似于 Windows 的相册 app。
   - 添加一个按钮，在文件管理器中查看这张图片。
   - 在其它应用中打开时，应该调用 Windows API，弹出一个弹窗，让用户选择一个应用来打开这张图片。
   - 支持触屏左右滑动、滚轮滑动切到下一张图片
[] 性能问题：预加载了5张照片，但是连续滑动两三张的时候依然会有加载动画出现。
[] 支持多种格式 mp4 mov heic heif gif bmp 透明底
   - 支持 mvimg 格式（Live Photo）
[] 缩略图打成 bundle，（例如 tar，你有更好的打包策略吗？），包大小可设置，默认为 64MB，可选 4-256MB。
   - 支持 treeshaking，即将不存在的图片从包中删除。
[] 在新相册打开图片，提示是否需要加入相册
[] 在设置添加一个选项，为系统注册文件关联。
[] i18n，同时将代码中的注释都改为英文。

-----


针对文件更新，需要订阅父文件夹的更新事件，然后如果有照片文件，则如果有照片文件发生变化。的需要检查是否需要更新据库

照片导入的时候，性能似乎有点问题，应该只需要 LS 获得原信息，然后批量插入数据库即可，但是现在导入的数据大概只有一秒，一两百个。如果是因为获取 exif 信息需要读取每个文件的话，那好像也合理。慢一点就慢一点吧，毕竟导入是一次性的。

相册目录需要维护一个 last sync version，意思是已经完成同步的相册的数据对应的修改时间。然后在相册更新过程中，不应该修改这个值，只有在确保修完全修改。玩相册后，可以更新这个值。后续启动的时候，如果检查到这个值和文件夹的最后修改时间相同。就不再需要扫描整个文件，只需要监听文件夹的更新时间。不过监听导致的文件更新也需要去更新这个 last thing verSiOn。但这里面还有一个一致性的问题，就如果一边读取的时候，相册一边在更新。如何我保证我读取到了更新后的最新版本，有没有出现漏读或者多读数据，这个一致性的问题还要再看看。