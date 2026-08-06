# DesktopPet AGENTS

## 0. 实施基线
- 环境：macOS 13+（Xcode 16 / Swift 6）；Windows（VS 2022 Build Tools + .NET 8 SDK）
- 语言/运行时：Swift 6 + SwiftUI（macOS 主版，SwiftPM）；C# .NET 8 + WPF（windows-native 版）
- 主要技术栈：SwiftPM + Sparkle 自动更新；.NET 8 + WPF + Win2D + Windows.Graphics.Capture
- 配置根目录：Windows 为 `%APPDATA%/DesktopPet/`（providers.json、logs/、diary/）；macOS 为 App 内部设置 + UserDefaults
- 标准验证命令：
  - macOS：`swift build` / `swift test`（CI 为 macos-15 + Xcode 16）
  - macOS 打包：`./scripts/build-app.sh release`
  - Windows 上验证 Swift 纯逻辑层：`./scripts/verify-core-windows.sh`（git-bash 下）
  - Windows .NET：`dotnet build DesktopPet.sln`（x64）

## 1. 作用
- 本文件是项目级硬规则，只保留长期约束实现结构的内容。
- 详细设计、字段说明、流程细节统一回到 `docs/` 正式文档。

## 2. Source of Truth
- 优先级：`docs/` 正式文档（`windows-architecture.md` 为 Windows 版总纲）→ README.md → 当前代码 → 本文件。
- macOS 版无独立 spec 文档，以 README 与当前代码为准。
- `tools.md` / `memory.md` / `memory/archive/` 只做协作辅助，不覆盖正式设计真值。

## 3. 架构与项目结构
- 双平台双仓库线，同一 repo：
  - macOS 版（根目录，SwiftPM）：`Sources/DesktopPetCore/`（纯逻辑：养成/活动模型/情绪/休息时钟/事件总线，平台无关，可单测）→ `Sources/App/`（SwiftUI 可执行目标 `desktoppet`，菜单栏应用）；`Tests/` 下两组测试
  - Windows 版（`windows-native/`，.NET 8 + WPF）：`DesktopPet.sln` 含 `DesktopPet.App`（WPF 表现层）、应用层服务、`DesktopPet.Core`（领域层，零 UI 零 IO）、`DesktopPet.Infra`（基础设施，依赖倒置）、`DesktopPet.Agent` / `DesktopPet.AgentHost`（截屏/分析独立进程）
- 依赖方向：表现层 → 应用层 → 领域层 ← 基础设施层；`DesktopPet.Core` 不依赖 WPF/IO。
- 关键入口：macOS `Sources/App/DesktopPetApp.swift`；Windows `DesktopPet.App`（PetApp.exe）+ `DesktopPet.AgentHost`（PetAgent.exe）。

## 4. Agent 协作文件规则
- 开始任务前，优先读取项目根 `tools.md` 与 `memory.md`。
- 只读取当前项目根协作文件；`memory/archive/` 只按需读取当月或最近一个归档文件，不批量加载历史。
- `tools.md` 记录稳定可复用的命令、路径、模式、坑点；写入前先读。
- `memory.md` 是当前状态快照 + 最近活跃窗口；完整功能/修复闭环后整体覆盖更新，不做流水追加。
- `memory.md` 只保留：当前基线 / 已完成能力 / 进行中 / 关键决策 / 坑点 / 最近活跃窗口；移除失效项，目标 ≤120 行。
- `memory/archive/YYYY-MM.md` 是月度归档；重要根因分析、调试洞察、关键决策和踩坑按月追加，写入前先读当月文件，不存在先创建。
- `memory/archive` 追加格式：`## [日期 | 标题]` + `- **Events**：` / `- **Changes**：` / `- **Insights**：`。
- 协作文件不是产品真值，不得覆盖正式设计和当前代码。

## 5. 验证、入口与关键路径
- 标准验证命令见「0. 实施基线」。
- 关键文档入口：`docs/windows-architecture.md`（总纲）、`docs/windows-migration-plan.md`、`docs/windows-ui-design.md`、`docs/ai-personas.md`、`docs/feature-research.md`、`docs/feature-migration.md`。
- Windows 定向验收脚本：`windows-native/scripts/phase6-*.ps1`（smoke/e2e/ui 验收）；性能基线 `perf-bench.ps1`。
- 配置与数据：`data/`（petdex 清单 JSON）；`Localizations/`（en/zh-Hans/zh-Hant/vi）。

## 6. 项目特定规则
- 分层边界：`DesktopPetCore`（macOS）与 `DesktopPet.Core`（Windows）均为纯逻辑层，零 UI 零 IO 依赖，核心逻辑必须可单测；领域层定义接口，基础设施实现（依赖倒置）。
- Windows 渲染约定：宠物窗口/弹幕层走自绘渲染器（16ms 帧预算，刻意豁免 MVVM）；设置/对话走 MVVM。新增高频渲染路径不得引入绑定/模板开销。
- Provider 可插拔：模型 / TTS / 生图走 OpenAI 兼容协议统一实现（`IModelProvider` / `ITtsProvider` / `IImageProvider`）；API Key 存 Windows Credential Manager，JSON 只存引用 ID，不落明文。
- 双进程：PetApp 与 PetAgent 分离，AI 推理/网络不得阻塞 UI 线程；Agent 崩溃由 PetApp 看门狗自动重启。
- AI 总开关：关闭后全部 AI 功能失效（无截屏/网络/后台进程），保持纯桌宠模式。
- 明确不做（本期）：角色社区/UGC、抽卡内购、多宠物群聊、重写回复、触觉反馈、移动端。
- 密钥类文件（如 `windows-native/测试.txt`）已被 .gitignore 忽略，不得提交或读取。

## 7. 维护原则
- 本文件保持短、小、硬。
- 近期状态放 `memory.md`，稳定协作知识放 `tools.md`，长期历史放 `memory/archive/`。
- 项目特定解释性长文回到 `docs/` 或正式文档，不堆进本文件。
