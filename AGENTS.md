# Agent Instructions

After modifying any code, run the build and verify it passes:

```
make build
```

If the build fails, read the error output, fix the issues, and rebuild. Repeat until the build succeeds before considering the task done.

## 日志路径

开发模式下，应用的日志位于 `%LocalAppData%\HamGallery-Dev\logs`。如果出现了闪退问题，请优先读取日志，看有没有异常。

## Skills

### ci-diagnostics-loop

- 位置：`.agents/skills/ci-diagnostics-loop/SKILL.md`
- 用途：自动化诊断 GitHub Actions Release workflow 失败、修复问题、打 build tag 并循环验证。
- 触发场景：CI 构建失败、需要快速定位失败原因（哪个 job、哪个 step）、需要逐次修复并验证、修复 CI workflow 配置问题。
- 工作流：获取 CI 日志 → 分析失败原因 → 修改 `.github/workflows/release.yml` → commit → 打 build tag → push → 等待 CI → 重复，直至全部 jobs success。

### translate-resw

- 位置：`.agents/skills/translate-resw/SKILL.md`
- 用途：手工翻译 `FluentGallery/Strings/*/Resources.resw`。
- 触发场景：用户要求补齐 i18n、本地化、翻译 `.resw`、按 chunk 分批翻译、以 English 为准补全缺失或未翻译文案。
- 约束：禁止调用外部翻译 API，禁止编写脚本批量翻译，必须使用 agent 提供的读写文件工具直接修改目标文件。


