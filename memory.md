# DesktopPet 项目状态

> 本文件是当前状态快照 + 最近活跃窗口，允许覆盖更新。
> 完整历史归档见 `memory/archive/`，稳定规律见 `tools.md`。

## 当前基线
- macOS 主版（SwiftPM）：`swift build` / `swift test` 通过；打包 `./scripts/build-app.sh release`。
- Windows 版（.NET 8 + WPF）：Core 374 / Infra 96 / Agent 33 / App 38 顺序测试通过（541 total）；`dotnet build windows-native/DesktopPet.sln --no-restore -p:Platform=x64` 为 0 warning / 0 error。
- Windows 上 Swift core 验证：`./scripts/verify-core-windows.sh`（54 个 core 测试）。
- 最后更新：2026-08-09

## 版本锁定（详见 tools.md Environment）
- macOS：Swift 6.3 / macOS 13+ / Sparkle 2.9.4（锁）。
- Windows：.NET SDK 10.0.302 编译 net8.0 TFM；WindowsAppSDK 1.6.240923002 / Win2D 1.3.0 / ImageSharp 3.1.11 / H.NotifyIcon.WPF 2.1.4 / System.Drawing.Common 8.0.10 / System.Speech 8.0.0（锁）。
- 注意：API 用法以锁定版本为准，不同框架版本同名 API 签名/行为不同；升级依赖需全量回归。

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
- TTS 三级 Provider 栈已实施（`docs/windows-tts-design.md`）：SAPI 兜底 + OneCore 系统自然语音（App 层 WinRT）+ OpenAI 兼容端点（Infra，providers.json `tts` 段，Key 存 Credential Manager）；`ITtsProvider` 契约下沉 Core（TtsContracts.cs），设置页引擎单选/音色下拉/试听/语速（50-200%）；Edge TTS 直连不做（TLS 指纹 + 地域风控实证，EdgeTts.cs 保留标记不可用）。
- 坑点：SAPI `synth.Rate` 合法范围 -10..+10，语速映射必须 clamp（200% 不 clamp 会抛 ArgumentOutOfRangeException）；`OpenAiCompatibleTtsProvider.ListVoicesAsync` 的 401 必须显式抛 auth（吞掉会导致设置页假成功）；OneCore SSML `xml:lang` 必须跟随选中语音语言；设置页异步音色加载需 generation token 防竞态。
- 领域层（Core）零 UI 零 IO、可单测；宠物窗口自绘渲染器豁免 MVVM，设置/对话走 MVVM。
- AI 总开关：关闭即纯桌宠模式，无截屏/网络/后台进程。
- Windows 本地化以 Core embedded JSON 为唯一词典；语言变更必须先保存 settings，再刷新已跟踪静态槽位；用户/模型/日记/自定义人格内容显式排除。
- App/Agent 日志统一 `AppDataPaths.Logs`，写盘与导出均脱敏；恢复出厂先停 Agent/请求和关闭句柄，再暂存数据目录、删除 `DesktopPet/*` 凭据并通过父进程握手重启。
- 全屏只在输出交付边界抑制主动聊天/气泡/弹幕，不停止截图与分析。

## 仍需注意的坑点
- Windows 上 `swift test` 崩溃（SwiftPM llbuild bug #6605），core 测试必须走 `verify-core-windows.sh`。
- `windows-native/测试.txt` 含密钥，勿读取/提交。
- 模型请求限流：固定 worker 池 + P0/P1/P2 优先级、分场景 deadline；Provider transport 只分类错误，调度层拥有超时/重试。
- P/Invoke 结构体含 string 字段必须显式 `CharSet = CharSet.Unicode`：`CredNative.Credential` 曾缺省按 ANSI 编组，`CredWrite` 写出的凭据 target 名被系统按 UTF-16 解释成乱码（`敄歳潴偰瑥...`），`CredRead` 永远 NOT_FOUND → 迁移器每次启动误报 target-conflict 并累积垃圾凭据（2026-08-07 已修复 + round-trip 回归测试）。垃圾凭据特征：target 名的 UTF-16LE hex 含紧凑 ASCII 序列（如 `4465736B746F70...`），`CredEnumerate("DesktopPet/*")` 过滤不到，需全量枚举清理。
- 单元测试不能替代真实 GraphicsCapture/Win2D、Win32 热键/拖拽、Credential Manager、WPF/tray、多屏 mixed-DPI、CPU/内存和恢复出厂重启验收；这些证据必须在 child 5 明确记录。
- **GraphicsCapture 帧池死锁（已修复）**：FrameArrived 内节流拒绝时不得直接 return——任何帧滞留 FramePool（未 TryGetNextFrame）都会占满缓冲（bufferCount=1 时一帧即满），后续新帧被丢弃且 FrameArrived 永不再触发（静默失效，无异常无日志）。节流必须取出帧后丢弃（`using var dropped = sender.TryGetNextFrame()`）；bufferCount 用 2。
- **构建输出目录陷阱**：`dotnet build -p:Platform=x64` 输出 `bin/x64/`，而 `ResolveAgentHostPath` 回退探测 `bin/Debug/`（无 x64）——改 Agent 代码后必须不带 Platform 参数重新构建 AgentHost，否则 App 启动的仍是旧 DLL。

## 最近活跃窗口
- 2026-08-09（TTS 实施）：P0-P3 全部完成——契约下沉 Core（TtsContracts/Registry）、SAPI 适配新契约（ListVoices+语速）、OneCoreTtsProvider（App）、OpenAiCompatibleTtsProvider（/v1/audio/speech+voices）、AiSettings 加 TtsProviderId/TtsSpeedPercent、providers.json tts 段、设置页引擎/音色/试听/语速/连接编辑器；603 tests 全绿 + build 0 warn/error + 真机（SAPI/OneCore 全语速合成、App 冒烟）；独立 code review 发现 I-1~I-4（SAPI 200% 越界/ListVoices 吞 401/Speak 每次拉列表/SSML lang 硬编码）+ M-1~M-5 全部修复并补回归测试。
- 2026-08-09：设置审计修复——① Windows TTS 声音列表从硬编码 Edge 名改为 SAPI 动态枚举（`SapiTtsProvider.GetInstalledVoices`，旧 Edge 名回落"自动"）；`SapiTtsProvider` 语言回退加 `TryParseCulture` 兜底；② macOS 声音设置从"死设置"接入真实事件（Event 重构为 click/breakReminder，默认 Pop/Purr；`PetWindowModel` 点击、`BreakReminderController` 提醒改走 `SoundSettings.play`；SetupView 文案与 4 语言 strings 同步）。验证：Windows 557 tests 全过 + build 0 error + System.Speech 8.0 真机全路径（枚举/精确选中/语言回退/合成 WAV）+ PetApp F5 冒烟零错误日志；macOS 端未编译验证（用户暂缓）。
- 2026-08-07（晚）：修复截屏分析静默失效——根因 GraphicsCapture FrameArrived 节流拒绝不取帧→帧滞留占满 FramePool→FrameArrived 永不再触发（永久死锁，无异常无日志）；VS MCP 附加调试 + 独立探测程序 + 自包含对照实验三重实证；修复 = 节流拒绝时取帧即弃 + bufferCount 1→2；端到端恢复（push event kind=Coding 连续产出，弹幕路由正常）；Agent.Tests 33 / 全量 550 全过。另发现并移除 PetWindow.cs:406 残留调试断点（VS F5 启动必停导致 App 卡在启动流程、Agent 不启动）。
- 2026-08-07：修复 Windows 版气泡压头/悬空——根因：气泡按帧矩形顶定位（Headroom 固定偏移），不同宠物帧内透明边不同；改为底部锚定 + 按实际可见头顶（ContentTopInset）向上平移；新增 SpriteSheet/PetRenderer 测试（Core 374）。
- 2026-08-07：定位并修复启动必现的"模型连接的目标凭据已存在且内容不同"误报——根因是 `CredNative.Credential` 缺 `CharSet.Unicode` 致 CredWrite target 名编组损坏（见坑点）；已清理 2 条垃圾凭据、迁移 myovo 连接凭据到新引用（providers.json 已更新）、新增 round-trip 回归测试（Infra.Tests 104 全过）。
- 2026-08-06：C1-C6 与 Important child 1-4 完成；最新顺序验证 537 tests + clean x64 build。
- 2026-08-06：完成 I1 四语言实时本地化，修复 catalog canonical 盲区、ComboBox 字符串、可见窗口刷新和动态内容 key 碰撞。
- 2026-08-06：完成 I5 诊断/恢复出厂/全屏；审查后补齐日志硬上限与轮转恢复、整字段 Authorization 脱敏、父进程退出/PID 复用重启、迁移损坏可见性。
- 2026-08-06：I15 production caller 审计闭环（sprite import/delete、ball position、diary metadata commit ordering）；真实机器 smoke 留 child 5。
