---
name: fix-compile-xaml
description: >-
  诊断并修复本项目（WinUI 3 / .NET + CommunityToolkit.WinUI）中的 XAML 或 C# 编译错误。
  在用户报告编译失败、build 报错、XamlCompiler.exe exit code 1，或提到 CS 错误代码时使用。
  修复后必须实际运行编译命令确认 0 错误 0 警告再回复用户。
---

# Fix Compile (XAML & C#)

## 工作流程

### Step 1 — 获取精确错误信息

读取终端文件获取最新的 `dotnet build` 输出：

```powershell
# 项目根目录执行
dotnet build FluentGallery\FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug 2>&1
```

- 若终端输出只有 `XamlCompiler.exe 已退出，代码为 1`，而无具体行号，先修复所有 C# 错误再重新编译——XAML 编译器错误常为 CS 错误的级联结果。
- 若需要查看 XAML 编译器详细输出，读取：`FluentGallery\obj\x64\Debug\net10.0-windows10.0.19041.0\win-x64\output.json`

#### `dotnet build` 看不到 WMC 错误的原因与解决

**背景**：`dotnet build` 使用 .NET Core MSBuild，WinUI XAML 编译通过 out-of-process 的 `Exec` 任务调用 `XamlCompiler.exe`（由 `UseXamlCompilerExecutable=true` 控制）。当 XamlCompiler.exe 退出码为 1 时，`Exec` 任务立即报 MSB3073 失败，后续的 `OutputDeserializer`（负责将 output.json 中的 WMC 错误转为 MSBuild error）不会执行，导致具体的 WMC 错误信息丢失。

**Visual Studio** 使用 .NET Framework MSBuild，走 in-process 的 `CompileXaml` task，WMC 错误直接作为 MSBuild error 输出，因此 VS 始终能看到详细报错。

**解决方法**：用 VS 安装附带的 `MSBuild.exe` 来编译，即可在命令行获取完整 WMC 错误：

```powershell
# 查找 VS MSBuild 路径
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -property installationPath
$msbuild = "$vsPath\MSBuild\Current\Bin\amd64\MSBuild.exe"

# 用 VS MSBuild 编译，获取 WMC 错误
& $msbuild FluentGallery\FluentGallery.csproj /p:Platform=x64 /p:Configuration=Debug /p:RuntimeIdentifier=win-x64 /p:DevBuild=true /restore 2>&1 | Select-String "WMC|error "
```

> **注意**：`-p:UseXamlCompilerExecutable=false` 在 `dotnet build` 下**不可用**（CompileXaml task 与 .NET Core MSBuild 不兼容，会报 MSB4061）。只能用 VS 的 `MSBuild.exe`。

### Step 2 — 诊断常见错误

#### CS0104：命名空间歧义

**症状**：`error CS0104: "Foo" 是 "A.Foo" 和 "B.Foo" 之间的不明确的引用`

**根本原因**：两个 `using` 导入了同名类型。本项目的高频冲突：

| 冲突类型 | 来源 A | 来源 B |
|---|---|---|
| `AppDataPaths` | `FluentGallery.Helpers` | `Windows.Storage` |

**修复**：在冲突文件顶部添加 using 别名，指向项目内的类型：

```csharp
using AppDataPaths = FluentGallery.Helpers.AppDataPaths;
```

---

#### XAML：SettingsExpander 多子元素错误

**症状**：XamlCompiler.exe exit code 1，无具体行号

**根本原因**：`SettingsExpander` 的 `ContentProperty` 是 `Content`（单值）。直接写多个子元素（如 `StackPanel` + 多个 `SettingsCard`）会触发 XAML 解析错误。

**错误写法**：

```xml
<ctk:SettingsExpander ...>
    <StackPanel />         <!-- 映射到 Content -->
    <ctk:SettingsCard />   <!-- 第二个子元素 → 编译失败 -->
</ctk:SettingsExpander>
```

**正确写法**：用属性元素语法显式分离 `Content` 和 `Items`：

```xml
<ctk:SettingsExpander ...>
    <ctk:SettingsExpander.Content>
        <StackPanel />
    </ctk:SettingsExpander.Content>

    <ctk:SettingsExpander.Items>
        <ctk:SettingsCard ... />
        <ctk:SettingsCard ... />
    </ctk:SettingsExpander.Items>
</ctk:SettingsExpander>
```

> `x:Bind` 在 `<ctk:SettingsExpander.Items>` 内的 `SettingsCard` 中可正常绑定页面 ViewModel，无需 `x:DataType`。

---

#### WMC0055：枚举值不匹配

**症状**：`XamlCompiler error WMC0055: Cannot assign text value 'X' into property 'Y' of type 'Z'`

**根本原因**：XAML 中给属性赋了一个不存在于目标枚举的值。常见于混淆了相似枚举：

| 属性 | 所属控件 | 枚举类型 | 可选值 |
|---|---|---|---|
| `LabelPosition` | `AppBarButton` | `CommandBarLabelPosition` | `Default`, `Collapsed` |
| `DefaultLabelPosition` | `CommandBar` | `CommandBarDefaultLabelPosition` | `Bottom`, `Right`, `Collapsed` |

**典型错误**：在 `AppBarButton` 上写 `LabelPosition="Right"`——`Right` 属于 `CommandBarDefaultLabelPosition`，不属于 `CommandBarLabelPosition`。

**修复**：将 `LabelPosition="Right"` 改为 `DefaultLabelPosition="Right"` 并移到父级 `CommandBar` 上；或从 `AppBarButton` 上移除该属性。

---

#### XAML：x:Bind 在 Items DataTemplate 之外失效

若 `SettingsCard` 是通过 `ItemsSource` + `ItemTemplate` 渲染的，`x:Bind` 必须配合 `x:DataType` 使用。静态 `Items` 子元素（无 DataTemplate）则绑定页面 ViewModel。

---

### Step 3 — 验证修复

修复后**必须**重新编译并确认输出：

```powershell
dotnet build FluentGallery\FluentGallery.csproj -p:Platform=x64 --runtime win-x64 --no-self-contained -c Debug 2>&1
```

确认输出包含 `已成功生成` 且 `0 个错误` 后再回复用户。

## 注意事项

- 不要仅靠 `ReadLints` 判断 XAML 错误，XAML 编译器报错不会出现在 Lint 结果中。
- `XamlCompiler.exe exit code 1` 几乎都是 XAML 语法错误或级联的 CS 错误，先解决 CS 错误。
- 每次修复后必须实际编译，不能只靠代码审查就回复用户"已修复"。
