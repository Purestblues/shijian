# 项目协作与开发规范 (Project Guidelines)

## Git 与 GitHub 工作流规范 (Git Workflow)

1. **改动后自动提交与推送**：
   - 每次任务改动（代码实现、重构、修复、文档更新等）完成并验证通过后，**自动执行 Git 提交并推送到 GitHub**（`origin main`）。
   - 提交信息（Commit Message）遵循语义化规范（如 `feat:`, `fix:`, `docs:`, `refactor:`, `chore:`），准确概括本次改动内容。

2. **Release 与 Tag 权限约束（严格遵守）**：
   - **严禁擅自创建 Tag 或推送 Release**。
   - 是否发版、发版时机以及 Tag/版本号确定，**必须完全由用户显式决定与指示**。
   - 日常改动仅执行分支推送（`git push origin main`），切勿打 Tag 或推送 `v*` 标签，避免无意触发 GitHub Actions Release CI 构建与发版。

## 验证与测试规范 (Verification)

- 提交代码前，需根据改动范围执行对应测试以确保主线质量：
  - C# 客户端及核心逻辑：`dotnet test ShiJian.Tests/ShiJian.Tests.csproj -c Release --nologo`
  - Rust 服务端：`cargo test`（位于 `shijian-server/`）
  - 前端/插件模块：`npm test`（位于对应插件或 webui 目录）

## GUI 自动化验证规范 (/gui-verify)

- 自动化界面验证时，运行 `pwsh -File script/dev-run.ps1`（脚本在 Agent 环境下会自动采用分离模式拉起最新编译客户端并等待就绪）。
- 如仅涉及前端 UI / 客户端改动，推荐追加 `-ClientOnly`（复用已有服务端，避免触发 UAC 提权并大幅加快就绪速度）。
- 启动后使用 `gui_controller` MCP（`gui_list_windows` -> `gui_inspect_window` -> `gui_capture` -> `gui_act`）进行首屏定位与交互验证。
- 验证完毕后，调用 `gui_close_window` 关闭窗口，或调用 `pwsh -File script/dev-run.ps1 -Stop` 停止调试环境并恢复安装版服务。
