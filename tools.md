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
- `docs/windows-imagegen-design.md`（v1 已实施总纲）+ `docs/windows-imagegen-v2-design.md`（增量设计：能力自描述/图生图/SenseNova；含维护约定）— 生图模块两卷，改前先看卷首职责表
- `docs/windows-tts-design.md` — TTS 专项（Provider 模式同构）

## Environment（版本锁定，改动前必读）
- macOS：Swift 6.3（swift-tools-version 6.0，CI macos-15 + Xcode 16）+ macOS 13+（SwiftUI）；Sparkle 声明 from 2.6.0、Package.resolved 锁定 2.9.4。
- Windows：本机 .NET SDK 10.0.302（MSBuild 用它编译），TFM 锁定 `net8.0` / `net8.0-windows10.0.19041.0`；依赖锁定 WindowsAppSDK 1.6.240923002、Win2D 1.3.0、H.NotifyIcon.WPF 2.1.4、SixLabors.ImageSharp 3.1.11、System.Drawing.Common 8.0.10、System.Speech 8.0.0。
- 注意事项：本项目 API 用法一律以「锁定的依赖版本」为准——同名 API 在不同框架版本签名/行为不同（Win2D 1.x、WindowsAppSDK 1.6、ImageSharp 3.x vs 2.x、Swift 6 严格并发、.NET 8 vs 10 SDK）。写代码/搜参考前先按上述清单核对目标版本，不要用网上其他版本的写法；升级任何依赖前必须全量回归（见 Tools 测试命令）。

## Tools
- macOS 构建/测试：`swift build` / `swift test`（CI：macos-15 + Xcode 16）
- macOS 打包：`./scripts/build-app.sh release`（产物 `build/DesktopPet.app`）；`./scripts/release.sh`、`./scripts/ci-dmg.sh` 为发布链路
- Windows 上验证 Swift 纯逻辑层：`./scripts/verify-core-windows.sh`（git-bash 下，绕过 SwiftPM bug）
- Windows .NET：`dotnet build DesktopPet.sln -p:Platform=x64`；测试项目：`tests/DesktopPet.Core.Tests` / `Infra.Tests` / `Agent.Tests` / `App.Tests`
- Windows 定向验收：`windows-native/scripts/phase6-*.ps1`（smoke/e2e/ui/模型连接/真实对话）；`perf-bench.ps1`（性能基线）
- Windows 图标：`windows-native/scripts/generate-app-icon.ps1` 从 `assets/icon.png` 重新生成多尺寸 `src/DesktopPet.App/Assets/app.ico`（exe 图标/托盘/窗口同源，改图后必须重跑并重新构建）
- 关键入口：macOS `Sources/App/DesktopPetApp.swift`；Windows `windows-native/src/DesktopPet.App`（PetApp.exe）、`src/DesktopPet.AgentHost`（PetAgent.exe）、`src/DesktopPet.Agent`
- 配置/数据：Windows `%APPDATA%/DesktopPet/`（providers.json / logs/ / diary/）；`data/`（petdex 清单）；`Localizations/`（en/zh-Hans/zh-Hant/vi）

## Patterns
- 分层依赖方向：表现层（App）→ 应用层服务 → 领域层（Core）← 基础设施层（Infra）；Core 定义接口，Infra 实现，Core 零 UI 零 IO 可单测。
- Windows 渲染划分：宠物窗口/弹幕自绘渲染器（不走 MVVM），设置/对话 MVVM；新增高频渲染路径沿用此划分。
- Provider 抽象：模型/TTS/生图统一 OpenAI 兼容协议（一个 HttpClient 实现通吃云端与本地 Ollama/vLLM）。
- 对话请求管道：校验→人格拼接→记忆注入→屏幕上下文→模型调用→输出→token 记账→异步画像更新，每步独立可测。
- 迁移惯例：Tauri 旧版 `windows/` 已整体删除，旧 localStorage 数据迁移为 JSON 仓储（适配器一次性迁移）。
- Windows 本地化：Core embedded JSON 为唯一 catalog，四语言 key/placeholder 必须完全同集；`LanguageCoordinator` 先保存再发布；WPF 静态槽位走 `WpfLocalizer`，用户/模型/日记/自定义人格内容必须用 dynamic exclusion。
- Windows 诊断：App/Agent 统一写 `AppDataPaths.Logs`；logger 写盘前脱敏并硬限制单文件，ZIP 导出再脱敏；恢复出厂前必须依次停 Agent/请求、关闭 UI/日志资源，再操作数据目录和 Credential Manager。

## Pitfalls
- **气泡定位**：气泡底锚定「实际可见头顶」（`SpriteRect.Y + ContentTopInset`，Bottom 对齐 + 向上平移 SnugToHeadTop）；不要改回帧矩形/固定 headroom 偏移（不同宠物帧内透明边不同，会压头/悬空）。
- Windows 上 `swift test` 会崩溃（SwiftPM llbuild job bug，见 swiftlang/swift-package-manager#6605），必须用 `./scripts/verify-core-windows.sh` 跑 54 个 core 测试。
- `windows-native/测试.txt` 含真实密钥，已被 .gitignore 忽略；不要读取、不要提交、不要解引用其内容。
- WebView2 透明窗口禁用硬件加速导致动画卡顿（旧 Tauri 版根因）——Windows 版已迁移原生渲染，不要再引入 Web 渲染路径。
- API Key 不得落明文 JSON：Windows 版存 Credential Manager，JSON 只存引用 ID。
- 模型请求并发受固定 worker 池和 P0>P1>P2 优先级队列控制；超时/重试策略由调度层拥有，Provider 只分类 transport 错误。
- WPF 自动文本扫描不得直接覆盖动态内容；即使当前内容恰好等于 catalog key，也必须保持用户/模型原文。
- Agent 代码改动后必须**不带 Platform 参数**构建 AgentHost（`dotnet build src/DesktopPet.AgentHost/...`）：`-p:Platform=x64` 输出 `bin/x64/`，而 `ResolveAgentHostPath` 回退探测 `bin/Debug/`（无 x64），App 实际启动的是后者——否则改 Agent 不生效。
- **App 构建输出陷阱**：sln 平台映射 `Debug|x64 → Debug|Any CPU`，`dotnet build DesktopPet.sln -p:Platform=x64` 与不带 Platform 输出**相同**（`bin/Debug/net8.0-windows10.0.19041.0/win-x64/`）；`bin/x64/` 是历史残留（旧构建），启动 App 前用 `find src/DesktopPet.App/bin -name DesktopPet.App.exe -exec stat -c '%y %n' {} \;` 确认最新时间戳的路径再启动。
- Windows 文件发布/重置/凭据/GraphicsCapture/多屏 DPI 逻辑的单测不等于原生验收；最终报告要显式列出未跑的真实机器 smoke。
