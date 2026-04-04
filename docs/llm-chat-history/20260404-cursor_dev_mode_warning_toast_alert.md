# Dev mode warning toast alert
_Exported on 4/4/2026 at 18:32:31 GMT+8 from Cursor (2.6.22)_

---

**User**

在 dev 模式下，如果出现 warning 日志就弹一个 toast 告警，并且加一个选项（本次启动都不再提示）

---

**Cursor**

了解了项目结构。接下来读取关键文件，制定实现方案。

好，我已经充分了解了项目。现在开始实现：创建自定义日志拦截器 → 修改 `MainWindow` 添加 Toast 覆盖层 → 注册 provider。

现在在 `MainWindow.xaml.cs` 末尾（`}` 前）添加 Toast 相关方法：

等等，`using FluentGallery.Helpers` 已经存在了，撤销多余的 using：

现在把 `DevWarningLoggerProvider` 注册到日志系统：

现在检查 lint 错误：

现在读取技能文件，按照固定流程编译验证：

应用正在运行导致 .exe 文件被锁，代码编译本身没有 CS/XAML 错误。用一个临时输出目录绕过文件锁：

**已成功生成，0 个错误。** 清理临时输出目录：

编译通过（0 个错误）。以下是本次改动的完整说明：

---

## 变更概述

### 新增文件

**`FluentGallery/Helpers/DevWarningLoggerProvider.cs`**

整个文件包在 `#if DEBUG` 内，不影响 Release 构建。实现了一对 `ILoggerProvider` + `ILogger`：
- 拦截所有 `LogLevel.Warning` 及以上的日志
- 通过静态事件 `DevWarningLoggerProvider.WarningLogged` 将 `(category, message)` 广播给订阅者
- 可能在任意后台线程触发，订阅者自行切回 UI 线程

### 修改文件

**`FluentGallery/MainWindow.xaml`**

在 `NavigationView` 外层加了一个 `<Grid>` 作为容器，并在其上叠加了一个橙色 Warning Toast：
- 位置：右上角（`VerticalAlignment="Top"` + `HorizontalAlignment="Right"`）
- 第一行：警告图标 + 消息文本 + 关闭按钮（×）
- 第二行：`本次启动不再提示` 超链接按钮

**`FluentGallery/MainWindow.xaml.cs`**（仅 `#if DEBUG` 块）

- 构造函数中订阅 `DevWarningLoggerProvider.WarningLogged` 事件，`Closed` 时取消订阅（防泄漏）
- `OnDevWarningLogged`：判断是否已静音，通过 `DispatcherQueue.TryEnqueue` 切回 UI 线程
- `ShowDevWarnToast`：更新文本、淡入动画、启动 8 秒自动关闭计时器
- `HideDevWarnToast`：停止计时器、淡出动画
- `DevWarnClose_Click` → `HideDevWarnToast()`
- `DevWarnMute_Click` → 设置 `_devWarnMuted = true`，本次启动后续所有 Warning 都不再弹出

**`FluentGallery/App.xaml.cs`**

```csharp
services.AddLogging(logging =>
{
    logging.SetMinimumLevel(LogLevel.Debug);
    logging.AddSerilog(dispose: true);
#if DEBUG
    logging.AddProvider(new DevWarningLoggerProvider());
#endif
});
```

所有现有的 `_logger.LogWarning(...)` 调用无需任何修改，会自动触发 Toast。

