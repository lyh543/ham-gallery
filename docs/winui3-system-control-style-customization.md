# WinUI 3 系统控件样式重定义指南

## 结论

对于 **WinUI 3 内置控件的外观定制**，优先通过 `Style` + `ControlTemplate` 重定义系统控件样式，通常就是 best practice。

这也是当前项目里定制右键菜单信息项的方案：

- 保留系统控件类型：仍然使用 `MenuFlyoutItem`
- 基于系统默认样式：`BasedOn="{StaticResource DefaultMenuFlyoutItemStyle}"`
- 仅替换需要变化的视觉模板：例如把默认文本 presenter 换成可控的 `TextBlock`

这不是“重写一个新的系统控件”，而是“复用原控件的属性、状态和资源体系，替换视觉树”。

## 何时这是 best practice

适用场景：

- 需要修改系统控件的视觉结构
- 需要调整文本换行、截断、颜色、行高、间距
- 需要保留原生主题资源、圆角、背景、状态色
- 需要继续使用原控件类型及其公开属性、事件、绑定方式

不适用场景：

- 需要新增控件公开 API
- 需要修改控件行为逻辑，而不是仅改外观
- 需要改变焦点、键盘、子菜单、命令执行等内部机制
- 需要跨多个控件复用一整套全新交互模型

这些场景通常更适合：

- 新建自定义控件
- 组合一个新的 `UserControl`
- 或直接改用 `Flyout` / `Popup`，而不是继续套在原系统控件模板上

## 为什么优先改 Style，而不是重写控件

原因很直接：WinUI 3 的系统控件类本身，主要负责属性、事件、状态入口和内部行为；真正决定视觉表现的是主题资源和模板。

以 `MenuFlyoutItem` 为例：

- `MenuFlyoutItem.cs` 能看到的是 `Text`、`Icon`、`Click`、`Template` 等公开属性
- 默认的外观、padding、foreground、pointer over/pressed 资源，都在 WinUI 的 `generic.xaml` 里

也就是说：

- **类** 决定“它是什么控件”
- **样式和模板** 决定“它看起来像什么”

如果需求只是“像系统控件，但某些文本布局不一样”，改样式通常比重写控件更稳、更便宜，也更符合 WinUI 的设计方式。

## 当前项目采用的方案

当前项目在 [App.xaml](C:/Users/lyh54/git/github/ham-gallery/FluentGallery/App.xaml) 中为 `MenuFlyoutItem` 定义了两套信息项样式：

- `ContextMenuInfoTitleItemStyle`
- `ContextMenuInfoDetailItemStyle`

核心做法：

1. `BasedOn="{StaticResource DefaultMenuFlyoutItemStyle}"`
2. 保留系统资源，例如 `Background`、`BorderBrush`、`CornerRadius`
3. 通过 `ControlTemplate` 将内部文本区替换为真正的 `TextBlock`
4. 使用 `TemplateBinding` 继续复用 `Text`、`Foreground`、`FontSize`、`Padding` 等属性

这样做以后：

- 文件名可以 `Wrap + CharacterEllipsis + MaxLines=3`
- 明细可以单行、更小字号、更紧行高
- 外层依然是 `MenuFlyoutItem`
- 调用代码不需要知道内部模板细节

辅助构建逻辑位于 [ContextMenuHelper.cs](C:/Users/lyh54/git/github/ham-gallery/FluentGallery/Helpers/ContextMenuHelper.cs)。

## 这套方案的关键原则

### 1. 优先 `BasedOn` 默认样式

推荐：

```xaml
<Style x:Key="MyCustomMenuFlyoutItemStyle"
       TargetType="MenuFlyoutItem"
       BasedOn="{StaticResource DefaultMenuFlyoutItemStyle}">
```

这样做的好处：

- 自动继承 WinUI 默认资源体系
- 系统主题切换时更稳
- 不容易丢掉默认的 corner radius、padding、背景等设置
- 维护者可以更清楚地看出“这是在原生基础上的局部改造”

不推荐一上来完全从空模板重写，除非你明确知道哪些默认行为和状态要自己补。

### 2. 优先复用 `ThemeResource`

推荐继续使用系统资源，例如：

- `TextFillColorPrimaryBrush`
- `TextFillColorSecondaryBrush`
- `SubtleFillColorSecondaryBrush`
- `MenuFlyoutItemForeground`
- `MenuFlyoutItemThemePadding`

不要直接把颜色硬编码成固定 RGB，除非它明确不是主题色的一部分。

这样做的目的：

- 跟随亮/暗主题
- 跟随 WinUI 资源演进
- 减少项目内自定义色值碎片化

### 3. 用 `TemplateBinding` 传递控件属性

例如：

```xaml
<TextBlock Text="{TemplateBinding Text}"
           Foreground="{TemplateBinding Foreground}"
           FontSize="{TemplateBinding FontSize}" />
```

这样样式使用者仍然可以通过设置 `MenuFlyoutItem.Text`、`Foreground`、`FontSize` 控制显示，而不是把这些值写死在模板里。

这是模板可维护性的关键。

### 4. 只替换必要的视觉部分

目标应该是：

- 尽可能沿用系统控件原有的资源和状态
- 只替换默认模板做不到的那块

这次我们替换的是文本布局，因为默认 `MenuFlyoutItem.Text` 不足以支持：

- 标题多行换行
- 标题三行截断
- 标题和明细分色
- 不同行高和字号

这就属于“系统外观能力不够，但控件本身仍然适合继续用”的典型场景。

## 如何查找系统控件默认样式

### 1. 看 `generic.xaml`

WinUI 3 的默认样式和模板定义通常在 NuGet 缓存中的 `generic.xaml`。

本机这次定位到的路径是：

[generic.xaml](C:/Users/lyh54/.nuget/packages/microsoft.windowsappsdk.winui/1.8.260224000/lib/native/Microsoft.UI/Themes/generic.xaml)

对于 `MenuFlyoutItem`，重点关注：

- `DefaultMenuFlyoutItemStyle`
- `MenuFlyoutItemThemePadding`
- `MenuFlyoutItemForeground`
- `MenuFlyoutItemTextTrimming`

### 2. 看 metadata-as-source，但不要过度依赖它

像 [MenuFlyoutItem.cs](C:/Users/lyh54/AppData/Local/Temp/MetadataAsSource/ed825a23620d4c52bf6578930fc9cb75/DecompilationMetadataAsSourceFileProvider/1b20b6b7de2e4bd3974bda37d63199cd/MenuFlyoutItem.cs) 这种文件能帮助理解控件公开 API，但通常不能告诉你真正的默认视觉结构。

经验上：

- 想知道控件有哪些属性、事件、模板入口：看 metadata-as-source
- 想知道控件原生长什么样：看 `generic.xaml`

## 维护时推荐的操作流程

1. 明确需求属于“外观变化”还是“行为变化”
2. 如果是外观变化，先找系统控件默认样式和资源键
3. 优先 `BasedOn` 默认样式
4. 尽量保留系统资源和状态组
5. 只替换必要的 presenter 或视觉结构
6. 用 `TemplateBinding` 传递属性，避免写死模板
7. 修改后实际运行，重点看：亮/暗主题、hover、pressed、disabled、文本截断、焦点状态

## 这套方案的边界

虽然这是一种推荐方案，但不是万能方案。

需要注意：

- 替换 `ControlTemplate` 后，你也接手了一部分原生模板维护责任
- 如果模板改得太狠，可能丢失原生 `VisualState`、可访问性或键盘体验
- 原生控件升级后，`generic.xaml` 的内部结构和资源名可能变化

因此建议：

- 简单场景尽量局部修改
- 模板只做必要改动
- 文档里记录“参考了哪个原生样式键和资源键”

## 当前项目对维护者的建议

后续如果还要继续定制系统控件样式，建议遵守下面几条：

- 先判断是不是纯外观问题
- 先看 `generic.xaml`，不要直接猜系统模板怎么写
- 优先新增带明确用途的样式键，而不是直接覆盖全局 `MenuFlyoutItem` 样式
- 只有在全局统一改造时，才考虑直接覆盖系统默认样式
- 如果需求已经明显超出原系统控件边界，及时切换到自定义 `Flyout` / 自定义控件方案

## 一句话总结

在 WinUI 3 里，**对系统控件做外观级定制时，基于默认样式重定义 `Style` / `ControlTemplate`，通常就是 best practice；只有在需求超出模板能力或涉及行为改造时，才应该升级到新控件或新组件。**