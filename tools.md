# DesktopPet Tools

## Concepts
- 本文件记录项目协作知识，不替代正式设计文档或当前代码。
- 真值优先级：`docs/` 正式文档（`windows-architecture.md` 为 Windows 版总纲）→ README.md → 当前代码。
- 双线结构：macOS 版（SwiftPM，根目录）与 Windows 版（`windows-native/`，.NET 8 + WPF）并存，改动前先确认目标平台。

## Read First
- `docs/windows-architecture.md` — Windows 版分层/模式/Provider/管道总纲
- `docs/windows-migration-plan.md` — Tauri → .NET 8 迁移路线与 Phase
- `docs/windows-ui-design.md` — Lumen 视觉与 UI 验收清单
- `docs/ai-personas.md` — 人格设计
- README.md — macOS 版功能与构建说明

## Tools
- macOS 构建/测试：`swift build` / `swift test`（CI：macos-15 + Xcode 16）
- macOS 打包：`./scripts/build-app.sh release`（产物 `build/DesktopPet.app`）；`./scripts/release.sh`、`./scripts/ci-dmg.sh` 为发布链路
- Windows 上验证 Swift 纯逻辑层：`./scripts/verify-core-windows.sh`（git-bash 下，绕过 SwiftPM bug）
- Windows .NET：`dotnet build DesktopPet.sln -p:Platform=x64`；测试项目：`tests/DesktopPet.Core.Tests` / `Infra.Tests` / `Agent.Tests` / `App.Tests`
- Windows 定向验收：`windows-native/scripts/phase6-*.ps1`（smoke/e2e/ui/模型连接/真实对话）；`perf-bench.ps1`（性能基线）
- 关键入口：macOS `Sources/App/DesktopPetApp.swift`；Windows `windows-native/src/DesktopPet.App`（PetApp.exe）、`src/DesktopPet.AgentHost`（PetAgent.exe）、`src/DesktopPet.Agent`
- 配置/数据：Windows `%APPDATA%/DesktopPet/`（providers.json / logs/ / diary/）；`data/`（petdex 清单）；`Localizations/`（en/zh-Hans/zh-Hant/vi）

## Patterns
- 分层依赖方向：表现层（App）→ 应用层服务 → 领域层（Core）← 基础设施层（Infra）；Core 定义接口，Infra 实现，Core 零 UI 零 IO 可单测。
- Windows 渲染划分：宠物窗口/弹幕自绘渲染器（不走 MVVM），设置/对话 MVVM；新增高频渲染路径沿用此划分。
- Provider 抽象：模型/TTS/生图统一 OpenAI 兼容协议（一个 HttpClient 实现通吃云端与本地 Ollama/vLLM）。
- 对话请求管道：校验→人格拼接→记忆注入→屏幕上下文→模型调用→输出→token 记账→异步画像更新，每步独立可测。
- 迁移惯例：Tauri 旧版 `windows/` 已整体删除，旧 localStorage 数据迁移为 JSON 仓储（适配器一次性迁移）。

## Pitfalls
- Windows 上 `swift test` 会崩溃（SwiftPM llbuild job bug，见 swiftlang/swift-package-manager#6605），必须用 `./scripts/verify-core-windows.sh` 跑 54 个 core 测试。
- `windows-native/测试.txt` 含真实密钥，已被 .gitignore 忽略；不要读取、不要提交、不要解引用其内容。
- WebView2 透明窗口禁用硬件加速导致动画卡顿（旧 Tauri 版根因）——Windows 版已迁移原生渲染，不要再引入 Web 渲染路径。
- API Key 不得落明文 JSON：Windows 版存 Credential Manager，JSON 只存引用 ID。
- 模型请求并发受限流：用 SemaphoreSlim(3) 并发闸 + P0>P1>P2 优先级队列，对话 30s/互动 8s 超时。
