# DesktopPet 项目状态

> 本文件是当前状态快照 + 最近活跃窗口，允许覆盖更新。
> 完整历史归档见 `memory/archive/`，稳定规律见 `tools.md`。

## 当前基线
- macOS 主版（SwiftPM）：`swift build` / `swift test` 通过；打包 `./scripts/build-app.sh release`。
- Windows 版（.NET 8 + WPF）：Core 370 / Infra 96 / Agent 33 / App 38 顺序测试通过（537 total）；`dotnet build windows-native/DesktopPet.sln --no-restore -p:Platform=x64` 为 0 warning / 0 error。
- Windows 上 Swift core 验证：`./scripts/verify-core-windows.sh`（54 个 core 测试）。
- 最后更新：2026-08-07

## 已完成能力
- macOS 桌宠完整功能：养成（XP/五阶段/成就）、精灵导入切片、气泡、休息提醒、4 语言本地化、菜单栏应用 + Sparkle 更新。
- Windows .NET 8 + WPF 迁移全部完工，Tauri 旧版（`windows/`）已删除。
- Windows 版：宠物/漫游、气泡、引导、分层会话记忆、可插拔模型/TTS/生图、三优先级（P0/P1/P2）AI 运行时、捕获/渲染/拖拽/弹幕资源生命周期、可配置事务型全局快捷键与连接级凭据迁移。
- Windows 重要项 I1-I17 的实现子任务 1-4 已完成：726-key 英/简中/繁中/越南语同集 catalog 与持久化后发布的实时刷新；`%APPDATA%/DesktopPet/logs` 滚动脱敏/ZIP 导出；CPU/WorkingSet 诊断；Credential Manager 前缀恢复出厂；多屏/DPI 全屏主动输出抑制；I15 原子文件/调用方补偿闭环。

## 进行中 / 未完成
- `.tasks/windows-review-fix`（2026-08-07 闭环）：审查落盘 + 官方文档查证 + 四批次修复全部完成并提交（4 commits：App 行为风险 / Infra-Agent / Core 结构 / 文档对齐）；544 tests 全过，x64 build 0 warn/error。UI 线程类修复需真实机器 smoke（并入 windows-important-hardening child 5 矩阵）。
- `.tasks/windows-important-hardening` child 5 待执行：全量独立 review、正式报告更新、真实 Windows GPU/Win32/WPF/Credential Manager/日志导出/恢复出厂重启/多屏 mixed-DPI 验收。
- Roadmap 后续项：v0.2 桌面感知深化；Provider 默认范围、自动更新方案等产品决策见 `docs/windows-architecture.md` §10。

## 关键决策（仍有效）
- Tauri → .NET 8 + WPF 整体迁移（透明窗口软渲染是卡顿根因，原生渲染是唯一合理选择）。
- 双进程架构：PetApp.exe + PetAgent.exe（截屏/分析/总结），Agent 崩溃看门狗自动重启。
- 模型/TTS/生图统一 OpenAI 兼容协议，Provider 可插拔；API Key 存 Windows Credential Manager，不落明文。
- 领域层（Core）零 UI 零 IO、可单测；宠物窗口自绘渲染器豁免 MVVM，设置/对话走 MVVM。
- AI 总开关：关闭即纯桌宠模式，无截屏/网络/后台进程。
- Windows 本地化以 Core embedded JSON 为唯一词典；语言变更必须先保存 settings，再刷新已跟踪静态槽位；用户/模型/日记/自定义人格内容显式排除。
- App/Agent 日志统一 `AppDataPaths.Logs`，写盘与导出均脱敏；恢复出厂先停 Agent/请求和关闭句柄，再暂存数据目录、删除 `DesktopPet/*` 凭据并通过父进程握手重启。
- 全屏只在输出交付边界抑制主动聊天/气泡/弹幕，不停止截图与分析。

## 仍需注意的坑点
- Windows 上 `swift test` 崩溃（SwiftPM llbuild bug #6605），core 测试必须走 `verify-core-windows.sh`。
- `windows-native/测试.txt` 含密钥，勿读取/提交。
- 模型请求限流：固定 worker 池 + P0/P1/P2 优先级、分场景 deadline；Provider transport 只分类错误，调度层拥有超时/重试。
- 单元测试不能替代真实 GraphicsCapture/Win2D、Win32 热键/拖拽、Credential Manager、WPF/tray、多屏 mixed-DPI、CPU/内存和恢复出厂重启验收；这些证据必须在 child 5 明确记录。

## 最近活跃窗口
- 2026-08-06：C1-C6 与 Important child 1-4 完成；最新顺序验证 537 tests + clean x64 build。
- 2026-08-06：完成 I1 四语言实时本地化，修复 catalog canonical 盲区、ComboBox 字符串、可见窗口刷新和动态内容 key 碰撞。
- 2026-08-06：完成 I5 诊断/恢复出厂/全屏；审查后补齐日志硬上限与轮转恢复、整字段 Authorization 脱敏、父进程退出/PID 复用重启、迁移损坏可见性。
- 2026-08-06：I15 production caller 审计闭环（sprite import/delete、ball position、diary metadata commit ordering）；真实机器 smoke 留 child 5。
