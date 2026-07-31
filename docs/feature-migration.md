# DesktopPet（纯桌宠版）功能迁移文档

> 目标：从 `desktop-pet`（AgentPet 二开仓库，agent 监控 + 桌宠）中剥离出**纯桌宠**项目。
> 普通用户不需要 agent 监控，需要的是一个会陪伴、会养成、未来能对话、能总结一天的桌宠。
> 本文档分三部分：① 保留/去除清单；② 迁移注意事项；③ 预留能力接口设计。

---

## 1. 现状盘点（上游结构）

```
desktop-pet/
├── Sources/
│   ├── DesktopPetCore/     # 纯逻辑层（无 UI）：事件模型、状态机、养成规则、窗口规划
│   └── App/                # macOS SwiftUI 应用层：窗口、设置、菜单栏、控制器
├── windows/                # Windows/Linux Tauri(Rust) 平行实现（全功能对等）
├── web/                    # Astro + Cloudflare Workers 社区站（gallery/leaderboard/profile/admin）
├── landing/                # 落地页 + Cloudflare Functions + 宠物库 API
├── cdn-proxy/              # 宠物资源 CDN 热链代理
├── data/                   # 宠物库 manifest（petdex/openpets/merged）
├── scripts/                # 构建/发布脚本
├── docs/                   # 设计文档、翻译 README
├── Localizations/          # en/vi/zh-Hans/zh-Hant
└── Tests/                  # 单元测试
```

**上游数据流（核心要替换的部分）**：

```
agent hooks → `desktoppet hook` CLI → Unix socket / 文件队列
    → EventSocketServer → SessionStore(AgentSession) → AppDaemon
    → PetController.update(sessions:) → mood 聚合 → PetWindow 渲染
    → PetCareController.feedTokens/recordMeal → XP/等级/成就
```

**判定原则**：
- **保留**：桌宠渲染、情绪系统、养成系统、宠物包生态、台词泡泡、休息提醒 —— 与 agent 无关或可解耦。
- **去除**：一切以"agent 会话"为输入的东西（hook、socket、transcript、token 用量、终端聚焦、审批门），以及社区后端（web/landing/cdn-proxy）。
- **改造后保留**：本身是通用机制、但被 agent 数据模型"染色"的模块（mood 聚合、窗口规划、菜单栏、设置页、泡泡消息、反应式台词）。

---

## 2. 保留清单

### 2.1 DesktopPetCore（保留并改造）

| 文件 | 处理 | 说明 |
| --- | --- | --- |
| `PetMood.swift` | **改造** | `PetMood` 枚举保留（含 sleepy/levelup）；`MoodResolver.aggregate` 的入参从 `[AgentSession]` 改为 `[ActivitySession]` |
| `PetCare.swift` | 保留 | XP/等级/成就/饥饿纯规则。`feedTokens/recordMeal` 语义保留，新增"活动喂食"入口（见 §4） |
| `PetWindowPlanner.swift` | **改造** | 输入从 `[AgentSession]` 改为 `[ActivitySession]`；`sessionIDs` → `activityIDs`；split 逻辑保留 |
| `ProjectPetMapping.swift` | 保留 | 项目→宠物映射结构（后续"按项目养宠"仍有用） |
| `ProjectPetResolver.swift` | 保留 | 纯路径匹配逻辑，无 agent 依赖 |
| `BreakClock.swift` | 保留 | 休息提醒时钟，纯逻辑 |
| `PerKeyThrottle.swift` | 保留 | 通用节流工具 |

### 2.2 App（保留并改造）

| 文件 | 处理 | 说明 |
| --- | --- | --- |
| `PetController.swift` | **改造** | 核心改造点：`update(sessions:)` → `update(activities:)`；庆祝/升级闪光、break rest、split 窗口协调全保留 |
| `PetView.swift` | 保留 | 宠物渲染主视图（1371 行，最大文件，零 agent 依赖） |
| `PetWindowController.swift` / `PetWindowModel.swift` | 保留 | NSPanel 无边框浮动窗管理 |
| `ImageSpriteView.swift` / `ImagePetStore.swift` / `SpriteSlicer.swift` | 保留 | 精灵图加载/切帧/渲染 |
| `PetMoodFX.swift` / `PetBindings.swift` | 保留 | mood→帧/动效映射 |
| `ClickThroughHostingView.swift` | 保留 | 鼠标穿透窗口 |
| `PetCareController.swift` / `CareTabView.swift` / `PetStatsView.swift` / `StageBadge.swift` / `IdleBoost.swift` | 保留 | 养成系统 UI（喂食改为活动驱动后 UI 基本不变） |
| `PetInstaller.swift` / `PetBrowser.swift` / `BrowsePetsView.swift` / `CreatePetView.swift` | 保留 | 宠物包下载/浏览/制作（Petdex 生态，与 agent 无关） |
| `BubbleMessages.swift` / `ChatSettings.swift` / `BubbleSettings.swift` / `BubbleSettingsView.swift` / `MenuBarChatBubble.swift` / `GrowingTextEditor.swift` | **改造** | 消息按 `AgentKind` 分组 → 改为按活动类型/情绪分组；自定义台词机制保留 |
| `BreakReminderController.swift` / `BreakReminderSettings.swift` | 保留 | 休息提醒 |
| `NotificationManager.swift` / `SoundSettings.swift` | 保留 | 通知文案从"agent 完成"改为通用事件 |
| `StatusBarController.swift` / `MenuBarContentView.swift` | **改造** | 去掉 agent 列表与计数；保留菜单栏宠物图标、Care 快捷入口、设置入口 |
| `SettingsModel.swift` | **改造** | 删掉 hook 安装/审批门逻辑；保留通知权限管理 |
| `SetupView.swift` | **改造** | tab 从 5 个（pet/bubble/care/general/advanced）重构为 4 个（pet/bubble/care/advanced）；删 agents 安装区 |
| `SettingsWindowController.swift` / `OnboardingView.swift` | **改造** | 首启引导不再出现"安装 hook" |
| `Theme.swift` / `ColorSwitch.swift` / `AppLanguage.swift` / `NativeSearchField.swift` | 保留 | 通用 UI 基建 |
| `LoginItem.swift` | 保留 | 开机自启 |
| `UpdaterController.swift` | 保留 | Sparkle 自动更新 |
| `DesktopPetApp.swift` / `AppEntry.swift` | **改造** | 去掉 `hook` / `run` 两个 CLI 分支，仅保留 GUI 入口 |

### 2.3 周边资源

| 路径 | 处理 | 说明 |
| --- | --- | --- |
| `data/` | 保留 | 宠物库 manifest，宠物生态核心 |
| `Localizations/` | 保留 | 删 agent 相关词条，保留其余 |
| `assets/` | 保留 | 换新横幅/截图 |
| `scripts/build-app.sh`、`ci-dmg.sh`、`release.sh` 等 | 保留 | 构建发布脚本；`make-announce*`、`make-banner*` 可删 |
| `Tests/` | 保留 | 仅保留：`BubbleSettingsTests`、`CareChatTests`、`IdleBoostTests`、`PetBindingsTests`、`SpriteSlicerTests`、`AchievementTests`、`BreakClockTests`、`PetCareTests` |

---

## 3. 去除清单

### 3.1 DesktopPetCore（全部删除）

| 文件 | 职责 |
| --- | --- |
| `AgentEvent.swift` / `AgentSession.swift` / `AgentState.swift` | agent 会话数据模型（**整个项目的数据轴心**） |
| `AgentCatalog.swift` | agent 目录（11 种 agent 元数据） |
| `AgentHooks.swift` / `HookInstaller.swift` / `HookPayloads.swift` / `HookArguments.swift` | hook 安装与载荷解析 |
| `ClaudeHookPayload.swift` / `CodexHookConfig.swift` / `AntigravityHookPayload.swift` | 各 agent 专属载荷 |
| `EventSocketServer.swift` / `EventSender.swift` / `EventCoding.swift` | Unix socket 事件通道（hook 专用） |
| `StateMapper.swift` | agent 事件名→状态映射 |
| `TranscriptReader.swift` | 读取 Claude/Codex 对话记录（token/标题/模型） |
| `TerminalInfo.swift` / `QuestionDetector.swift` | 终端检测 / 问句识别 |
| `SessionStore.swift` / `SessionArchive.swift` / `SessionArchiveStore.swift` | 会话内存/归档存储 |
| `PendingApprovalRegistry.swift` / `ApprovalGateConfig.swift` | 审批门 |
| `ModelPricing.swift` | 模型计费 |
| `ActivityFormatter.swift` / `TickerFormatter.swift` | agent 活动文案格式化 |
| `RunArguments.swift` | `desktoppet run` 包装器参数 |

> `ActivityFormatter` 的"拟人化活动词汇"（chef/engineer/wizard 五主题短语池）值得抄进新项目，作为对话/活动文案素材。

### 3.2 App（全部删除）

| 文件 | 职责 |
| --- | --- |
| `AppDaemon.swift` | agent 会话 daemon（**被新的活动引擎替代，见 §4**） |
| `AgentIcons.swift` | agent 品牌图标 |
| `HistoryTabView.swift` | 会话历史 tab |
| `CareSyncController.swift` | GitHub 登录 + 云端同步 |
| `OpenUsageClient.swift` / `NativeUsageProbe.swift` | 订阅用量探测 |
| `ProjectUsageStore.swift` | 项目 token 用量统计 |
| `ReactiveEngine.swift` | 用量/会话指标反应台词（机制可抄到新项目，见 §4） |
| `SettingsDemoPanel.swift` | agent 事件演示面板 |
| `TerminalFocus.swift` | 点击聚焦 agent 终端 |
| `CLI.swift` / `RunCLI.swift` | `desktoppet hook/run` 命令行 |
| `CoffeeView.swift` + `donate-vietqr.png` | 原作者捐赠入口 |

### 3.3 目录级删除

| 路径 | 理由 |
| --- | --- |
| `web/` | 社区站（gallery/leaderboard/profile/admin/usage）整套后端，纯桌宠不需要；宠物库浏览走 Petdex 公共 API（保留 `PetBrowser` 即可） |
| `landing/` | 落地页 + Cloudflare Functions（宠物提交/审核/API） |
| `cdn-proxy/` | CDN 热链代理（上游服务端组件） |
| `windows/` | Tauri 平行实现整体是 agent 版。**建议 v2 再裁剪**（工作量 ≈ 一个完整应用），v1 只交付 macOS；见 §5 路线图 |
| `.github/workflows/` | 保留 `ci.yml`，删 `pages.yml`（web 部署）、`windows-build.yml`、`linux-build.yml`，`release.yml` 改为只出 macOS |
| `docs/specs/` 中 agent 相关设计文档 | 保留 `2026-05-29-agentpet-design.md` 作架构参考（其"事件→状态→UI"分层思想是我们要保留的骨架） |
| `Package.swift` 中 Sparkle 之外无其他依赖 | 保持仅 Sparkle |

---

## 4. 迁移注意事项（耦合点与坑）

### 4.1 AgentSession 是数据轴心，必须先替换再动其他
以下全部直接依赖 `AgentSession`/`AgentState`，改动顺序必须是：**先定义 `ActivityEvent`/`ActivitySession`（§4.2）→ 再改 MoodResolver/PetWindowPlanner/PetController → 最后动 UI 层**：
`PetMood.MoodResolver.aggregate`、`PetWindowPlanner.plan`、`PetController.update`、`TickerFormatter`、`StatusBarController`、`BubbleMessages`、`MenuBarContentView`、`ReactiveEngine`、`CareTabView`（按会话列表展示）、`HistoryTabView`。

### 4.2 新数据流设计（替换 AppDaemon）
```
桌面活动源(DesktopMonitor/对话/未来插件)
    → ActivityEvent { type, source, timestamp, payload }
    → EventBus（App 内部，预留 IPC 扩展）
    → ActivityStore（替代 SessionStore：聚合、超时、归档）
    → PetController.update(activities:)          # 驱动 mood + 泡泡
    → PetCareController.feed(activity:)          # 驱动 XP（活动喂食）
    → StatusBarController / DailyReporter        # 菜单栏 + 每日总结
```
保留上游三个好设计：
1. **事件队列回放**：App 未运行期间的事件先落盘，启动后按原时间戳回放（原 `EventSocketServer.drainQueue`）。
2. **超时收敛**：`done→idle→remove` 的超时链（原 `SessionStore` 30s/600s/300s/90s）。
3. **节流**：昂贵处理按 key 节流（原 `PerKeyThrottle`）。

### 4.3 数据/存档兼容
- `PetCareState` 是 Codable 持久化（`~/.desktoppet/care/*.json`），字段全部保留，新"活动喂食"增加可选字段即可，**老用户存档无缝升级**。
- 数据目录：**继续用 `~/.desktoppet/`**（宠物包、care 存档直接继承），但删除 agent 相关子目录（`queue/`、`sessions/`、`usage/`）。若想完全隔离，改 `~/.desktoppet-pure/` 并在文档说明用户需重新下载宠物包——不推荐。
- hook 残留：老版本可能已给 Claude Code 等写入过 hook 配置；纯桌宠版不提供卸载入口的话，应在首次启动时提示用户去 Settings 卸载（或文档说明）。**注意：不能主动改用户 agent 配置文件，只提示。**

### 4.4 宠物包格式契约（不能动）
`pet.json` 的 mood 状态集：`idle/working/waiting/done/celebrate`（+ 应用层 `sleepy/levelup`）。Petdex 生态兼容靠它，**保留原样**。未来对话功能复用 `working`（思考中）、`waiting`（等你说话）动画即可，无需扩展格式。

### 4.5 许可与署名
MIT 许可要求保留原作者版权声明：README 顶部"renamed, modified fork of AgentPet"声明保留，`LICENSE` 原样保留。删除原作者捐赠链接（Ko-fi/Buy me a coffee）与 `CoffeeView`，但可在 README 致谢区保留社区贡献者名单。

### 4.6 其他
- 本地化：`Localizations/*.lproj` 中 agent 相关 key 会残留，裁剪后跑一遍 `genstrings`/同步清理；中文为首要语言。
- `BubbleSettingsView` 中"per-agent"配置 UI 删除，改"per-activity-type"。
- 测试目标：`DesktopPetAppTests` 依赖 `desktoppet` target，删除 agent 测试后保留目标结构即可；`DesktopPetCoreTests` 同理。
- CI：macOS build 用 Xcode 16 / Swift 6 不变；删除需要 `MATRIX_OS` 的 windows/linux job。

---

## 5. 预留能力接口设计（给桌宠赋予能力）

> 本节为**接口草案**，迁移 Phase 1 落地时只搭骨架（协议 + 空实现），不实现具体 AI 能力。
> 目标能力：① 接入多模态模型；② 监控桌面动态；③ 实时对话；④ 每日总结；⑤ 生成当日总结图。

### 5.1 模块划分

```
DesktopPetCore（纯逻辑，可测试）
├── ActivityEvent.swift        # 统一活动事件模型
├── ActivityStore.swift        # 活动聚合/超时/归档（替代 SessionStore）
├── PetBrain.swift             # 模型适配协议（对话/总结/生图）
└── DailyReport.swift          # 每日报告聚合规则

App（应用层）
├── EventBus.swift             # 内部事件总线
├── DesktopMonitor.swift       # 桌面监控（NSWorkspace/AX）
├── ConversationController.swift # 对话状态机 + 语音/文本输入
├── AIClient.swift             # OpenAI 兼容 / Anthropic / Ollama 适配器
└── DailyReporter.swift        # 每日定时总结 + 生图 + 报告落盘
```

### 5.2 核心协议草案

```swift
// 统一活动事件：一切驱动桌宠的输入都归一化成它
public struct ActivityEvent: Codable, Sendable, Equatable {
    public enum Kind: String, Codable, Sendable {
        case appFocus      // 切换到某应用（桌面监控）
        case inputBurst    // 键盘/鼠标活跃（桌面监控）
        case chatMessage   // 与宠物对话（对话系统）
        case dailySummary  // 每日总结生成（定时器）
        case userAction    // 用户手动喂食/互动
        // 预留：agentActivity（未来插件化恢复 agent 能力）
    }
    public var id: String
    public var kind: Kind
    public var source: String       // "desktopMonitor" / "conversation" / ...
    public var timestamp: Date
    public var title: String        // 显示用：如应用名、对话摘要
    public var detail: String?      // 附加上下文
    public var weight: Double       // 喂食权重（0…1）
}

// 活动会话：ActivityStore 按"当前活跃事务"聚合的产物，替代 AgentSession
public struct ActivitySession: Codable, Sendable, Equatable {
    public var id: String
    public var kind: ActivityEvent.Kind
    public var state: ActivityState   // .active / .paused / .done（复用 mood 语义）
    public var title: String
    public var updatedAt: Date
}

// 模型能力适配：多模态入口，一个协议管对话/总结/生图
public protocol PetBrain: Sendable {
    var providerName: String { get }            // "OpenAI" / "Anthropic" / "Ollama"
    func chat(_ turns: [ChatTurn]) async throws -> String          // 实时对话（文本/后续加音频）
    func summarize(activities: [ActivityEvent]) async throws -> String  // 每日总结
    func generateImage(prompt: String) async throws -> Data        // 总结图（返回 PNG/JPEG）
}
// 内置实现：AIClient.openAICompatible(baseURL:apiKey:model:) 可同时覆盖 OpenAI/DeepSeek/本地 vLLM；
// Ollama 免 key 本地跑；Anthropic 走 messages API。apiKey 只存 Keychain/UserDefaults 提示，不写进代码。

// 桌面监控源：把"桌面上发生了什么"变成 ActivityEvent
public protocol ActivitySource: Sendable {
    var sourceName: String { get }
    func start(emitting: @Sendable @escaping (ActivityEvent) -> Void)
    func stop()
}
```

### 5.3 关键设计决策

| 决策点 | 方案 |
| --- | --- |
| mood 与活动映射 | `appFocus`/`chatMessage` → `.working`（动画复用）；`dailySummary` 生成中 → `.waiting`；无活动 → `.idle`。**宠物包格式零改动** |
| 对话入口 | 点击宠物 → 输入框（复用 `GrowingTextEditor`）；Ctrl/Cmd+双击唤醒；语音输入 v2（macOS 无原生 STT，接 Whisper 本地或云端，预留 `ChatTurn.audio` 字段） |
| 上下文窗口 | `ConversationController` 维护最近 N 轮（默认 12 轮）+ 当日活动摘要注入 system prompt，让宠物"知道你今天干了什么" |
| 每日总结触发 | `DailyReporter`：本地 23:30 定时（`BreakClock` 同款机制）或用户手动触发；总结内容 = `ActivityStore` 当日事件聚合 → `PetBrain.summarize` → 落盘 `~/.desktoppet/reports/2026-08-01.md`；配图 = `generateImage` 存同目录，**喂给宠物展示为"记忆"** |
| 隐私 | 桌面监控默认只记应用名/窗口标题/活跃时长，**不截屏不记内容**；截图/OCR 能力做成显式开关，默认关 |
| 喂食打通 | `PetCareController.feed(activity:)`：`weight` → XP（1 XP = 5000 tokens 的既有比例保留作参考，对话 1 轮 ≈ 少量 XP，总结完成 = 1 顿正餐）。`recordMeal` 语义保留 |
| 架构可逆 | `ActivityEvent` 的 `source` 字段预留 `"agent"` 通道——将来想恢复 agent 监控，只需加一个 `AgentBridgeSource`，不动宠物侧任何代码 |

### 5.4 实施路线图

| 阶段 | 内容 | 交付 |
| --- | --- | --- |
| **Phase 1（本次）** | 纯桌宠裁剪：删 agent 全家桶，ActivityEvent/EventBus 骨架 + `PetBrain` 协议（空实现 `NoopBrain`），跑通编译 + 现有 pet 测试 | v0.1 macOS 可用 |
| **Phase 2** | DesktopMonitor + EventBus 接通 → 宠物按"你在用 X 应用"产生 mood/喂食；对话接入（先 OpenAI 兼容 + Ollama） | v0.2 |
| **Phase 3** | 每日总结 + 总结图生成 + 报告展示（宠物"记忆"交互） | v0.3 |
| **Phase 4** | Windows/Linux Tauri 版裁剪（以 Phase 1 的 macOS 版为基准，裁剪 `windows/`） | v1.0 |

---

## 6. 一句话总结

**迁移 = 保留"宠物侧"（渲染/养成/宠物包/台词/设置），把"输入侧"从 agent hooks 换成 ActivityEvent 总线，并在总线末端预留 PetBrain（对话/总结/生图）三个协议方法。** 宠物包格式、Care 存档、MIT 署名是三个不能破坏的契约。
