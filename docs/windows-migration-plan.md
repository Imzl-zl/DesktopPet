# Windows 版技术栈迁移计划：Tauri → .NET 8 + WPF

> 状态：草案 v1 · 范围：`windows/` 目录整体替换 · 保留 macOS 版不变

## 1. 背景与动机

### 1.1 现状

- `windows/` 是 Tauri 2 + WebView2 前端（TypeScript ~7000 行 + Rust 侧 ~500 行），移植自 macOS Swift/SwiftUI 版。
- 宠物窗口为**透明 + 置顶 + 无边框**窗口，这是 WebView2 的弱项场景。

### 1.2 性能问题的根因（不是代码质量问题，是技术栈限制）

| 问题 | 根因 |
|---|---|
| 动画卡顿、CPU 占用高 | WebView2 在 `WS_EX_LAYERED` 透明窗口下**禁用硬件加速**，逐帧精灵动画走软件合成 |
| 拖拽延迟、"拖不动" | 鼠标事件 → JS → IPC → Rust → 窗口移动的链路长；透明窗口 hit-test 与 webview 合成互相干扰，需要 Rust 侧 `set_hit_rect` + 忽略光标事件来打补丁 |
| 内存占用大 | WebView2 进程常驻（浏览器内核 ~100MB+），桌宠是开机常驻程序，用户直接感知 |
| 架构脆弱 | 拖拽/点击穿透/置顶互相打架，每修一个 bug 引入一个新 workaround（`pet-pointer-drag.ts`、`window-drag.ts`、`pet-interaction-lease.ts` 等补丁层） |

### 1.3 新需求对选型的影响（本计划的关键输入）

后续要接入：
1. **多模态 AI**：每秒截屏一次分析用户在做什么 → 需要原生截屏 API + 后台进程
2. **全屏弹幕**：透明全屏覆盖层 + 高频滚动动画 → 需要 GPU 合成
3. **气泡对话**：与用户对话 → 按需创建的对话窗口（非常驻）

这三项叠加后，Web 栈（DOM 渲染 + 透明窗口软渲染）必然扛不住；原生渲染是唯一合理选择。

## 2. 目标技术栈

| 项 | 选择 | 理由 |
|---|---|---|
| 运行时 | **.NET 8 (LTS)** | 长期支持、WinRT 互操作好、WPF 生态成熟 |
| UI 框架 | **WPF**（宠物窗口、浮球、设置、气泡） | 透明窗口 + 硬件加速、`DispatcherTimer`/`CompositionTarget.Rendering` 动画、成熟稳定 |
| 弹幕层 | 独立全屏透明窗口，**Win2D（Direct2D 封装）**渲染 | GPU 合成滚动文本，几百条弹幕 60fps |
| 截屏 | **`Windows.Graphics.Capture`**（WinRT API，C# 一等公民） | Win11 原生、无需前台窗口、权限弹窗一次；Win10 回退 DXGI Desktop Duplication |
| 图像解码/像素处理 | `System.Drawing.Common`（仅 Windows 用）或 `ImageSharp` | 精灵图切片需要逐像素 alpha 扫描 |
| 模型调用 | `HttpClient` + OpenAI 兼容协议 | 与现有 `ImageGen` 的调用方式一致（Rust 侧已有 ureq 实现可对照） |
| 持久化 | JSON 文件（`%APPDATA%/DesktopPet/`） | 与现有 `pet_positions.json`、localStorage 数据语义对齐 |
| 多语言 | `.resx` 或 JSON 资源 | 现有 4 语言（en/zh-Hans/zh-Hant/vi） |
| 自动更新 | 后置决策：Squirrel.Windows / 自建 / Velopack | 见 §9 风险 |
| 打包 | NSIS 或 WiX（保持现有安装体验） | 现有产物是 NSIS + MSI |
| UI 设计语言 | **「Lumen 流明」**（轻盈简约 · 光感 · 毛玻璃），替换 Ember Glass | 完整规范见 `docs/windows-ui-design.md`，Phase 4 落地 |

**明确不选**：Electron/Tauri（软渲染）、Unity/Godot（内存大、常驻场景过重）、纯 C++（开发量 3-5 倍，收益不明显；弹幕/截屏在 C# 都有现成封装）。

## 3. 现状功能盘点与迁移映射

### 3.1 Tauri 版模块 → WPF 对应

| 现有模块（TS） | 行数 | 功能 | WPF 对应 | 工作量 |
|---|---|---|---|---|
| `pet.ts` | 325 | 精灵渲染、**自动切片**（alpha-gutter）、alpha hitTest | `SpriteSlicer.cs`（mac 版 `SpriteSlicer.swift` 可对照）+ `PetRenderer.cs`（帧缓存 + `WriteableBitmap`） | 中 |
| `roam/`（engine/modes/environment/physics） | ~800 | 漫游引擎：tick 循环、抛掷/下落物理、窗口边界感知、睡眠/漫步模式 | `MovementEngine.cs`（`DispatcherTimer` 或独立线程 + 位置插值） | 中 |
| `window-drag.ts` + Rust `set_hit_rect` | 179 | 拖拽（Rust 侧忽略光标事件补丁） | WPF `Window.DragMove()` + 鼠标捕获 + `MoveWindow`，**无需补丁层** | 低 |
| `pet-pointer-drag.ts` / `pet-interaction-lease.ts` | 145 | 指针拖拽 + 多宠物互斥 | 鼠标事件处理 + 全局互斥状态 | 低 |
| `bubble.ts` | 40 | 单行胶囊气泡 + 交叉淡入 | `BubbleView.cs`（自绘或 XAML，`OpacityAnimation`） | 低 |
| `floating-ball.ts` + drag/pointer | ~500 | 浮球：**左键气泡菜单**（输入 + 预设 + 发送）、右键设置、拖拽持久化、球内活体宠物 | `FloatingBallWindow.cs`（菜单内嵌气泡区 + AI 模式切换行） | 中 |
| `popover.ts` | 128 | 托盘右键弹窗：显示开关、大小滑杆、设置/更新/退出 | WPF `ContextMenu`（托盘 `NotifyIcon`，用 `H.NotifyIcon` 或 WinForms 兼容层） | 低 |
| `care.ts` | 256 | 养成：XP、等级、5 阶段进化、饥饿状态、14 成就、streak（**token 经济学已有**：5000 token=1 XP、会话=25 XP） | `CareEngine.cs`（沿用 token 经济学）+ JSON 持久化 | 中 |
| `activity.ts` | — | 气泡台词/聊天内容 | 资源数据迁移 | 低 |
| `quick-bubble.ts` | 77 | 快速气泡：**浮球发送 → 全员广播**；**点击宠物单只说/全员说**（`LEFT_CLICK_KEY`）；预设池 + 时长配置 | `QuickBubbleService.cs`（事件总线：浮球 → 各宠物窗口）+ 预设池 JSON | 低 |
| `catalog.ts` | 108 | CDN 宠物目录（manifest 拉取） | `CatalogClient.cs`（HttpClient + JSON） | 低 |
| `pets.ts` | 180 | 宠物实例存储（`PetStore.instances[]`）、**多实例并存、每只独立显隐/大小/漫游** | `PetStore.cs`（继承现状数据结构，JSON 迁移） | 低 |
| `settings.ts` | **1986** | 设置页：宠物管理、切片预览、页面列表、**AI 生成宠物**（ImageGen 卡片：base/key/模型/尺寸/参考图，调 `generate_image`） | `SettingsWindow.cs`（最大单项工作）；ImageGen 保留并归入生图 Provider 体系（`IImageProvider`） | **高** |
| `i18n.ts` | 1127 | 4 语言翻译 | resx / JSON 资源 | 中 |
| `state.ts` | 137 | ActivityStore + 心情推导（Phase 2 预留接口） | `ActivityStore.cs`（保持接口形状，为 AI 功能预留） | 低 |
| `main.ts` | 80 | bootstrap、**多窗口同步（`sync_desktop_pet_windows`）**、自动更新 | `App.xaml.cs` + 多实例窗口管理器（每宠物一窗口，创建/销毁/显隐同步） | 低 |

### 3.2 Rust 侧功能 → C# 直接实现

| Rust 功能 | C# 对应 |
|---|---|
| `set_hit_rect` / 忽略光标事件（透明窗口补丁） | 不需要，WPF 透明窗口 + alpha hitTest 原生解决 |
| 宠物位置持久化 `pet_positions.json` | `File` + JSON 序列化 |
| 系统窗口枚举（`sys_windows.rs`，漫游避让） | `EnumWindows` P/Invoke（约 30 行） |
| 图像生成 `generate_image`（OpenAI 兼容 API） | `HttpClient` + `System.Text.Json` |
| 托盘菜单、语言持久化 | WPF 原生 + `%APPDATA%` 配置文件 |
| 自动更新 | 后置（§9） |

### 3.3 macOS 版独有、Windows 版目前没有的功能（可选纳入）

`BreakReminder`（休息提醒）、`ImageGenView`（AI 生成宠物）、`OnboardingView`、`PetBrowser`、声音设置。**建议 Phase 4 之后按需补**，不阻塞迁移。

## 4. 目标架构

### 4.1 进程划分（双进程，命名管道 IPC）

```
┌───────────────────────────┐        ┌────────────────────────────┐
│  PetApp.exe（前台 UI）      │        │  PetAgent.exe（后台分析）     │
│                           │        │                            │
│  常驻：                     │        │  · 每秒截屏（Graphics        │
│  · 宠物窗口 ×N（透明置顶）    │        │    Capture）                │
│  · 浮球窗口                 │  IPC   │  · 变化检测（帧哈希，节流 API） │
│  · 托盘 + 菜单              │◄──────►│  · 多模态推理（本地/云端）      │
│                           │ 管道    │  · 行为决策 → 事件推给 UI     │
│  按需（模式激活才创建，      │        │                            │
│  切换/关闭即销毁）：          │        │                            │
│  · 弹幕层（全屏透明，Win2D）  │        │                            │
│  · AI 对话气泡窗口           │        │                            │
│  · 设置窗口                 │        │                            │
└───────────────────────────┘        └────────────────────────────┘
```

- **为什么拆**：模型推理（秒级延迟）和网络 IO 不能阻塞宠物动画（16ms 预算）。AI 挂掉/卡顿时宠物照常跑。
- **单进程兜底**：Phase 0-4 先单进程（AI 未接入），进程模型在代码里按模块隔离，Phase 5 拆进程时只动 IPC 层。
- **窗口生命周期原则**：桌面常驻只有宠物 + 浮球 + 托盘；弹幕层、对话气泡、设置窗全部按需创建、关闭即销毁（不激活的模式零窗口零渲染）。

### 4.2 解决方案结构（`.sln`）

```
windows-native/
├── src/
│   ├── DesktopPet.App/          # WPF 主程序（窗口、渲染、UI）
│   ├── DesktopPet.Core/         # 领域逻辑（切片、养成、漫游物理、存储）— 可测试
│   ├── DesktopPet.Agent/        # 截屏 + 多模态（Phase 5）
│   └── DesktopPet.AgentHost/    # Agent 宿主（控制台，可独立跑）
├── tests/
│   ├── DesktopPet.Core.Tests/   # xUnit（切片算法、养成、物理）
│   └── DesktopPet.Agent.Tests/
└── packaging/                   # NSIS/WiX 脚本
```

- `Core` 不依赖 WPF → 切片、养成、物理全部可单元测试。
- 现有 vitest 测试（`pet.test.ts`、`care.test.ts` 等 ~20 个测试文件）中**纯逻辑测试 1:1 移植**到 xUnit；DOM/canvas 相关测试（拖拽、指针）改为 WPF 集成测试或手工验收清单。

## 5. 分阶段迁移路线

### Phase 0 — 骨架与验收基线（1 周）
- 建解决方案；透明置顶无边框宠物窗口 + 鼠标拖拽（无补丁层）
- **多实例窗口管理器**：每宠物一窗口（继承现状 `PetStore.instances`），创建/销毁/独立显隐 + **全局显隐开关**（托盘，继承现状 `set_desktop_pets_visible` 语义）
- 托盘图标 + 显示/隐藏 + 退出
- 打包脚本（NSIS）跑通
- **验收**：拖拽跟手（见 §7 指标）、空闲 CPU < 1%；多只宠物同时展示、单只显隐、全局隐藏均正常

### Phase 1 — 宠物核心：自定义宠物全保留（1 周）
- `SpriteSlicer.cs`：alpha-gutter 切片（对照 `pet.ts:slice()` 与 `SpriteSlicer.swift`）+ 固定网格回退
- `PetRenderer.cs`：帧缓存位图 + `WriteableBitmap` 逐帧绘制；动画行/idle 播放列表
- alpha hitTest（点击透明区不拖窗）
- 导入自定义精灵图 UI（文件选择 + 切片预览）
- **验收**：现有所有宠物包（CDN 目录 + 本地导入）切片结果与 TS 版一致（用同一批测试图做对照测试）

### Phase 2 — 交互层（1 周）
- 气泡（单行胶囊 + 交叉淡入）、快速气泡（**浮球发送 → 全员同时说**；点击宠物 → 单只说/全员说；预设池 + 时长 4s 默认）
- 漫游引擎：tick、抛掷/下落物理、边界避让、睡眠/漫步模式
- 浮球（左键气泡菜单、右键设置、球内活体宠物、位置持久化）
- **验收**：拖拽 + 抛掷手感与 macOS 版一致；漫游不穿窗、不越屏；全员/单只气泡行为与 Tauri 版一致

### Phase 3 — 数据与养成（1 周）
- `CareEngine`（XP/等级/5 阶段/饥饿，沿用 token 经济学：5000 token=1 XP、会话=25 XP）、`PetStore`（多实例）、`CatalogClient`
- **成长表现层**（精灵图不变，见 `docs/windows-ui-design.md` §3.7）：阶段行为解锁（cursor/climb 漫游模式）+ 视觉叠加（光晕/辉光/星点/皇冠）+ 升级反馈（星点迸发 + 进化气泡）；`OverlayRenderer` 与宠物渲染器同帧绘制
- 位置/可见性/养成状态 JSON 持久化；从 Tauri 版 localStorage 迁移数据（一次性迁移工具，含 `ap_care_*` 养成状态）
- **验收**：xUnit 测试覆盖切片 + 养成 + 物理核心逻辑；五阶段表现逐级可验证（测试宠物直接注入 XP）

### Phase 4 — 设置与多语言 + UI 全面重设计（1-2 周，最大块）
- **UI 按「Lumen」设计语言全面重做**（`docs/windows-ui-design.md`）：浅色毛玻璃 + 深色跟随、左侧图标导航设置页、实时动画宠物卡片、统一 150-300ms 动效规范；气泡/浮球/托盘菜单同步换肤
- `SettingsWindow` 全量重写（宠物管理、切片预览、目录浏览、语言切换）
- 4 语言资源迁移
- **验收**：设置功能与 Tauri 版逐项对照通过；UI 通过 §6 验收检查单（深浅模式、高 DPI、动效、对比度）

### Phase 5 — AI 能力（新功能，原生实现，2 周）
- `PetAgent`：`Windows.Graphics.Capture` 每秒截屏 → 帧哈希变化检测 → 仅变化时调多模态（**分析开关**）
- **AI 总开关**：设置页 AI 助手页顶部，一键关闭全部 AI 功能（分析/输出/记忆/主动互动/总结），关闭后无截屏、无网络调用、无后台进程——纯桌宠模式
- **人格系统**（见 `docs/ai-personas.md`）：Base Prompt + 人格 Prompt 拼接；内置 7 人格（暖男/高冷男神/小狼狗/小奶狗/高冷女神/绿茶/知性大姐姐）+ 自定义人格（`personas.json`）；设置页人格卡片网格 + 对话窗顶部快捷切换；影响对话/弹幕/分析评论全部 AI 输出
- 行为决策 → IPC 事件 → 宠物反应（气泡台词、心情）
- **输出模式三选一（弹幕 / 对话 / 静默）**：弹幕层（Win2D，事件驱动刷弹幕）与对话气泡窗口均按模式激活创建、切换即销毁；浮球菜单 + 设置页均可切换。**语义**：模式只决定 AI 主动输出形式；用户主动对话随时可开（对话窗不受模式限制）；静默 = 截屏分析停止 + 无主动输出（最干净）
- 对话气泡：打字机效果 + 上下文（最近 N 条屏幕事件）；**屏幕上下文开关**（默认关，隐私：开启后对话请求才携带当前屏幕描述/截图）
- **验收**：AI 推理期间宠物动画不掉帧；弹幕 60fps；模式切换 < 300ms 且关闭后无窗口/渲染残留；7 种内置人格切换后回复风格明显区分（每人格用固定测试句验证）；AI 总开关关闭后任务管理器无 Agent 进程、无网络连接

### Phase 6 — 陪伴增强（调研采纳的 P0 + 部分 P1，2 周）
> 功能详情与来源见 `docs/feature-research.md`（调研 BongoCat / C.AI / 星野 / 筑梦岛 / 心光等成熟产品）。**范围口径**：Phase 6 = P0 全部 + P1 中的每日总结/语音；键鼠响应（P1）后置。
> **开关原则**：以下所有功能在设置页 AI 助手页都有独立开关；AI 总开关关闭时全部失效。
- **记忆系统**：`MemoryStore.cs` 结构化用户画像（称呼/作息/常聊话题/对话摘要），每轮请求注入；配合主动互动隔天提起（"你昨晚又加班到两点"）；**记忆开关**（默认开，关 = 不记录不注入，画像文件不落盘）
- **主动互动**：AI 驱动的问候/评论——定时（早晚/久坐）+ 事件驱动（持续编码/切换窗口类型/摸鱼），按当前人格输出到弹幕或对话；**主动互动开关**（默认开）+ 频率档（少/中/多）；**屏幕感知开关**（默认开，关 = 不再从截屏推断用户活动，定时问候仍可用）
- **亲密度系统**：新维度 `intimacy`（0-100，随对话/token/连续天数增长，长期不互动缓慢下降不归零）；人格表现按亲密度分档（称呼"你"→"宝贝"、关心频率、亲昵度）；与 XP 养成双线并行（XP=外观/行为，亲密度=AI 关系）；**亲密度开关**（默认开，关 = 固定为人格基础档）
- **全局快捷键**：`Ctrl+Alt+H` 显隐 / `Ctrl+Alt+M` 切换模式 / `Ctrl+Alt+S` 设置 / `Ctrl+Alt+Q` 退出（`RegisterHotKey` P/Invoke）
- **对话增强**：从这里重新开始（清空上下文重新聊，保留记忆/亲密度）；自定义人格支持示例对话输入（C.AI 经验：示例 > 描述）。~~重写回复~~已砍（对话场景无使用场景）
- **每日总结 + 总结图**（宠物日记）：每天结束时 PetAgent 生成当日总结（做了什么/聊了什么/心情）；**总结全局一份**（记录的是"你的一天"，多宠物共享，各宠物可就总结发评论）；总结文本 → `ImagePromptBuilder` → 生图模型生成「当日总结图」（可插拔 `IImageProvider`，复用 `providers.json` 生图连接）；设置页宠物卡片可查看（文本+图），对话中可问"今天干了啥""把昨天画出来"；生图失败不影响文本。**开关**：每日总结开关（默认开）+ 总结图开关（默认**关**——云端费用+隐私，显式开启）
- **语音输出**：对话模式可开启朗读（Edge TTS 免费协议），默认关、弹幕模式不朗读；**语音开关**（AI 助手页，默认关）
- **验收**：记忆跨天生效（隔天主动提及验证）；亲密度档位切换后人格称呼/语气变化可验证；快捷键全通；重开对话不影响记忆与亲密度；每日总结当日可生成；语音开关生效；**全部开关关闭后无后台进程、无网络连接（防火墙/任务管理器验证）**

### 里程碑总览

| 里程碑 | 内容 | 时长 |
|---|---|---|
| M1 | Phase 0-1：骨架 + 宠物核心（可替换 Tauri 版的宠物展示） | 2 周 |
| M2 | Phase 2-3：交互 + 养成 | 3 周 |
| M3 | Phase 4：设置全量 | 4-5 周 |
| M4 | Phase 5：AI 能力（分析 + 人格 + 弹幕/对话模式） | 6-7 周 |
| M5 | Phase 6：陪伴增强（记忆/主动互动/亲密度/快捷键） | 8-9 周 |
| M6 | 并行：Tauri 版下线（git 保留 `windows/` 历史分支） | — |

## 6. 关键技术实现要点

### 6.1 透明置顶窗口（WPF）
```csharp
// 核心三件套：无边框 + 透明 + 置顶
WindowStyle = WindowStyle.None;
AllowsTransparency = true;
Background = Brushes.Transparent;
Topmost = true;
ShowInTaskbar = false;
```
注意：`AllowsTransparency=true` 会关闭硬件加速的 DWM 合成部分路径，所以**动画用 `WriteableBitmap` 直写像素 + `CompositionTarget.Rendering`**，不要依赖 XAML 元素动画做高频精灵帧（XAML 元素级动画在透明窗口下性能一般）。弹幕层用 Win2D 绕过此限制。

### 6.2 精灵渲染
- 切片结果缓存为每帧 `BitmapSource`（与 TS 版"每帧从大图裁剪"相比是净优化）
- 每帧 alpha 掩码缓存 → `hitTest` O(1) 查掩码，不再逐像素读
- 缩放用 `BitmapScalingMode.NearestNeighbor`（像素风不模糊）

### 6.3 拖拽
- 在精灵 alpha 命中区域按下 → `CaptureMouse()` → `MoveWindow`（或 `Left + Top` 赋值）
- 不需要 Rust 侧 hit-rect 补丁；释放时持久化位置
- 多显示器 DPI：用 `VisualTreeHelper.GetDpi` 处理缩放

### 6.4 截屏 + 变化检测（Phase 5）
- `GraphicsCapturePicker` 或直接 `GraphicsCaptureItem.CreateForMonitor`（Win11 22H2+，无需 picker）
- 帧差检测：缩略图（如 320×180）灰度哈希，变化超过阈值才送模型
- API 成本护栏：默认限频（如最多 1 次/5s 云端调用），本地小模型（Ollama + Qwen2.5-VL）过滤后再决定是否升级

### 6.5 弹幕层（Phase 5）
- 独立全屏透明置顶窗口，`IsHitTestVisible=false`（不挡鼠标）
- Win2D `CanvasControl` 每帧绘制滚动文本；DWrite 文本布局缓存
- 与宠物窗口互不干扰（独立窗口、独立渲染循环）

## 7. 性能验收标准（对照测试）

同机器同场景，新旧版本并排对比：

| 指标 | Tauri 现状（基线） | WPF 目标 |
|---|---|---|
| 空闲 CPU（单宠物，无动画） | 实测记录 | **< 1%** |
| 动画时 CPU | 实测记录 | **< 5%**（60fps 时） |
| 内存（常驻） | 实测记录 | **< 120MB**（WebView2 内核通常 100MB+） |
| 拖拽延迟（鼠标 → 窗口位移） | 实测记录 | **< 16ms**（跟手） |
| 动画帧率 | 实测记录 | **60fps 稳定** |
| 弹幕（100 条滚动） | —（新功能） | **60fps** |
| 启动到宠物可见 | 实测记录 | **< 2s** |

Phase 0 完成时先测基线：写一个 `scripts/perf-bench.ps1`，用 PerformanceCounter 采 CPU/内存，拖拽延迟用 `GetMessageTime` 差值采样。

## 8. 测试策略

| 层 | 方式 |
|---|---|
| 核心逻辑（切片/养成/物理/存储） | xUnit，**切片对照测试**：同一批测试图喂 TS 版与 C# 版，输出 Rect 数组断言一致 |
| UI 行为（拖拽、气泡） | 手工验收清单 + 关键路径集成测试（可选 FlaUI） |
| 性能 | `perf-bench.ps1` 脚本，Phase 0 起每阶段跑一次防回归 |
| AI（Phase 5） | 截屏模块用录制好的屏幕帧序列做离线测试（不依赖真实屏幕） |

## 9. 风险与决策点

| # | 决策点 | 选项 | 建议 |
|---|---|---|---|
| 1 | **弹幕层框架** | WPF + Win2D / WinUI 3 Composition | WPF + Win2D（WinUI 3 的窗口模型和透明支持仍有坑，作为 Phase 5 的备选验证项） |
| 2 | **自动更新** | Squirrel.Windows / Velopack / 自建 | Phase 4 前调研 Velopack（活跃、支持 NSIS 风格）；暂用 Tauri 版现有更新服务过渡 |
| 3 | **托盘实现** | `H.NotifyIcon`（WPF 原生库）/ WinForms NotifyIcon 兼容 | `H.NotifyIcon`（活跃维护） |
| 4 | **双进程时机** | 一开始就拆 / Phase 5 再拆 | 代码按模块隔离，Phase 5 再拆（YAGNI） |
| 5 | **旧版处理** | git 保留 `windows/` / 删除 | 保留在 `windows-tauri/` 分支或 tag，避免误删可对照实现 |
| 6 | **图像解码库** | `System.Drawing.Common` / `ImageSharp` | `ImageSharp`（无 GDI+ 平台警告、纯托管、活跃维护） |
| 7 | **截图权限** | Graphics Capture 首次弹窗 | 引导页说明用途；隐私开关（默认本地处理，云端需显式开启） |
| 8 | **macOS 版并行维护** | 两套代码各自演进 | 核心逻辑（切片/养成/物理）语义对齐，不追求共享代码 |

## 10. 建议的执行顺序

1. **先做 Phase 0 骨架 + 性能基线**（1 周内）：验证 WPF 透明窗口 + 拖拽体验确实达标，再投入全量迁移——这是最低成本的验证点。
2. 同时写 `perf-bench.ps1` 测 Tauri 现状基线，作为后续对照。
3. M1（Phase 0-1）完成即可在 Windows 上替换 Tauri 版做日常使用，边用边迁。

---

*文档维护：随迁移进展更新本计划；每阶段验收通过后勾选里程碑。*
