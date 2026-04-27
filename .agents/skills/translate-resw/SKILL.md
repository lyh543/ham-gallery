---
name: translate-resw
description: "手工翻译 FluentGallery 的 RESW 资源文件。用于补齐 i18n、本地化、翻译 .resw、按 chunk 分批翻译、以 English 为准修复缺失字段或未翻译的英文/中文字段。禁止调用外部翻译 API，禁止编写脚本批量翻译，必须直接用 agent 的文件读写工具修改资源文件。"
argument-hint: "目标语言或范围，例如：ja-JP settings/common，或 ja-JP, ko-KR, de-DE, fr-FR, es-ES"
---

# Translate RESW

对 `FluentGallery/Strings/<locale>/Resources.resw` 做手工翻译。

## 适用场景

- 用户要求给新语言补齐 i18n
- 用户要求翻译或校对 `.resw` 资源
- 用户要求“不要用 API 翻译”“自己手工翻译”
- 需要把英文基准资源同步到其它语言
- 需要分 chunk 逐批完成大量翻译文本

## 硬性约束

1. 以 `FluentGallery/Strings/en-US/Resources.resw` 为唯一基准。
2. 只能使用 agent 提供的读取、搜索、补丁编辑工具。
3. 不得调用任何外部翻译 API、网页翻译服务、第三方翻译 CLI。
4. 不得编写 PowerShell、Python、Node 等脚本去自动翻译文本。
5. 只翻译以下情况：
   - 目标语言缺少 English 中存在的字段
   - 目标语言字段仍是英文原文
   - 目标语言字段仍是中文占位或混入中文
   - 目标语言字段明显未完成、机器痕迹重、与当前语言不一致
6. 已经是正确母语且质量可接受的字段，不要重复改写。
7. 修改时必须保留 XML 结构、key 名称、占位符、换行和转义。

## 工作流程

### Step 1 - 确定范围

先明确：

- 目标语言，例如 `ja-JP`、`ko-KR`、`de-DE`
- 目标 key 范围，例如 `SettingsPage_`、`Settings_`、`PhotoDetail_Toast_`
- 是否要求补全整个文件，还是只处理常用文案

如果用户没有限制范围，优先从最常见、最直接可见的文案开始：

1. `SettingsPage_`
2. `Settings_`
3. `Common_`
4. `PhotoDetail_Toast_` / `PhotoDetail_Delete_` / `PhotoDetail_IndexPrompt_`
5. `AlbumList_CreateAlbum_` / `AlbumList_DeleteConfirm_` / `AlbumList_Context_`
6. `AllPhotos_DeleteConfirm_` / `PhotoList_DeleteConfirm_`
7. `Search_`

### Step 2 - 对照 English 与目标语言

读取 English 基准文件和目标语言文件，逐段对照。

对每个 key，按下面规则判断是否需要翻译：

- 目标文件没有该 key：需要补上
- value 与 English 相同：视为未翻译，需要翻译
- value 是中文而目标语言不是中文：视为未翻译，需要翻译
- value 只有局部翻译，关键按钮/标题仍是英文：需要修正

不要因为措辞偏好而大面积重写已经正确的译文。

### Step 3 - 逐语言 + 分 chunk 处理

**禁止同时修改多个语言文件。** 每次只处理一个语言，完成后运行 `validate_all.py` 确认通过，再开始下一个语言。这样可以保持上下文专注，避免跨语言的混淆和批量引入错误。

所有语言都修复完毕、`validate_all.py` 全部通过、`make build` 成功后，才可以回复用户。在此之前，不要输出总结或询问用户下一步。

禁止一次处理整份大文件。

分 chunk 规则：

- 每个 chunk 建议 20 到 60 个 key
- 单个 chunk 只处理一个连续语义区块，避免混杂过多页面
- 优先按前缀或页面分块，而不是随机抽取

推荐 chunk 划分：

1. `SettingsPage_` 一块或多块
2. `Settings_` 一块
3. `Common_` + 常用对话框 一块
4. `PhotoDetail_*` 一块
5. `Search_` 一块

每完成一个 chunk：

- 立即保存到目标 `.resw`
- 再开始下一个 chunk

### Step 4 - 手工翻译要求

翻译时遵守这些规则：

- 保留 `{0}`、`{1}` 等占位符原样不动
- 保留换行、问号、引号语义
- 产品名、技术名、格式名一般不翻：如 `GitHub`、`HEIC`、`HEIF`、`ISO`、`GPS`
- 按 UI 语境翻译，优先短、稳、自然，适合按钮/卡片/对话框
- 同一概念前后一致，例如：
  - Delete / Remove / Clear / Reset 需区分语义
  - Album、Photo、Thumbnail、Cache、Index、Logs 在同语言内保持统一术语
- 若 English 是提示或状态文案，目标语言也要保持同样的语气，不擅自加强或弱化

### Step 0 - 开始前校验

在开始任何翻译或修复前，先运行统一校验脚本，确认当前资源文件结构和 key 覆盖状态。

```bash
python .agents/skills/translate-resw/validate_all.py
```

如果开始前执行失败，先根据日志修复 XML 或补齐缺失 key，再进入翻译工作。

### Step 5 - 格式校验

在完成翻译或修改后，必须再次运行统一校验脚本，确保 XML 格式正确、无重复 Key，并且没有新的缺失 key。

```bash
python .agents/skills/translate-resw/validate_all.py
```

如果结束时执行失败，说明翻译后又引入了结构问题或遗漏 key。必须根据日志重新检查、修复，然后再次运行统一校验脚本，直到通过为止，再执行 `make build`。

### Step 5 - 文件修改方式

必须直接编辑 `.resw` 文件。

允许：

- 使用读取工具查看 English 和目标语言片段
- 使用补丁工具追加缺失的 `<data ...>` 节点
- 使用补丁工具替换未翻译的 `<value>...</value>`

禁止：

- 生成中间翻译脚本再运行
- 把资源导出到 JSON/CSV 再批处理回写
- 调用任何联网翻译接口

### Step 6 - 质量检查

每轮翻译完成后检查：

1. 所有修改仍是合法 XML
2. 没有改坏 `name="..."`
3. 占位符数量和顺序未变化
4. 没有把 `&amp;`、引号、换行误改坏
5. 同一区块术语一致

如果这次任务改动了应用代码或资源并且仓库要求构建验证，则执行项目规定的构建命令确认没有引入构建问题。

### Step 7 - 输出总结

完成后向用户总结：

- 处理了哪些语言
- 处理了哪些 chunk / key 前缀
- 新增了多少缺失字段
- 修正了多少未翻译字段
- 是否执行了构建验证，结果如何

若仍有剩余未翻译范围，也要明确列出下一批建议处理的 chunk。

## 建议术语

- `Clear` 和 `Delete` 不混用
- `Cache` 优先译为“缓存”对应术语，而不是随意弱化
- `Index` 在本项目中偏向“索引”而不是“目录”
- `Open Folder` 优先按系统 UI 习惯翻译
- `Follow system` 应保留“跟随系统”语义

## 完成定义

当且仅当以下条件满足，才算完成：

1. 指定范围内的缺失字段已补齐
2. 指定范围内残留英文或中文的字段已改为目标语言
3. 修改已按 chunk 落盘到目标 `.resw`
4. `validate_all.py` 对所有语言全部输出通过
5. `make build` 成功
6. 所有语言处理完毕后，统一向用户给出一次简洁总结

**未满足上述所有条件前，不得回复用户。**