# 版本发布与 GitHub Actions

## 目标与实现方案

推送 `v主版本.次版本.修订版本` 或带预发布后缀的标签后，自动构建 Windows x64 自包含程序，
生成完整 ZIP 与 SHA-256 清单，并发布到 GitHub Releases。

采用两个任务：`build` 只读源码，执行构建、自测和打包；`publish` 在验证同一次运行的构建产物后，
使用内置 `GITHUB_TOKEN` 的 `contents: write` 权限发布。无需配置个人访问令牌。
单任务流程配置更短；拆分任务可以让构建和自测阶段保持只读，因此采用拆分方案。

技术栈：GitHub Actions、Windows runner、.NET 10 SDK、PowerShell 7、GitHub CLI。
`global.json` 将 SDK 限定在稳定的 .NET 10.0 功能带，避免 runner 预装更高主版本时改变所选 SDK。
官方 Actions 固定到完整提交 SHA，升级时同时核对官方版本与运行时要求。

## 实施与验收

- [x] 自测使用隔离的临时主题配置，不依赖开发者机器安装或配置 Codex。
- [x] 统一构建、版本注入、打包、校验与发布入口。
- [x] 配置标签触发与只构建的手动运行。
- [x] 更新中英文使用说明，完成独立 Review、本地验证和实际 Actions 构建验证。

## 发布契约

- 标签如 `v1.0.0-rc4`、`v1.0.0`；标签决定 EXE 信息版本、ZIP 文件名及包内 `VERSION`。
  无需为了标签构建修改源码中的默认版本。带 `-` 后缀的版本标为 prerelease。
- 手动触发工作流只构建并保留 Actions artifact，不创建标签或 Release。
- 发布附件为 EXE、完整 ZIP、启动脚本、`LICENSE`、`LICENSE-codex-token-overlay`、
  `THIRD-PARTY-NOTICES.md`、`SHA256SUMS.txt`。
- ZIP 必须同时包含原始许可证、第三方声明、启动脚本、版本、EXE 校验信息、使用说明与两个日志 fixture。
  只从显式文件清单组包，避免携带开发者的设置或调试日志。
- 发布前校验本地附件和 ZIP 内文件；上传后核对 GitHub 的 SHA-256、附件大小与标签提交，再公开草稿。
- 不覆盖已发布的同名版本，也不替换内容不同的同名附件；失败时保留草稿。
  发布任务失败后优先选择 **Re-run failed jobs**，复用仍在 14 天保留期内的原构建 artifact。
  **Re-run all jobs** 会重新构建，ZIP 时间戳或 SDK 补丁可能使文件哈希改变；若原 artifact 已过期，
  或新的构建与草稿附件不同，应使用新的版本标签，不放宽覆盖保护。
- `release/SHA256.txt` 是已手工发布 rc3 的历史记录。自动构建的校验信息从当次 EXE 生成。

## 维护与验证

发布流程由 `.github/workflows/release.yml`、`tools/prepare-release.ps1` 和
`tools/publish-release.ps1` 共同定义。发布脚本与工作流变更须经过独立 Review。

本地可以指定新的空输出目录构建发布包；校验失败、自测失败或标签不合法时，不进入公开发布步骤。
不在自动化中运行需要真实 Codex 窗口、注册表变更或交互桌面的验收脚本。
