# Ham Gallery（丸灰相册）

Ham Gallery is being implemented incrementally from [PROMPT.md](PROMPT.md).

## 安装方法

### 从应用商店安装

暂时未提供商店链接。

### 从 GitHub Release 下载
从 [GitHub Releases](https://github.com/lyh543/ham-gallery/releases) 下载最新发布版本。

- 选择 `.msix` 包可直接安装并注册到系统
- 选择便携 `.zip` 包可解压后直接运行

### Clone 代码后本地安装

```powershell
git clone https://github.com/lyh543/ham-gallery.git
cd ham-gallery
make && make install
```

## 运行时数据目录

应用以非打包模式（unpackaged）运行，所有运行时数据存放在 `%LocalAppData%\HamGallery\` 下：

| 路径 | 用途 |
|------|------|
| `%LocalAppData%\HamGallery\gallery.db` | SQLite 主数据库（相册、照片、设置） |
| `%LocalAppData%\HamGallery\Thumbnails\` | 生成的缩略图 JPEG 文件 |
| `%LocalAppData%\HamGallery\logs\` | 滚动日志文件 |
| `%LocalAppData%\HamGallery\Temp\` | 临时文件（如裁剪前备份） |

在资源管理器地址栏输入 `%LocalAppData%\HamGallery` 可直接打开该目录。

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
make build run
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

## Publish & Install

### MSIX 安装链路

```powershell
make && make install
```

等价于：

```powershell
make msix-signed && make install-msix
```

- `make` 默认执行 `make msix-signed`
- `make msix-signed` 会先执行幂等的 `make cert-create`，再生成已签名 MSIX
- `make install` 默认执行 `make install-msix`
- `make install-msix` 会先执行幂等的 `make uninstall-msix`，再安装现有 MSIX 产物，不会重新编译
- `make uninstall` 默认执行 `make uninstall-msix`

### 便携目录复制链路

```powershell
make publish && make install-publish
```

默认复制到 `C:\Tools\FluentGallery`，可通过 `INSTALL_DIR` 覆盖：

```powershell
make install-publish INSTALL_DIR="C:\Apps\FluentGallery"
```

### 分步说明

| 命令 | 说明 |
| ---- | ---- |
| `make` | 默认生成已签名 MSIX（等同于 `make msix-signed`） |
| `make install` | 默认执行 `make install-msix` |
| `make uninstall` | 默认执行 `make uninstall-msix` |
| `make cert-create` | 生成或复用本地 MSIX 签名证书 |
| `make msix-signed` | 先执行 `cert-create`，再构建已签名 MSIX |
| `make install-msix` | 先卸载旧 MSIX，再信任证书并安装现有 MSIX 产物，不重新编译 |
| `make uninstall-msix` | 卸载当前用户已安装的 Ham Gallery MSIX |
| `make publish` | 自包含发布，可配合 `ARCH` 或 `ENV` 使用 |
| `make install-publish` | 将已发布文件镜像复制到 `INSTALL_DIR`（默认 `C:\Tools\FluentGallery`） |
| `make zip` | 从 `make publish` 的输出生成便携 ZIP |

MSIX 安装完成后，应用会注册到系统，可从开始菜单启动和卸载。

便携目录复制安装完成后，在目标目录找到 `FluentGallery.exe`，右键 → **发送到桌面快捷方式** 即可从桌面启动。

> `make publish && make install-publish` 以 unpackaged 模式运行（`WindowsPackageType=None`），不会注册到开始菜单，也不支持 Windows 控制面板卸载。若要卸载，直接删除安装目录即可。
> GitHub Action 构建的 Release 版本会提供 MSIX 和 ZIP 两种格式。

## TODO

[] 清除全部数据 -》 清除应用数据
[] 如果缩略图生成进度没生成完，回到设置以后要能看到之前的缩略图生成进度
[] 生成缩略图的优先级有问题，导致打开的目录的缩略图是黑的（写一个优先队列来解决？）。不对，为什么图片列表里看得到缩略图，但是图片详情就是黑的？
[] 图片详情页支持复制、移动、重命名
[] 支持多种格式 mp4 mov heic heif gif bmp 透明底
   - 支持 mvimg 格式（Live Photo）
[] 编写 README，添加宣传视频


-----


针对文件更新，需要订阅父文件夹的更新事件，然后如果有照片文件，则如果有照片文件发生变化。的需要检查是否需要更新据库

照片导入的时候，性能似乎有点问题，应该只需要 LS 获得原信息，然后批量插入数据库即可，但是现在导入的数据大概只有一秒，一两百个。如果是因为获取 exif 信息需要读取每个文件的话，那好像也合理。慢一点就慢一点吧，毕竟导入是一次性的。

相册目录需要维护一个 last sync version，意思是已经完成同步的相册的数据对应的修改时间。然后在相册更新过程中，不应该修改这个值，只有在确保修完全修改。玩相册后，可以更新这个值。后续启动的时候，如果检查到这个值和文件夹的最后修改时间相同。就不再需要扫描整个文件，只需要监听文件夹的更新时间。不过监听导致的文件更新也需要去更新这个 last thing verSiOn。但这里面还有一个一致性的问题，就如果一边读取的时候，相册一边在更新。如何我保证我读取到了更新后的最新版本，有没有出现漏读或者多读数据，这个一致性的问题还要再看看。