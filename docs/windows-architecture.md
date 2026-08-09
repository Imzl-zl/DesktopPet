# Windows 版架构设计（.NET 8 + WPF）

> 总纲文档：衔接 `windows-migration-plan.md`（迁移路线）、`windows-ui-design.md`（Lumen 视觉）、`ai-personas.md`（人格）、`feature-research.md`（功能采纳）。
> 本文件回答：**分层与模块、设计模式、可插拔 Provider、对话管道、性能方法、功能边界、系统级 UX、错误处理**。

---

## 1. 架构总览（分层）

```
┌─────────────────────────────────────────────────────────────┐
│ 表现层 DesktopPet.App（WPF）                                  │
│  PetWindow ×N / FloatingBallWindow / ChatWindow /            │
│  DanmakuWindow / SettingsWindow / TrayMenu                   │
│  · 宠物窗口 = 自绘渲染器（不走 MVVM，性能敏感）                    │
│  · 设置/对话 = MVVM（View + ViewModel + Service）              │
├─────────────────────────────────────────────────────────────┤
│ 应用层/协调（DesktopPet.App，无 UI 依赖的用例编排）               │
│  AiCoordinator（AI 运行时/Agent 生命周期/主动互动/每日总结）       │
│  ModeService（输出模式 danmaku/chat/bubble/silent）            │
│  PetWindowManager（多实例窗口/广播/设置下发）                   │
│  HotkeySettingsCoordinator / LanguageCoordinator             │
├─────────────────────────────────────────────────────────────┤
│ 领域层 DesktopPet.Core（纯逻辑，可单测，不依赖 WPF/IO）           │
│  SpriteSlicer / RoamEngine+RoamPhysics / CareEngine /         │
│  PersonaEngine / ConversationMemory+MemoryProfileExtractor /  │
│  IntimacyEngine / InteractionEngine / DanmakuEngine /         │
│  ModelRequestScheduler（P0/P1/P2 并发调度）                   │
├─────────────────────────────────────────────────────────────┤
│ 基础设施层 DesktopPet.Infra / DesktopPet.Agent                │
│  FileJsonStore（仓储，Infra.Storage）· PipeRpc（IPC）           │
│  ModelProvider 抽象（Core 定义）+ OpenAI 兼容实现（Infra）       │
│  TtsProvider 抽象（Core 定义）+ 三级实现（SAPI/OneCore/端点）    │
│  HotkeyManager（RegisterHotKey）· RollingFileLogger           │
│  Agent：GraphicsCaptureSource / AnalysisEngine                │
└─────────────────────────────────────────────────────────────┘
```

**依赖方向**：表现层 → 应用层/协调 → 领域层 ← 基础设施层（依赖倒置：领域层定义接口，基础设施实现）。`DesktopPet.Core` 零 UI 零 IO 依赖，全部核心逻辑可单测。

**进程**：`PetApp.exe`（上面全部）+ `PetAgent.exe`（截屏/分析/总结，Phase 5 拆，见迁移计划 §4.1）。

---

## 2. 设计模式选型（按场景选，不堆砌）

| 模式 | 用在哪 | 为什么 |
|---|---|---|
| **MVVM** | 设置页、对话窗口、托盘菜单 | WPF 标准；数据绑定 + `INotifyPropertyChanged`；View 无逻辑 |
| **Renderer 自绘**（非 MVVM） | 宠物窗口、弹幕层 | 16ms 帧预算内直写像素，绑定/模板开销不可接受；刻意豁免 |
| **直接扇出 + C# event** | 浮球广播（`PetWindowManager.BroadcastQuickBubble` foreach 窗口）、设置变更 `ApplySettings` 循环下发 | 窗口数少（≤5），无需 Mediator 框架；C# `event` 用于跨层通知（如 `AnalysisEngine.EventRaised`） |
| **状态机** | 宠物行为（idle/wander/sleep/drag/roam 模式）；对话会话（idle/thinking/replying/error） | 行为转换有明确边界；迁移 TS 版 roam 引擎的 mode 系统 |
| **策略分派** | 漫游模式 stay/wander/cursor/climb（`RoamModes` 单类 switch 分派）；输出模式弹幕/对话/气泡/静默 | 行为可插拔；新增模式不改核心循环 |
| **Provider 抽象** | 模型（OpenAI 兼容/本地 Ollama）、TTS（SAPI 默认/OneCore 自然语音/OpenAI 兼容端点，见 `windows-tts-design.md`）、图像生成 | 用户要求可自定义连接；见 §3 |
| **仓储（Repository）** | PetStore / CareState / MemoryStore / Personas / 设置，统一 `IJsonStore` | 持久化细节隔离；测试用内存实现 |
| **请求管道（Pipeline）** | 对话请求：校验→人格拼接→记忆注入→亲密度修饰→模型调用→token 记账→XP/亲密度结算 | 横切关注点（记账/注入/日志）不污染会话逻辑；可单测每步 |
| **工厂** | `PetWindowManager`（多实例窗口创建/销毁） | 多宠物实例生命周期统一管理 |
| **手工构造器注入** | App.xaml.cs 组合根：手工 `new` + 构造器注入 + `Func` 工厂（无 DI 容器） | 单进程窗口应用，显式依赖链清晰；不引入容器依赖 |
| **观察者** | 设置变更 → 各窗口刷新（`PetWindowManager.ApplySettings` 直接下发） | 窗口数少，直接循环即可 |
| **适配器** | Tauri 版 localStorage 数据 → 新版 JSON 迁移 | 一次性迁移工具，隔离旧格式 |

**明确不用的**：领域事件（过度设计）、CQRS/EventSourcing（数据量小，JSON 仓储足够）、事件总线框架、DI 容器（手工组合根 + 构造器注入已覆盖，见上）。

---

## 3. 可插拔 Provider 设计（模型 / TTS / 图像生成）

### 3.1 统一连接配置（用户可自定义）

```json
// %APPDATA%/DesktopPet/providers.json
{
  "models": [
    {
      "id": "openai-default",
      "name": "OpenAI GPT-4o",
      "baseUrl": "https://api.openai.com/v1",
      "apiKey": "sk-...",            // 存 Windows Credential Manager，不落明文 JSON
      "modelName": "gpt-4o",
      "capabilities": ["chat", "vision"],   // 能力标记，UI 按能力显示
      "isDefault": true
    },
    {
      "id": "ollama-local",
      "name": "本地 Ollama",
      "baseUrl": "http://localhost:11434/v1",   // Ollama 的 OpenAI 兼容端点
      "apiKey": "",
      "modelName": "qwen2.5-vl:7b",
      "capabilities": ["chat", "vision"],
      "isDefault": false
    }
  ],
  "image": {   // 生图连接（可选，总结图用）
    "baseUrl": "...",
    "apiKeyRef": "...",
    "modelName": "...",
    "size": "1024x1024"
  }
}
// TTS 在线端点配置见 `windows-tts-design.md` §5.2：providers.json 新增 tts 段
// （baseUrl/apiKeyRef/modelName/voice，对齐 image 段）；离线引擎（SAPI/OneCore）零配置。
```

- **协议**：全部走 OpenAI 兼容 REST（`/chat/completions`，图片用 content 数组）——云端（OpenAI/DeepSeek/Qwen/GLM…）和本地（Ollama/vLLM/LM Studio）天然统一，一个 `HttpClient` 实现通吃
- **能力发现**：设置页「测试连接」按钮 → 调 `/models` 列出可用模型；失败给出明确错误（超时/401/URL 错误分类提示）
- **API Key 安全**：`Windows Credential Manager` 存储，JSON 只存引用 ID，不落盘明文
- **多模型分工**：未实现（可选保留项）。变化检测用本地灰度哈希（FrameHasher），事件分类是模型输出后的关键词粗分类（AnalysisEngine.Classify），无独立本地小模型链路
- 界面：设置页 AI 助手 → 「模型连接」卡片：连接列表 + 测试按钮 + 能力徽章 + 默认标记

### 3.2 接口定义

```csharp
public interface IModelProvider {
    string Id { get; }
    ModelCapabilities Capabilities { get; }        // Chat | Vision
    Task<ChatResult> CompleteAsync(ChatRequest req, CancellationToken ct);
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct); // 测试连接
}
// 实现：OpenAiCompatibleModelProvider（Core/Scheduling/ModelContracts.cs 定义接口）

// TTS 契约（Core/Tts/TtsContracts.cs）：ListVoicesAsync + SynthesizeAsync(TtsSynthesisRequest)
// 实现：SapiTtsProvider（默认，离线）/ OneCoreTtsProvider（App，系统自然语音）/
//      OpenAiCompatibleTtsProvider（在线端点，用户自配）
// 完整设计见 windows-tts-design.md（三级 Provider 栈 / 配置 / UX / 验收）。
```

### 3.3 模型请求调度器（ModelRequestScheduler）

多宠物各自表达、主动互动、每日总结都可能同时要调用模型——**并发调用 + 并发控制**，不是串行排队，也不是无脑并发：

```
┌─ 请求队列（优先级）─────────────────────────┐
│ P0 用户对话（交互，不能等）                    │
│ P1 主动互动（多宠物并行，事件驱动）             │
│ P2 每日总结/画像更新（后台，可让路）            │
└──────────────┬──────────────────────────────┘
               ▼
      SemaphoreSlim 并发闸（默认 3，按 provider 可配）
               ▼
      ModelProvider.CompleteAsync（各请求独立）
```

**规则**：
- **并行**：全员回应时 N 只宠物同时发请求（`Task.WhenAll`），等待一次延迟而非 N 倍
- **并发闸**：`SemaphoreSlim(3)` 限制同时飞着的请求数——防云端限流（RPM/TPM）和本地 Ollama 排队雪崩；每 provider 独立计数
- **优先级**：队列按 P0>P1>P2 插队；用户对话永远不被主动互动阻塞（对话发出即占并发闸）
- **超时**：互动 8s / 对话 30s；互动超时的宠物跳过本轮（下轮轮换补偿），对话超时走重试（指数退避 2 次）
- **互斥**：主动互动进行中用户发起对话 → 互动请求继续但输出排到对话之后；避免对话时宠物插话
- **合并策略**：不做多角色合并请求（一个请求只有一个 system prompt，人格会混淆）；宁可并行 N 个小请求

### 3.4 生图 Provider（每日总结图，可插拔）

```csharp
public interface IImageProvider {
    string Id { get; }
    Task<ImageResult> GenerateAsync(ImageGenRequest req, CancellationToken ct);
}
// 实现：OpenAiCompatibleImageProvider（/images/generations，通吃 DALL·E / GPT-Image /
//       Qwen-Image / FLUX 等 OpenAI 兼容端点；本地 ComfyUI 兼容端点亦可）
```

- 配置复用 `providers.json`（新增 `image` 段：baseUrl/apiKey/modelName/size/quality），设置页 AI 助手 → 「生图连接」卡片（测试连接复用 `/models` 或发一张小图验证）
- **总结图流程**：每日总结文本 → `ImagePromptBuilder`（总结摘要 + 宠物形象描述 + Lumen 画风约束，如"轻盈简约、柔和光感、像素宠物拟人化"）→ 生图 → 保存 `%APPDATA%/DesktopPet/diary/yyyy-MM-dd.png` → 日记展示
- 失败降级：生图失败不影响总结文本生成（文本照常入日记，图留空位可手动重试）
- 成本护栏：默认每日 1 张；开关可关；失败重试不超过 2 次

---

## 4. 对话请求管道（核心流程，单测重点）

```
用户输入
  → ① 校验（长度/频率限制）
  → ② 人格拼接：Base Prompt + 人格 Prompt（按亲密度档位选称呼/语气版本）
  → ③ 记忆注入：用户画像摘要 + 最近对话摘要（按 token 预算裁剪，超限轮次压缩进 ≤200 字滚动摘要）
  → ④ 屏幕上下文（对话模式：最近 N 条屏幕事件，可选携带当前截图）
  → ⑤ ModelProvider.CompleteAsync（temperature 0.7；max_tokens 120 为桌宠短句路径上限，对话路径按模型连接配置，每日总结 0.8/300）
  → ⑥ 输出 → 打字机气泡 / 弹幕（按当前输出模式）
  → ⑦ token 记账：→ CareEngine（XP）+ IntimacyEngine（亲密度）
  → ⑧ 异步：画像更新（新话题标签、作息推断）
```

管道每步独立可测；失败点（⑤）有超时/重试（指数退避，2 次）/降级（本地模型 → 明确错误提示）。

---

## 5. 性能设计（方法层）

| 方面 | 方法 | 依据/继承 |
|---|---|---|
| **精灵渲染** | 普通精灵帧使用冻结 `BitmapSource` LRU 缓存；alpha 掩码缓存（O(1) hitTest）；仅动态成长叠加使用 `WriteableBitmap` | 避免每帧 CPU 放大和 RGBA/BGRA 整表面复制，同时保留动态效果 |
| **透明 presenter** | WPF layered presenter 用于兼容；高频透明桌宠演进至 `WS_EX_NOREDIRECTIONBITMAP` + `Windows.UI.Composition.DesktopWindowTarget` | WDDM 下 WPF layered window 可硬件加速，但 WPF 与 Visual Layer 不能跨 airspace 共享内容 |
| **帧率语义** | 按动画状态机定义的 fps 播放；完全静止时停掉渲染循环；性能验收记录 p95 间隔和拖拽延迟 | 不降低既有动画或漫游频率换取 CPU 数字 |
| **弹幕层** | Win2D GPU 合成；文本对象池（滚动条目不反复创建）；DWrite 布局缓存（同文案不重排） | §6.5 |
| **内存** | 位图缓存 LRU 上限（如 32MB）；宠物实例卸载时释放帧缓存；设置页缩略图按需加载 | — |
| **截屏** | 缩略图 320×180 灰度哈希变化检测（采集 1fps 成本极低，与分析间隔独立）；云端调用限频（分析间隔 3-30s，默认 5s） | §6.4 |
| **拖拽** | 直接 `MoveWindow`，无 IPC 无补丁层；`GetMessageTime` 采样验证 <16ms | 迁移计划 §7 |
| **启动** | 宠物窗口先行（<2s 可见），设置/目录/弹幕按需懒加载；`app.manifest` PerMonitorV2 | 对标 bongo-cat-next <2s |
| **网络** | `HttpClient` 单例 + 连接复用；超时 30s；指数退避重试 | — |
| **双进程** | AI 推理/网络永不阻塞 UI 线程；Agent 崩溃自动重启（PetApp 监控） | §4.1 |
| **空闲基线** | 无动画无 AI 时 CPU <1%、内存 <120MB | 验收标准 §7 |

---

## 6. 功能边界（做到什么样）

### M1-M3（核心，先交付）
宠物展示/多实例/显隐、自定义精灵切片、拖拽/物理/漫游、气泡（互动+快速气泡）、养成（XP/阶段/成就）、设置页全量、4 语言、托盘/浮球。**无 AI**。

### M4（AI 基础）
截屏分析（分析开关）、7 人格 + 自定义、弹幕/对话/静默三模式（静默 = 不分析不输出）、聊天增强（重开、示例对话）。**AI 总开关**：关 = 全部 AI 功能失效（无截屏/网络/后台进程），纯桌宠模式。

### M5（陪伴增强）
记忆（记忆开关）、主动互动（开关 + 频率档）、亲密度（开关）、全局快捷键、每日总结（开关）+ 总结图（开关，默认关）、语音输出（开关，默认关）。**开关层级**：AI 总开关 → 功能独立开关，全部集中在设置页 AI 助手页。

### 明确不做（本期）
角色社区/UGC、抽卡内购、多宠物群聊（仅多宠物轮流评论同一事件的简单版）、重写回复、触觉反馈、移动端。

---

## 7. 系统级 UX 设计（跨界面）

| 场景 | 设计 |
|---|---|
| **首次启动** | 引导三步：① 选/导入宠物 ② 人格初选（默认暖男）③ AI 连接（可跳过，稍后设置）；跳过也有完整桌宠体验 |
| **AI 状态可见** | 思考中：对话气泡顶部状态点呼吸（sky）；弹幕模式：无视觉（避免刷屏感）；分析关闭时宠物头顶不显示任何指示 |
| **模型断连/Key 无效** | 对话区内联错误条（danger 12% 底）+ 一键跳转设置页连接卡片；不弹系统框 |
| **限频被触发** | 静默降级：宠物气泡显示"让我歇口气~"（人格化文案），不显示技术错误 |
| **崩溃恢复** | 位置/状态每次变更即持久化（防丢失）；宠物窗口异常自动重建（工厂 + 看门狗） |
| **所有操作可逆** | 删除宠物 → 内联确认；清空对话 → 确认；恢复出厂设置（设置页底部） |
| **键盘可达** | 设置/对话窗口全键盘可操作；全局快捷键常显在设置页（可自定义） |
| **忙碌不打扰** | 弹幕/主动互动在用户全屏（游戏/放映）时暂停（Win32 前台窗口几何检测 + 输出交付边界抑制；截屏与分析不停止） |
| **性能可见** | 设置页「关于」显示当前 CPU/内存占用（自采样），用户可自查"卡不卡" |

---

## 8. 错误处理与可观测性

- **错误分级**：可恢复（网络抖动 → 重试）、可提示（Key 无效 → 内联错误条）、静默（限频 → 人格化文案）
- **日志**：`Logger`（文件滚动，`%APPDATA%/DesktopPet/logs/`，debug 级开关）；关键事件（崩溃、模型失败、升级）写结构化日志
- **诊断页**：设置页「关于」→ 日志导出按钮（一键打包 zip 供反馈）
- **崩溃兜底**：`AppDomain.UnhandledException` + 看门狗重启 Agent；宠物窗口异常独立捕获，不拖垮主进程

---

## 9. 代码组织（命名空间，以实际目录为准）

```
DesktopPet.App/        Windows, Settings, Rendering, Ai, Tray, Hotkeys, Fullscreen, Localization, Interop, Storage, Bench
DesktopPet.Core/       Ai, Care, Danmaku, Hotkeys, I18n, Input, Interaction, Memory, Personas, Pets, Rendering, Roaming, Scheduling, Slicing, Storage, Summary
DesktopPet.Agent/      Capture, Analysis（变化检测在 AnalysisEngine 内；每日总结在 Core/Summary）
DesktopPet.Infra/      Providers(Model/Tts/Image), Tts, PipeRpc, Diagnostics(Logger/原子写/脱敏), Hotkey, Lifecycle, Storage(FileJsonStore)
DesktopPet.Core.Tests/ 切片对照/漫游/养成/人格拼接/亲密度/管道/存储
DesktopPet.Agent.Tests/ 截屏离线测试（录制帧序列）
DesktopPet.Infra.Tests/ Provider/PipeRpc/日志/原子写/凭据/生命周期
DesktopPet.App.Tests/   本地化/全屏抑制/热键/渲染缓存/进程指标
```

**测试策略**：核心管道（人格拼接 → 记忆注入 → 记账）用 mock `IModelProvider` 全链路单测；切片用与 TS 版同批测试图做对照断言；UI 走手工验收清单（见 UI 文档 §6）。

**组件名对齐**（代码真名）：`QuickBubbleController`（Core/Interaction）、`RoamEngine`+`RoamPhysics`（Core/Roaming）、`ConversationMemory`+`MemoryProfileExtractor`（Core/Memory）、`InteractionEngine`（Core/Interaction）、`HotkeyManager`（Infra/Hotkey）、`PetWindowManager`（App/Windows）、`FileJsonStore`（Infra/Storage）、`RollingFileLogger`（Infra/Diagnostics）、`GraphicsCaptureSource`（Agent/Capture）。

---

## 10. 待定决策（进入对应 Phase 前敲定）

| # | 决策 | 状态 | 默认倾向 |
|---|---|---|---|
| 1 | Provider 默认实现范围（仅 OpenAI 兼容？+ SAPI？） | 已定 | OpenAI 兼容（模型/生图）+ SAPI 默认 TTS；OneCore 自然语音 / OpenAI 兼容端点（见 `windows-tts-design.md`）；Edge TTS 直连不做（TLS 指纹 + 地域风控实证） |
| 2 | 记忆画像字段清单（称呼/作息/话题/摘要长度） | 已定 | 4 字段起步，摘要 ≤200 字（代码常量 200） |
| 3 | 亲密度与 XP 联动曲线（token 换算比例） | 已定 | 亲密度 = 对话轮次加权 + token 少量加成 |
| 4 | 主动互动触发阈值（久坐/深夜/持续编码定义） | 已定 | 久坐 60min / 深夜 23 点后 / 编码连续 2h |
| 5 | 自动更新方案（Velopack vs 自建） | 待定 | Velopack（活跃、NSIS 兼容） |
