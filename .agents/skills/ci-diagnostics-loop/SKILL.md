---
name: ci-diagnostics-loop
description: "自动化 CI 日志诊断、问题修复、打 build tag、push、再诊断的循环流程。当 GitHub Actions 构建失败时使用。自动获取失败信息，分析、修复、提交、打 tag、push，然后等待新 CI 运行后重新诊断。循环直至所有 jobs 成功。"
argument-hint: "问题描述或开始修复，例如：fix CI failures，或 check latest run"
---

# CI Diagnostics & Fix Loop

自动化诊断 GitHub Actions Release 工作流失败原因并循环修复。

## 适用场景

- 推送代码后，GitHub Actions Release workflow 失败
- 需要快速诊断失败原因（哪个 job、哪个 step）
- 已经识别出根本原因，需要修改代码并逐次验证修复
- 需要在多个 build tag (v0.1.8.1, v0.1.8.2 等) 上循环测试

## 工作流程

### Phase 1 - 诊断最新 CI 运行

1. **获取 CI 日志**
   ```pwsh
   cd <project-root>
   python .agents\skills\ci-diagnostics-loop\get-release-logs.py
   ```
   
   输出示例：
   ```
   Run ID: 25443611349
   Ref: v0.1.8.0 | Status: failure
   Jobs:
     ✗ build (x64, ...) → Prepare MSIX for upload
     ✓ build (x86, ...) → success
     ○ release → skipped
   ```

2. **分析失败**
   - 记录哪些 jobs 失败、失败在哪个 step
   - 记录失败的 Run ID 和 GitHub Actions URL
   - 如果有多个失败，按依赖关系排序（build → release）

3. **查看完整错误日志**
   - 点击 GitHub Actions 链接查看失败 step 的完整输出
   - 复制错误信息到诊断上下文

### Phase 2 - 根本原因分析

根据错误类型，定位问题：

| 错误类型 | 可能原因 | 检查点 |
|---------|--------|--------|
| `Prepare MSIX for upload` 失败 | MSIX 未生成 / 路径错误 | dotnet build 是否启用签名、证书是否有效、输出目录是否正确 |
| `Build MSIX` 失败 | 编译错误、缺少依赖 | 检查 .csproj 配置、NuGet 包版本、Platform/Runtime 组合 |
| `Create msixbundle` 失败 | bundle 包含错误类型包 / 缺少必要文件 | 用 glob 找到的 .msix/.msixbundle 类型混杂、footprint 文件缺失 |
| `Sign msixbundle` 失败 | 证书无效 / thumbprint 错误 | 证书是否正确导入、thumbprint 是否与 build job 一致 |

### Phase 3 - 修复问题

根据根本原因修改代码：

**常见修复**

1. **MSIX 未生成（最常见）**
   - 问题：`AppxPackageSigningEnabled=false` 可能导致 dotnet 不生成包
   - 修复：改为 `AppxPackageSigningEnabled=true` + restore cert in build job
   - 代码位置：`.github/workflows/release.yml` → build job → "Build MSIX" step

2. **Bundle 包含 .msixbundle**
   - 问题：glob 匹配到了 `.msixbundle`（上一轮打包的结果），导致 MakeAppx 拒绝
   - 修复：只匹配 `.msix`，或添加 `-notmatch "\.msixbundle"` 过滤
   - 代码位置：release job → "Create msixbundle" step

3. **文件路径不存在**
   - 问题：`dist\msix\` 中文件位置与脚本期望不符
   - 修复：在失败 step 前添加诊断：`Get-ChildItem "dist\msix" -Recurse | Select-Object FullName`
   - 代码位置：相应 step 上方新增诊断 step

### Phase 4 - 提交、推送、打 tag

修复后执行四部曲：

```pwsh
# 1. Add & commit
git add -A
git commit -m "fix: <描述修复>"

# 2. Push commit to origin
git push

# 3. 使用 bump-tag.ps1 打新 build tag（会自动 push tag）
.\tools\bump-tag.ps1 -Part build -y

# 输出示例：
# New tag: v0.1.8.1
# Pushed!
```

**为什么用 build part？**
- `-Part patch`: v0.1.7.0 → v0.1.8.0（修复 bug）
- `-Part build`: v0.1.8.0 → v0.1.8.1（CI 测试迭代，不改功能）
- 建议：**总是用 `-Part build`**，因为这些都是 CI/workflow 迭代，不是代码功能修改

### Phase 5 - 等待 & 重诊断

1. **等待 CI 运行**
   - GitHub Actions 自动被 push 的 tag 触发
   - 约 2-5 分钟完成全部 jobs
   - 不需要手动在网页上刷新

2. **重新运行诊断**
   ```pwsh
   python .agents\skills\ci-diagnostics-loop\get-release-logs.py
   ```

3. **检查结果**
   - 如果全部 ✓ success：问题已修复，循环结束 ✅
   - 如果仍有 ✗ failure：返回 Phase 2，分析新错误，继续修复

## 循环例子

```
v0.1.8.0: 3 × build ✗ (Prepare MSIX) → 分析 → 修改 build job 启用签名
v0.1.8.1: 3 × build ✓ + release ✗ (Create msixbundle) → 分析 → 修改 glob 过滤
v0.1.8.2: all jobs ✓ → 修复完毕！
```

## 约束

1. **每次修改只改一个问题**
   - 不要同时修改多个 step，这样无法精确定位哪个改动有效
   - 如果发现新问题，下一轮单独修复

2. **保留诊断日志**
   - 每次诊断前记录 Run ID 和 Failed step 名称
   - 便于回溯问题演化

3. **不要跳过 push**
   - 即使本地修改看起来对，也要 push tag 让 CI 验证
   - 本地环境和 CI runner 可能差异很大（SDK 版本、路径、环境变量等）

4. **CI runner 是 Windows Server（不是本地 Windows 10/11）**
   - 路径分隔符、PowerShell 版本、SDK 位置可能不同
   - 避免硬编码路径或依赖本地环境变量

## 诊断工具

### `python .agents\skills\ci-diagnostics-loop\get-release-logs.py`

公开 API（无需认证）获取 Release workflow 的最新 3 次运行，但有速率限制：

```
无 Token 限制：60 请求/小时
使用 PAT Token：5000 请求/小时
```

#### 遇到 Rate Limit 怎么办？

如果看到：
```
❌ GitHub API rate limited (403 Forbidden)
```

需要提供 GitHub Personal Access Token (PAT)：

1. **创建 PAT**
   - 打开 https://github.com/settings/tokens
   - 点 "Generate new token (classic)"
   - 选择 scopes：`repo`, `read:org`, `read:user`, `read:repo_hook`
   - 复制 token

2. **在 PowerShell 中设置**
   ```pwsh
   $env:GITHUB_PAT = "<your-token-here>"
   ```

3. **重新运行诊断**
   ```pwsh
   python .agents\skills\ci-diagnostics-loop\get-release-logs.py
   ```

#### 获取信息：

```
获取信息：
  - Run ID、Ref (tag)、创建时间
  - 所有 jobs 的状态（success/failure/skipped）
  - 失败 jobs 的失败 step 名称
  - 每个 job 的 GitHub Actions URL（点击看完整日志）

无法获取：
  - 完整的 step 输出（需要点击 Actions UI 查看）
  - 代码行级别的编译错误（需要点击 Actions UI 查看）
```

### 手动查看完整日志

**方法 1：使用 GitHub Web UI（最简单）**

1. 运行 `python .agents\skills\ci-diagnostics-loop\get-release-logs.py` 获取 Run ID 和 Commit SHA
2. 运行 `python .agents\skills\ci-diagnostics-loop\get-check-suites.py <commit_sha>` 获取 Suite ID
   ```pwsh
   python .agents\skills\ci-diagnostics-loop\get-check-suites.py 58379034d74d2903915caf4bc690405c62213b5a
   ```
3. 点击输出中的 `Logs URL`：`https://github.com/lyh543/ham-gallery/suites/<suite-id>/logs?attempt=1`
4. 在浏览器中查看完整的 step 日志

**方法 2：使用 API 获取原始日志**

1. 从 `get-release-logs.py` 获取 Job ID
2. 运行：
   ```pwsh
   python .agents\skills\ci-diagnostics-loop\get-job-logs.py <job_id>
   ```
3. 日志会输出到 stdout，可以重定向到文件或搜索关键词

## 修改代码前的检查清单

每次要修改 `.github/workflows/release.yml` 时：

- [ ] 确认 PowerShell 语法正确（无缩进错误、拼写错误）
- [ ] 确认变量引用正确（`${{ steps.xxx.outputs.yyy }}`）
- [ ] 确认文件路径使用反斜杠（`dist\msix\`），不是正斜杠
- [ ] 确认所有 `Write-Host` 诊断语句保留，便于 debug
- [ ] 本地 git 状态干净（无未提交的文件），这样 commit 只包含修改

## 常见问题

**Q: 为什么要 push tag 让 CI 再跑一遍，不能本地验证吗？**

A: 不能。CI runner 是 Windows Server，不是你的本地机器。差异包括：
- Windows SDK 版本和位置
- .NET SDK 版本（可能有多个版本）
- 环境变量和 PATH 设置
- 硬件配置（CPU 数、GPU）
- 文件系统大小写处理

本地能通过的编译 / 打包，CI 上可能因路径、权限、缓存、依赖而失败。所以必须真实 CI 运行验证。

**Q: 一个 tag 花多长时间？**

A: 从 push tag 到 all jobs 完成约 3-5 分钟：
- GitHub 接收 push：< 1 分钟
- CI runner 启动：< 1 分钟
- 3 × build job 并行：2-3 分钟（并行，不是串行）
- release job（if build passed）：1-2 分钟
- 数据库同步：< 1 分钟

**Q: 如何知道 CI 是否运行完了？**

A: 再跑一次 `python .agents\skills\ci-diagnostics-loop\get-release-logs.py`，看新 tag 的 Created 时间和 Status：
- Status: `completed` → 已完成（看 Conclusion）
- Status: `in_progress` → 仍在运行

**Q: 连续修复多轮，如何快速回到某个历史版本？**

A: 如果某个中间版本（如 v0.1.8.1）的修复是对的，想保留该版本和代码：
```pwsh
# 回到该 tag 的代码
git checkout v0.1.8.1

# 继续从该版本修改，新建分支
git checkout -b fix-from-v0.1.8.1
# ... 修改代码
git add -A && git commit -m "fix: ..."
git checkout main && git merge fix-from-v0.1.8.1
git push

# 或直接跳过某个失败的 tag，用 -f 强推
git tag -d v0.1.8.2  # 本地删除
git push origin :refs/tags/v0.1.8.2  # GitHub 删除
.\tools\bump-tag.ps1 -Part build -y  # 重新打
```

但通常不需要这么复杂，因为每个 tag 都很廉价，只是测试迭代。

