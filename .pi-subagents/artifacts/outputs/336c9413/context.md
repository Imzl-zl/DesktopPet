# 只读核实交接：I1、I3-I5、I10、I12-I13

## 结论总览

| 项 | 核实结论 | 分类 | 建议严重度 |
|---|---|---|---|
| I1 | 成立，而且“语言设置”是已暴露但无即时/重启后 UI 效果的假开关。`I18nService.T()` 在产品代码零调用，绝大部分 WPF 文案为中文硬编码。 | 已交付功能缺陷 + M3/Phase 4 承诺缺口；全量修复是跨 UI 大功能 | Important |
| I3 | 一半是缺陷、一半是承诺缺口：固定的 H/M/S/Q 与迁移计划一致，但与架构 §7“可自定义、常显设置页”冲突；四次注册返回值均被丢弃，冲突时静默失效成立。 | 注册失败缺陷；可自定义是承诺缺口/中型功能 | Important |
| I4 | “无测试连接”成立，底层 `ListModelsAsync` 已存在但 UI 未接线。“多连接 keyRef 覆盖”也成立于多个 `ApiKeyRef` 为空的配置被编辑并填 Key 时，都会归一到 `model-key`；但当前 UI 只能选择/编辑已有连接，不能新增第二条，所以这是配置兼容场景中的真实缺陷，不是普通 UI 路径必现。 | 测试连接为承诺缺口；keyRef 冲突为凭据完整性缺陷 | Important |
| I5 | 四项均无实现。恢复出厂、日志导出、CPU/内存自采样是正式架构承诺；全屏暂停弹幕/主动互动也是正式承诺。它们不是现有代码回归，应拆成独立功能。 | 承诺缺口；其中恢复出厂和全屏检测是大功能/高决策面 | Important（不可作为一个补丁打包） |
| I10 | 成立。固定管道名、首连接即信任、无 ACL 显式收窄、无客户端 PID/握手认证；未认证连接可发 `Config` 和 `Shutdown`。恶意 Config 的 BaseUrl + ApiKeyRef 会让 Agent 从 Credential Manager 取 Key 并向指定 URL 发 Bearer。 | 安全缺陷 | Important，建议前置 |
| I12 | 成立且范围比报告所列更广：模型、生图 Provider 都各自 `new HttpClient()` 且不实现释放；App 每次任意设置保存都会重建模型 provider，Agent 每次 Config 也会重建，生图连接变更会替换 provider。 | 资源生命周期缺陷 | Important |
| I13 | 成立且最坏阻塞超过报告中的 2s/1s：设置页任意 `Save` 都同步调用 `AiCoordinator.ApplySettings`，其内 `StopAgent` 可同步等待发送 1s、Kill/WaitForExit 2s、RPC Dispose 1s；随后 `RebuildChatPipeline` 再同步等 2s。Provider 保存同样同步重建。 | UI 响应性/生命周期竞态缺陷 | Important |

## 正式文档证据

- `docs/windows-architecture.md:98-103,108-114`：测试连接必须调 `/models`、分类提示错误、连接卡片显示测试按钮/能力徽章；`IModelProvider.ListModelsAsync` 是正式契约。
- `docs/windows-architecture.md:99` 与项目 `AGENTS.md`：API Key 必须在 Windows Credential Manager，JSON 只能存引用 ID。
- `docs/windows-architecture.md:195-197`：`HttpClient` 单例/连接复用；AI/网络不得阻塞 UI；空闲性能基线。
- `docs/windows-architecture.md:204`、`docs/windows-migration-plan.md:157-161`：M3/Phase 4 明确交付设置全量与 4 语言。
- `docs/windows-architecture.md:226-229`：恢复出厂、可自定义且常显的全局快捷键、全屏时暂停弹幕/主动互动、关于页 CPU/内存自采样。
- `docs/windows-architecture.md:236-237`：日志应滚动写 `%APPDATA%/DesktopPet/logs/`，关于页一键导出 zip。
- `docs/windows-migration-plan.md:178`：固定默认快捷键 H/M/S/Q；因此“硬编码本身”不违背迁移计划，违背的是更高层架构对可自定义的追加承诺。
- `docs/windows-ui-design.md:160-164`：语言页、AI 模型连接测试/能力徽章均属于设计范围。

## 精确代码证据与改动面

### I1：语言设置无效

- `windows-native/src/DesktopPet.Core/I18n/I18n.cs:20-59`：服务和 `SetLang/T` 完整存在。
- `windows-native/src/DesktopPet.App/App.xaml.cs:61-66`：启动时创建服务并注入 manager，但仅设置对象，没有 UI 消费者。
- `windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:1363-1379`：语言单选只 `Save(... Lang=value)`；未调用 `_i18n.SetLang`，未重建整个窗口，也未通知托盘/对话/浮球。
- 产品源码搜索 `.T(` 为零；只有 `windows-native/tests/DesktopPet.Core.Tests/I18nTests.cs:14-58` 调用。App 中至少 79 个直接 `Text/Content/Title/Header` 字面赋值点，且这还不含 helper 参数与运行时消息；`SettingsWindow.cs` 本身 2436 行、大量中文硬编码。
- 资源 `windows-native/src/DesktopPet.Core/Resources/i18n.{zh,zh-TW,vi}.json` 各约 360 行，主要来自旧 UI，不能假设覆盖当前新 AI/动作/引导文案。

建议改动面：建立 App 层可观察的本地化入口（不要让 Core 依赖 WPF）；把 `SettingsWindow`、`TrayController`、`ChatWindow`、`FloatingBallWindow`、`WelcomeWindow`、`SpritePreviewWindow`、`BubbleView` 及 App/AiCoordinator 用户文案接到同一服务；语言变化时重建或刷新所有可见 UI。资源应按稳定 key 管理，不能继续以散落中文为源文本。错误码到本地化文案的映射留在 App 层。

测试：扩充词条完整性测试覆盖当前 UI key；App 层加入语言切换后窗口/菜单文案刷新的可测 presenter/view-model 测试；四语言手工 smoke（启动检测、运行时切换、重启持久化、托盘/设置/对话/错误态）。当前仅测服务字典，完全不能证明 UI 生效。

风险：这是跨全部 UI 的大波次，不应在 Important 修复波中顺手替换少量文案形成混合语言。人格内容、用户自定义台词和模型输出不应翻译。

### I3：快捷键

- `windows-native/src/DesktopPet.App/App.xaml.cs:283-305`：透明宿主创建后硬编码注册四组热键，四个 bool 返回值全部丢弃。
- `windows-native/src/DesktopPet.Infra/Hotkey/HotkeyManager.cs:43-85`：底层正确返回失败且失败不留映射，已有重注册能力。
- `windows-native/tests/DesktopPet.Infra.Tests/HotkeyManagerTests.cs:45-106`：覆盖 manager 成败/注销和固定 preset，但未覆盖 App 对失败的反馈、持久化、自定义冲突/回滚。

建议改动面：在 `AppSettings` 增加版本兼容的 `HotkeySettings`（四动作、默认仍为 H/M/S/Q）；Core 放规范化/冲突校验；设置 UI 常显并录制组合键；App 集中 `ApplyHotkeys`。重注册应先验证整组，再以“新组全部成功才替换旧组”的事务语义处理，失败保留旧映射并显示具体动作/组合冲突。不要把 Win32 错误静默掉，可从 `Marshal.GetLastWin32Error()` 带出可诊断码。

测试：Core 序列化/旧配置默认/重复组合；Infra 组合重注册失败不丢旧映射；App handler 的反馈与持久化；真实 Windows 手测冲突（先用其他程序占用组合）、重启、键盘布局。

### I4：模型连接与 Credential

- `windows-native/src/DesktopPet.Core/Scheduling/ModelContracts.cs:98-114`：`ListModelsAsync` 已在接口中。
- `windows-native/src/DesktopPet.Infra/Providers/OpenAiCompatibleModelProvider.cs:137-190`：`GET /models`、auth/timeout/network/http/invalid-response 分类均已有。
- `windows-native/tests/DesktopPet.Infra.Tests/ProviderTests.cs:303-324`：成功解析已有单测，但缺 401/超时/无效 JSON 的 ListModels 分支。
- `windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:2013-2124`：编辑器只有保存按钮，没有测试按钮；Key 用普通 `TextBox`，新输入可见；`ApiKeyRef` 为空时固定取 `model-key`。
- `SettingsWindow.cs:1516-1544` 已能选择多个已有连接；因此两个外部/旧配置连接若 `ApiKeyRef` 都为空，分别编辑填 Key 会覆盖同一 Credential target。UI 当前无“新增第二连接”，降低普通路径可达性但不消除缺陷。
- `windows-native/src/DesktopPet.Infra/Providers/ProviderConfig.cs:54-78`：`CredWrite` 返回值未检查；设置保存可能显示成功但 Key 实际未写入。

建议改动面：连接 ID 创建时唯一且不可随 model 名漂移；缺省 keyRef 用 `model/{connectionId}`（生图也用独立稳定 ID），提供旧 `model-key` 的显式迁移而非静默复制/删除；Credential 接口增加失败可见的结果/异常，必要时增加 Delete。编辑器用 `PasswordBox`，提供异步“测试连接”，以当前未保存表单值构建临时 config/provider，显示模型列表/能力或分类错误；测试成功不应隐式持久化 Key/配置。

安全边界：只允许 `http/https`；明确提示 `http` 会明文传 Key，默认拒绝或需用户确认；错误 UI/日志不得包含 Authorization、完整 Key 或服务响应中的敏感 body。允许 localhost 无 Key。BaseUrl 是用户显式输入的网络目的地，不要悄悄限制合法 Ollama/vLLM，但需阻止非 HTTP scheme。

### I5：四个独立承诺缺口

- `SettingsWindow.cs:1384-1421` 关于页仅版本、宠物数量、XP、数据目录。
- 全仓无 CPU/WorkingSet 自采样、日志 zip、恢复出厂、全屏前台检测实现。
- 日志现状反而在 `%TEMP%`：`DesktopPet.AgentHost/Program.cs:10-45`、`AgentService.cs:178-190`，设置编辑器异常也写 `%TEMP%/desktoppet-ai.log`；所以“导出日志按钮”不能先做成打包不存在的正式 logs 目录。

精确拆分：
1. 日志基础设施 + 导出：`DesktopPet.Infra/Logger`（滚动、脱敏）→ App/AgentHost 注入/初始化 `%APPDATA%/DesktopPet/logs/` → About 导出 zip。先迁日志，再做导出。
2. CPU/内存：App 服务按时间差采 `Process.TotalProcessorTime` 和系统 CPU 数，显示 App 与 Agent 分项/合计；窗口关闭即停 timer。不要把即时 `WorkingSet64` 标成长期平均。
3. 恢复出厂：需要 App 编排 Agent/窗口关闭、删除数据和凭据、重建默认 store，再重启。`IJsonStore` 当前没有 reset/delete API，`ICredentialStore` 没有 Delete，不能由 `SettingsWindow` 直接递归删目录。
4. 全屏暂停：App 层新增前台全屏检测服务，Mode/Interaction 消费暂停状态；要明确“只暂停弹幕和主动互动”还是连截屏分析一起暂停。正式架构文字只承诺前两者。

测试：日志滚动/脱敏/zip 内容；采样公式和 timer 生命周期；reset 的保留/删除矩阵及失败恢复；全屏检测对 maximized 普通窗、无边框游戏、多显示器、设置/桌宠自身窗口的判断；手工全屏游戏/PowerPoint 验收。

### I10：PipeRpc 鉴权

- `windows-native/src/DesktopPet.Infra/PipeRpc/PipeRpc.cs:76-84`：固定名字构造 `NamedPipeServerStream`，无 `PipeOptions.CurrentUserOnly`、显式 PipeSecurity、身份参数。
- `PipeRpc.cs:123-126`：客户端仅按名字连接。
- `windows-native/src/DesktopPet.Agent/AgentService.cs:46-100`：首个连接立即收发；未要求 client Hello；任何连接都可发 Config/Ping/Shutdown。
- `AgentService.cs:127-157`：Config 的 BaseUrl、ApiKeyRef 直接构造 provider；provider 会从 Credential Manager 取 Key并发 Bearer 请求。
- `windows-native/src/DesktopPet.AgentHost/Program.cs:63-82` 与 `AiCoordinator.cs:288-300,378-389`：宿主使用固定 `DesktopPet.Agent`，启动参数没有随机 pipe/预期父 PID。
- 现有 `PipeRpcTests` 仅验证帧/断连；`AgentServiceTests` 明确证明任意 client 可 Config 和 Shutdown。

建议安全基线：每次 Agent 启动生成随机 pipe 名；创建管道时至少 `PipeOptions.CurrentUserOnly`；App 启动 Agent 时传随机名和预期 App PID，Agent 在接受连接后用 Windows `GetNamedPipeClientProcessId` 校验实际 client PID，不匹配即断开且不得处理 Config/Shutdown。随机秘密不要写日志、配置或异常。仅 nonce 握手不足以对抗同用户进程读取命令行；PID 绑定更直接。若产品威胁模型只要求跨用户隔离，CurrentUserOnly 即可，但无法解决同用户首连接抢占/Shutdown。

测试：错误 PID/未认证连接不能 Config、不能 Shutdown、不能触发 Credential `Get`/HTTP；正确 App PID 正常握手；固定名字不再出现；ACL 测试需 Windows 条件测试。保留 64MB 帧上限不是认证。

重要边界：同一用户的完全恶意进程通常也能直接调用 CredRead 读取该用户 Generic Credential，因此不能宣称此改动构成对“同用户完全失陷”的密钥机密性隔离；它主要建立 Agent IPC 完整性、抵抗误连/低权限或其他用户进程，并阻止未授权控制/停机。报告中“外带凭据”链路真实，但威胁表述需保持这个限制。

### I12 + I13：Provider/异步生命周期（应同波修）

- `OpenAiCompatibleModelProvider.cs:25-34`、`OpenAiCompatibleImageProvider.cs:19-28`：每实例 new HttpClient，两个 provider 都不实现 Dispose。
- `AiCoordinator.cs:113-139`：每次设置变化均无条件 `RebuildChatPipeline`，即使只改主题/字号。
- `AiCoordinator.cs:171-177,467-478,750-760`：Provider 变更替换 image provider，并同步销毁 scheduler/新建 model provider；旧 provider/HttpClient 无释放。
- `AgentService.cs:127-162`：每个 Config 都新建模型 provider/AnalysisEngine；旧 provider 也无生命周期收口。
- `SettingsWindow.cs:1882-1910`：所有 UI Save 在 UI 线程同步调用 `_ai.ApplySettings`。
- `AiCoordinator.cs:306-349,470,771-773`：同步 `.Wait` 分布在 Shutdown send、进程退出、RPC dispose、scheduler dispose；总卡顿上界可叠加，不只是单个 1-2 秒。
- 当前 diff 的 C3 新增 `_chatSerial` 并让 `SendChatAsync` 持有它执行管道/记账，但 rebuild 不经该闸；设置变更可在在飞对话期间取消/替换 scheduler，字段生命周期仍有竞态。`ModelRequestScheduler.DisposeAsync` 也不会显式完成仍在队列中的 Job，需纳入取消语义检查。

建议结构：App/AgentHost 各自持有长生命周期共享 `HttpClient`（或清晰的工厂/handler 池），Provider 不拥有共享 client，timeout 用每请求 linked CTS，不能反复改共享 `HttpClient.Timeout`；测试 handler 构造保留。AiCoordinator 引入单一异步生命周期闸，`ApplySettingsAsync/ApplyProvidersAsync/DisposeAsync` 先原子交换新 pipeline，再异步 drain/dispose 旧 scheduler；只在 AI 相关字段实际变化时重建。StopAgent 全异步，先发送有界 Shutdown，再等待/kill，绝不在 Dispatcher 上 Wait。Agent Rebuild 也需取消并 await 旧 engine 后再释放 provider/资源，避免旧 task 与新配置重叠。

测试：注入 tracking HttpMessageHandler/HttpClient，重复 100 次 settings/config 不增长 handler/client；在飞对话时 ApplySettings/ApplyProviders 不死锁、不悬挂、结果语义明确；UI 调用立即 yield；StopAgent 超时后 kill 路径；scheduler dispose 令队列/在飞任务全部完成为取消。手工用 `netstat`/性能计数观察多次保存后连接数稳定。

## 当前 diff 约束

- 工作树已有用户改动：`AiCoordinator.cs`（C3/C5 相关）、`App.xaml.cs` + `SettingsWindow.cs` + `PetWindowManager.cs`（C6）、`ChatWindow.cs`、`SpritePreviewWindow.cs`、Core scheduler/danmaku/frame/sprite 与 scheduler tests。`docs/windows-code-review-2026-08.md` 本身未跟踪；无 staged 文件。
- 本次核实未修改任何项目文件；仅写本交接 artifact。
- 后续实现必须保留 C3 的对话串行/选中宠物记账、C5 会话 TTS、C6 设置窗刷新，尤其不要用旧版本覆盖 `AiCoordinator/App/SettingsWindow`。
- 当前 diff 没有修复本任务任一 I 项；反而 I13 的精确行号因 C3 diff 后移到 `AiCoordinator.cs:470` 等。

## 需用户决策

1. I1 切换语言是否要求立即刷新全部已打开窗口，还是允许提示重启。建议立即刷新；否则“语言设置”仍显得失效。
2. I3 哪些动作允许自定义，尤其“退出”是否可禁用/改绑；允许的修饰键集合及冲突时是拒绝整组还是仅拒绝单项。建议四项可改、不可留空的 Quit 可例外由用户确认，整组事务提交。
3. I4 测试连接能否向用户填写的任意 HTTP endpoint 发请求；是否默认禁止带 Key 的明文 `http`（localhost 可例外）。建议带 Key 时仅 HTTPS，loopback 明确例外。
4. I5 恢复出厂的删除范围：是否删除宠物导入资源、日记、画像/亲密度、日志，以及是否删除 Credential Manager 中所有 DesktopPet 引用；操作后自动重启还是就地重建。必须先定删除矩阵。
5. I5 全屏时是否继续截屏/分析。文档只承诺暂停弹幕和主动互动；建议继续分析但不输出，若隐私预期是全停则会改变 Agent 配置/成本语义。
6. I10 威胁模型：只隔离其他 Windows 用户，还是必须阻止同用户其他进程控制 Agent。建议按后者做随机 pipe + CurrentUserOnly + client PID 绑定，但不要承诺在同用户完全失陷下保护 Credential 机密性。

## 建议串行子波次

1. **决策冻结**：先回答上述 6 项，并把 I5 reset/fullscreen 验收口径写清。
2. **2A I12+I13 生命周期波**：共享 HTTP transport、异步 Coordinator/Agent pipeline 交换、消除 UI `.Wait`。这是 I4 测试连接和后续设置工作的基础。
3. **2B I10 安全波**：随机 pipe、CurrentUserOnly、client PID 绑定、未认证负向测试。
4. **2C I4 Provider UX/凭据波**：唯一 keyRef/迁移、PasswordBox、异步测试连接、错误分类与脱敏。
5. **2D I3 Hotkey 波**：schema/归一化、设置 UI、事务重注册、失败反馈。
6. **2E I1 i18n 大波**：先建立 App 本地化刷新机制，再按 Settings→Tray/FloatingBall→Chat/Welcome/Preview 分串行小批迁移；每批保持四语言完整，禁止半页混语。
7. **2F I5 diagnostics 波**：正式 logger/滚动/脱敏 → 日志导出 → CPU/内存采样。
8. **2G I5 reset 波**：按已批准删除矩阵实现凭据/文件/窗口/Agent 的可恢复重置。
9. **2H I5 fullscreen 波**：检测服务、多屏与误判测试、Mode/Interaction（以及用户决定后的 Agent）接线。

每个子波次完成后先跑定向项目测试，再跑 `dotnet build windows-native/DesktopPet.sln -p:Platform=x64`；I1/I3/I4/I5 还需真实 WPF 手工/UI 自动化验收。

## 下一代理契约

**Goal**：按上述串行波次制定并实施修复，不把缺陷、文档承诺和大功能混成单次大改；每波有独立测试与安全验收。

**Hard constraints**：Core 保持零 UI/IO；Key 不落 JSON/日志；AI 关闭仍无 Agent/截屏/网络；不得覆盖当前工作树 C3-C6 改动；安全认证失败不得读取 Credential 或应用 Config/Shutdown；UI Dispatcher 不得同步等待网络/进程/RPC/scheduler。

**Success criteria**：语言真实影响全部 UI；快捷键可见/可改且失败可见；测试连接异步、分类、脱敏且 keyRef 不碰撞；I5 四项按用户批准边界完成；Pipe 只接受预期 App；重复设置/配置不增长 HttpClient/socket，设置保存不卡 UI；新增回归测试覆盖负向和生命周期路径。

**Stop/escalation**：恢复出厂删除矩阵、全屏是否停分析、明文 HTTP policy、同用户威胁模型未获批准前，不实施对应破坏性/安全语义；其余已有足够证据可停止重新摸排并进入计划。

## 验证记录与残余风险

- `dotnet build windows-native/DesktopPet.sln --no-restore -p:Platform=x64`：通过，0 warning / 0 error。
- 串行测试：Core 340、Infra 51、Agent 20、App 4 全通过。
- 初次并行启动四个 test 项目时，三个因共享 `DesktopPet.Core/obj/...dll` 被并发编译锁定失败；随后串行重跑全部通过。这是验证命令并行方式问题，不是产品测试失败。
- `git diff --check`：通过。
- 残余风险：现有测试均未覆盖 UI 本地化接线、App 热键注册反馈、Pipe 身份认证、HttpClient 生命周期、Dispatcher 卡顿、恢复/诊断/全屏行为，因此绿测不能推翻上述 findings。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "已逐项核实 I1、I3-I5、I10、I12-I13，给出 docs/windows-architecture.md、windows-migration-plan.md 以及 SettingsWindow/App/AiCoordinator/I18n/Hotkey/PipeRpc/Provider/Credential/测试的精确文件与行号、分类和严重度。"
    }
  ],
  "changedFiles": [
    ".pi-subagents/artifacts/outputs/336c9413/context.md"
  ],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "dotnet test windows-native/tests/DesktopPet.App.Tests/DesktopPet.App.Tests.csproj --no-restore -p:Platform=x64",
      "result": "passed",
      "summary": "4/4 passed"
    },
    {
      "command": "dotnet test windows-native/tests/DesktopPet.Core.Tests/DesktopPet.Core.Tests.csproj --no-restore -p:Platform=x64 && dotnet test windows-native/tests/DesktopPet.Infra.Tests/DesktopPet.Infra.Tests.csproj --no-restore -p:Platform=x64 && dotnet test windows-native/tests/DesktopPet.Agent.Tests/DesktopPet.Agent.Tests.csproj --no-restore -p:Platform=x64",
      "result": "passed",
      "summary": "Core 340/340, Infra 51/51, Agent 20/20 passed"
    },
    {
      "command": "dotnet build windows-native/DesktopPet.sln --no-restore -p:Platform=x64",
      "result": "passed",
      "summary": "solution build passed with 0 warnings and 0 errors"
    },
    {
      "command": "git diff --check",
      "result": "passed",
      "summary": "no whitespace errors"
    },
    {
      "command": "parallel dotnet test of Core/Infra/Agent/App projects",
      "result": "failed",
      "summary": "three projects collided on shared DesktopPet.Core obj output lock; all were rerun serially and passed"
    }
  ],
  "validationOutput": [
    "Current product source has zero I18nService.T calls; only I18nTests call T().",
    "All 415 tests passed after serial rerun: Core 340, Infra 51, Agent 20, App 4.",
    "Current solution builds successfully; current diff does not fix the reviewed I items."
  ],
  "residualRisks": [
    "No current automated coverage proves runtime UI localization, hotkey registration feedback/customization, Pipe client identity, HttpClient/socket stability, UI non-blocking lifecycle, reset scope, diagnostics export, or fullscreen pause.",
    "I10 cannot promise Credential confidentiality against a fully malicious process already running as the same Windows user; proposed controls primarily protect IPC integrity and cross-user/unexpected-client access.",
    "I5 reset and fullscreen behavior require explicit user decisions before implementation."
  ],
  "noStagedFiles": true,
  "diffSummary": "Read-only review; no project source or docs modified. Existing unstaged C1-C6-related work was inspected and preserved; only this required context artifact was written.",
  "reviewFindings": [
    "important: windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:1363 - language radio only persists Lang; product code never calls I18nService.T, so exposed language setting has no UI effect",
    "important: windows-native/src/DesktopPet.App/App.xaml.cs:299 - four hardcoded hotkey registrations discard failure results; customization promised by architecture is absent",
    "important: windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:2092 - empty model credential references collapse to model-key; test-connection UI is absent despite existing provider API",
    "important: windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:1384 - About page lacks reset, log export, CPU/memory sampling; repository also lacks fullscreen pause",
    "important security: windows-native/src/DesktopPet.Infra/PipeRpc/PipeRpc.cs:76 - fixed, unauthenticated first-client pipe accepts Config/Shutdown and can redirect credential-bearing provider requests",
    "important: windows-native/src/DesktopPet.Infra/Providers/OpenAiCompatibleModelProvider.cs:33 - per-provider HttpClient is never disposed/reused; same issue exists for image provider and Agent config rebuild",
    "important: windows-native/src/DesktopPet.App/Ai/AiCoordinator.cs:306 - settings changes synchronously wait on RPC/process/scheduler lifecycle on the UI thread, with cumulative multi-second stalls and in-flight pipeline races"
  ],
  "manualNotes": "Formal-doc commitments, defects, and large feature gaps are separated above, with exact modification surfaces, security boundaries, decision gates, and serial subwaves."
}
```
