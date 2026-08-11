# DesktopPet 项目状态

> 本文件是当前状态快照 + 最近活跃窗口，允许覆盖更新。
> 完整历史归档见 `memory/archive/`，稳定规律见 `tools.md`。

## 当前基线
- macOS 主版（SwiftPM）：`swift build` / `swift test` 通过；打包 `./scripts/build-app.sh release`。
- Windows 版（.NET 8 + WPF）：Core 480 / Infra 161 / Agent 35 / App 53 顺序测试通过（729 total）；`dotnet build windows-native/DesktopPet.sln --no-restore -p:Platform=x64` 编译 0 CS error（桌宠运行中 App 复制阶段报 MSB3021 锁，验证以 dotnet test 为准）。
- Windows 上 Swift core 验证：`./scripts/verify-core-windows.sh`（54 个 core 测试）。
- 最后更新：2026-08-11

## 版本锁定（详见 tools.md Environment）
- macOS：Swift 6.3 / macOS 13+ / Sparkle 2.9.4（锁）。
- Windows：.NET SDK 10.0.302 编译 net8.0 TFM；WindowsAppSDK 1.6.240923002 / Win2D 1.3.0 / ImageSharp 3.1.11 / H.NotifyIcon.WPF 2.1.4 / System.Drawing.Common 8.0.10 / System.Speech 8.0.0（锁）。
- 注意：API 用法以锁定版本为准，不同框架版本同名 API 签名/行为不同；升级依赖需全量回归。

## 已完成能力
- macOS 桌宠完整功能：养成（XP/五阶段/成就）、精灵导入切片、气泡、休息提醒、4 语言本地化、菜单栏应用 + Sparkle 更新。
- Windows .NET 8 + WPF 迁移全部完工，Tauri 旧版（`windows/`）已删除。
- Windows 版：宠物/漫游、气泡、引导、分层会话记忆、可插拔模型/TTS/生图、三优先级（P0/P1/P2）AI 运行时、捕获/渲染/拖拽/弹幕资源生命周期、可配置事务型全局快捷键与连接级凭据迁移。
- Windows 重要项 I1-I17 实现子任务 1-4 完成：726-key 四语言同集 catalog 实时刷新；日志滚动脱敏/ZIP 导出；CPU/WorkingSet 诊断；Credential Manager 前缀恢复出厂；多屏/DPI 全屏抑制；I15 原子文件/调用方补偿闭环。
- **生图模块全部阶段完工（docs/windows-imagegen-design.md）**：阶段 1-4b（契约/目录 13 模型/透明管线/双协议族适配器/门面/连接列表迁移/总结图改道）→ 阶段 4c（设置页多连接列表编辑器 + 总结图模型下拉 + AiSettings.SummaryImageModelRef 持久化与 runtime 签名修复）→ 阶段 5（生图页：连接×模型/提示词/按能力参数面板/生成/取消/错误分类 + 历史画廊落盘 `%APPDATA%/DesktopPet/gallery/`：PNG+index.json 原子写、删除、200 上限修剪、损坏容错）。

## 进行中 / 未完成
- **已知 flaky**：SchedulerTests 两个并发时序测试全量并行时偶发失败，单跑恒过（既有问题，非生图模块引入，待排查）。
- `.tasks/windows-important-hardening` child 5 待执行：真实 Windows 验收（托盘可见性/GraphicsCapture 分辨率变化/弹幕防追尾视觉/GPU/Win32/Credential Manager/日志导出/恢复出厂重启/多屏 mixed-DPI）+ **生图模块 UI 冒烟**（设置页连接编辑器多连接增删改、总结图模型下拉、生图页生成/取消/画廊删除，需真实端点或 mock）。
- Roadmap 后续项：v0.2 桌面感知深化；Provider 默认范围、自动更新方案等产品决策见 `docs/windows-architecture.md` §10。

## 关键决策（仍有效）
- Tauri → .NET 8 + WPF 整体迁移（透明窗口软渲染是卡顿根因，原生渲染是唯一合理选择）。
- 双进程架构：PetApp.exe + PetAgent.exe（截屏/分析/总结），Agent 崩溃看门狗自动重启。
- 模型/TTS/生图统一 OpenAI 兼容协议，Provider 可插拔；API Key 存 Windows Credential Manager，不落明文。
- 生图模块：目录驱动（模型是数据）+ 两协议族适配器（模板方法基类）+ 透明=后处理策略（原生直传/绿幕 HSV 键控）+ 门面统一入口 + 多模型容错（auth/rate-limit 不换模型）。
- TTS 三级 Provider 栈（`docs/windows-tts-design.md`）：SAPI 兜底 + OneCore + OpenAI 兼容；`ITtsProvider` 契约下沉 Core；Edge TTS 直连不做。
- 领域层（Core）零 UI 零 IO、可单测；宠物窗口自绘渲染器豁免 MVVM，设置/对话走 MVVM。
- AI 总开关：关闭即纯桌宠模式，无截屏/网络/后台进程。
- Windows 本地化以 Core embedded JSON 为唯一词典；语言变更必须先保存 settings，再刷新已跟踪静态槽位；用户/模型/日记/自定义人格内容显式排除。
- App/Agent 日志统一 `AppDataPaths.Logs`，写盘与导出均脱敏；恢复出厂先停 Agent/请求和关闭句柄，再暂存数据目录、删除 `DesktopPet/*` 凭据并通过父进程握手重启。
- 全屏只在输出交付边界抑制主动聊天/气泡/弹幕，不停止截图与分析。

## 仍需注意的坑点
- Windows 上 `swift test` 崩溃（SwiftPM llbuild bug #6605），core 测试必须走 `verify-core-windows.sh`。
- `windows-native/测试.txt` 含密钥，勿读取/提交。
- 模型请求限流：固定 worker 池 + P0/P1/P2 优先级、分场景 deadline；Provider transport 只分类错误，调度层拥有超时/重试。
- **AiSettings 是位置参数 record + 手写 converter：追加字段必须 Normalize/converter Read/Write 三处同步 + round-trip 测试覆盖新字段**（4b 只同步 Write，Read/Normalize 双遗漏导致总结图模型选择保存后丢失，阶段 4c 才修复）。
- **设置生效链四环**：持久化（converter）→ 运行时消费（Signature/runtime 构建）→ UI 显示，每环都需测试或显式核对（SummaryImageModelRef 曾不在 SignatureOf → 改设置不重建 runtime）。
- P/Invoke 结构体含 string 字段必须显式 `CharSet = CharSet.Unicode`（曾致 CredWrite target 名乱码、迁移器每次启动误报 target-conflict 并累积垃圾凭据；垃圾凭据特征：target 名 UTF-16LE hex 含紧凑 ASCII 序列，需全量枚举清理）。
- 单元测试不能替代真实 GraphicsCapture/Win2D、Win32 热键/拖拽、Credential Manager、WPF/tray、多屏 mixed-DPI、CPU/内存和恢复出厂重启验收；生图页/连接编辑器 UI 冒烟同样留 child 5。
- **GraphicsCapture 帧池死锁（已修复）**：FrameArrived 内节流拒绝时不得直接 return——任何帧滞留 FramePool 都会占满缓冲，后续新帧被丢且 FrameArrived 永不再触发（静默失效）。节流必须取出帧后丢弃（`using var dropped = sender.TryGetNextFrame()`）；bufferCount 用 2。
- **构建输出目录陷阱**：`dotnet build -p:Platform=x64` 输出 `bin/x64/`，而 `ResolveAgentHostPath` 回退探测 `bin/Debug/`（无 x64）——改 Agent 代码后必须不带 Platform 参数重新构建 AgentHost，否则 App 启动的仍是旧 DLL。

## 最近活跃窗口
- 2026-08-11（生图模块阶段 4c/5，未提交）：多连接列表编辑器（列表/新建/编辑/删除/凭据回滚与清理）+ AI 页总结图模型下拉（AiSettings.SummaryImageModelRef）；修复该字段持久化缺口（converter Read + Normalize 双遗漏）与 runtime 签名未含该字段（改设置不重建）；阶段 5 生图页（连接×模型带价格/提示词计数/按能力参数面板：宽高比·档位·质量仅 openai·张数·seed·透明+绿幕提示/生成循环/取消/错误分类）+ 历史画廊落盘（GalleryIndex 契约 Core 7 测试、GalleryStore Infra 原子写/删除/修剪/损坏容错 9 测试）；入口=设置页 AI 页「打开生图页」。729 测试全绿。坑：GalleryStore 放 Infra 而非 App（纯 IO 分层，App.Tests 直接引用可测）；缩略图 BitmapImage 必须 OnLoad+Freeze；真机 UI 冒烟未跑（桌宠锁 bin，MSB3021 但编译成功）。
- 2026-08-11（生图模块阶段 1-4b，提交 57cb0cc/922f555）：设计定稿 + Core 契约/目录（13 模型 embedded JSON）/透明管线（绿幕 prompt+HSV 键控）+ OpenAI 兼容/Gemini 双协议族适配器 + ImageGenService 门面 + providers.json image 段连接列表迁移 + 总结图改道 + 旧 IImageProvider 退役。711 测试全绿。坑点：适配器按连接缓存但模型构造时固定 → fallback 换模型拿到旧适配器（已修为按(连接,模型)缓存）；`JsonIgnore(WhenWritingDefault)` 对反序列化同样生效（改自定义 converter）；AiSettings 位置参数追加字段三处同步（本次验证）；运行中 PetApp 锁 bin 报 MSB3021 但编译成功。
- 2026-08-11（屏幕事件 journal + 行为会话化）：diary/screen-YYYY-MM-DD.jsonl 一行一事件 + ActivitySessionBuilder 归并 + ActivitySummaryFormatter 注入（40 段预算）；启动清理 30 天前 journal；ScreenEventKind 新增 Music；文案中性化（4 语言同集 749 keys）；生图超时 120s→300s + SummaryImageRetryPolicy。626+ 测试全绿。坑：sln x64 平台映射 AnyCPU，验证运行版本认 `bin/Debug/.../win-x64/` 时间戳，`bin/x64/` 是旧残留。
- 2026-08-09（TTS 实施）：P0-P3 全部完成——契约下沉 Core、SAPI/OneCore/OpenAI 兼容三级栈、设置页引擎/音色/试听/语速/连接编辑器；603 tests 全绿 + build 0 warn/error + 真机验证；独立 code review 发现 I-1~I-4/M-1~M-5 全部修复并补回归测试。
- 2026-08-09：设置审计修复（macOS 声音死设置接入真实事件；Windows TTS 声音列表 SAPI 动态枚举 + 语言回退 TryParseCulture）。
- 2026-08-07（晚）：修复截屏分析静默失效（FrameArrived 节流死锁三重实证）；移除 PetWindow.cs:406 残留调试断点。
- 2026-08-07：修复气泡压头/悬空（底部锚定 + ContentTopInset）；启动必现凭据误报根因（CharSet.Unicode）+ 垃圾凭据清理。
- 2026-08-06：C1-C6 与 Important child 1-4 完成；I1 四语言实时本地化；I5 诊断/恢复出厂/全屏；I15 生产调用方审计闭环。
