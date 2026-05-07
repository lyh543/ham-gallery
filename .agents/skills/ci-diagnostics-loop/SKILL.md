---
name: ci-diagnostics-loop
description: "自动化 CI 日志诊断、问题修复、打 build tag、push、再诊断的循环流程。当 GitHub Actions 构建失败时使用。"
argument-hint: "问题描述，例如：fix CI failures，或 check latest run"
---

# CI Diagnostics & Fix Loop

自动化诊断 GitHub Actions Release workflow 失败原因并循环修复。

## 工具

本 skill 包含三个 Python 脚本（位于 `.agents/skills/ci-diagnostics-loop/`）：

| 脚本 | 用途 | 需要 PAT |
|------|------|---------|
| `get-release-logs.py` | 获取最新 5 次 Release 运行的状态、job 列表、job ID | 否（60 req/hr），有更快（5000 req/hr） |
| `get-job-logs.py <job_id>` | 获取指定 job 的完整原始日志 | **是** |
| `get-check-suites.py <commit_sha>` | 从 commit SHA 获取 suite ID 和网页日志链接 | 否 |

> **设置 PAT**：`$env:GITHUB_PAT = "<token>"`（支持 `GH_TOKEN` / `HAM_GALLERY_READ_ONLY_PAT`）
> 创建 token：https://github.com/settings/tokens → "Generate new token (classic)" → scopes: `repo`

## 5 阶段工作流

### Phase 1 — 获取最新 CI 状态

```pwsh
cd <project-root>
python .agents\skills\ci-diagnostics-loop\get-release-logs.py
```

输出示例：
```
Run ID: 25443611349  |  Ref: v0.1.8.0  |  Status: failure
Jobs:
  ✗ build (x64)   → failed at: Prepare MSIX for upload
  ✗ build (x86)   → failed at: Prepare MSIX for upload
  ✗ build (arm64) → failed at: Prepare MSIX for upload
  ○ release        → skipped
URL: https://github.com/lyh543/ham-gallery/actions/runs/25443611349
```

从输出中记录：**Run ID**、**failed step 名称**、**job ID**。

### Phase 2 — 提取详细错误日志

使用 job ID（从 Phase 1 输出）获取完整日志：

```pwsh
python .agents\skills\ci-diagnostics-loop\get-job-logs.py <job_id> 2>&1 `
  | Select-String -Pattern "error|Error|throw|failed" -Context 3 `
  | Select-Object -First 80
```

如果只想在浏览器看网页日志，可用 suite 方式：
```pwsh
# 获取 suite 网页日志 URL（从 commit SHA）
python .agents\skills\ci-diagnostics-loop\get-check-suites.py <commit_sha>
# 在浏览器打开输出中的 Logs URL
```

### Phase 3 — 根本原因分析

根据错误定位问题：

| 错误 | 可能原因 | 检查点 |
|------|--------|--------|
| `App package not found under dist\msix` | dotnet build 未生成 MSIX | 检查签名配置、`AppxPackageSigningEnabled` |
| `No app packages found (expected .msix or .msixbundle)` | build job 未上传文件 | 检查 `upload-artifact` 的 `path:` 是否涵盖实际生成的扩展名 |
| `Bundle creation failed` / `0x80080203` / `0x80080204` | 把已打包的 `.msixbundle` 再次打包 | 检查 release job 的逻辑分支是否正确识别 `.msixbundle` |
| `signtool failed` | 证书无效 / thumbprint 不匹配 | 检查 build job restore cert 步骤、secret 是否正确 |

### Phase 4 — 修复 & 提交 & 打 tag

```pwsh
# 1. 修改代码
# 2. 提交
git add -A
git commit -m "fix: <描述>"
# 3. 打 build tag（自动 push，触发 CI）
.\tools\bump-tag.ps1 -Part build -y
```

> `-Part build`: v0.1.8.x → v0.1.8.(x+1)，用于 CI 迭代测试，不是真正的功能修改
> `-Part patch`: v0.1.8.x → v0.1.9.0，用于正式的 bugfix 发布

### Phase 5 — 等待 & 验证

```pwsh
# 等待约 5-7 分钟
Start-Sleep -Seconds 420
python .agents\skills\ci-diagnostics-loop\get-release-logs.py
```

- `Conclusion: success` → 修复完毕 ✅，退出循环
- `Conclusion: failure` → 返回 Phase 2，分析新错误

## 关键经验

1. **每次只改一个问题** — 多处同时修改无法定位哪个有效
2. **上传 glob 要涵盖实际文件扩展名** — `*.msix` 不匹配 `*.msixbundle`
3. **pre-bundled 文件不能再次打包** — dotnet build with signing 会生成 `.msixbundle`，MakeAppx 无法把 bundle 打进 bundle
4. **本地能跑不代表 CI 能跑** — Windows SDK 路径、PowerShell 版本、证书权限等都不同
5. **Rate limit** — 没有 PAT 时 60 req/hr，设置 `$env:GITHUB_PAT` 后 5000 req/hr

## 常用过滤命令

```pwsh
# 过滤 job 日志中的错误
python .agents\skills\ci-diagnostics-loop\get-job-logs.py <id> 2>&1 `
  | Select-String "error|Error|throw" -Context 3 | Select-Object -First 100

# 查看文件列表相关日志
python .agents\skills\ci-diagnostics-loop\get-job-logs.py <id> 2>&1 `
  | Select-String "dist-all|msix|bundle|Found|Using" -Context 1

# 查看所有步骤名（找失败 step）
python .agents\skills\ci-diagnostics-loop\get-job-logs.py <id> 2>&1 `
  | Select-String "##\[group\]" | Select-Object -First 40
```
