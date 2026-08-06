# Code Context

## Files Retrieved
1. `docs/windows-code-review-2026-08.md` (lines 23-42) - I2、I6-I9、I11 的原始审查结论与严重级别（Important）。
2. `windows-native/src/DesktopPet.Agent/AgentService.cs` (lines 16-219) - Agent 管道、配置、AnalysisEngine 重建、Ping/Pong、退出和释放生命周期。
3. `windows-native/src/DesktopPet.Agent/Analysis/AnalysisEngine.cs` (lines 14-155) - 固定截屏循环、模型节流及异常吞噬路径。
4. `windows-native/src/DesktopPet.Agent/Capture/GraphicsCaptureSource.cs` (lines 16-203) - D3D/WinRT 对象创建、帧事件与释放路径。
5. `windows-native/src/DesktopPet.Agent/Capture/SwitchableScreenCaptureSource.cs` (lines 6-89) - 分析开关控制真实捕获源创建/销毁的外层生命周期。
6. `windows-native/src/DesktopPet.AgentHost/Program.cs` (lines 15-34, 63-91) - GraphicsCaptureSource 的 STA 创建和 AgentService 宿主生命周期。
7. `windows-native/src/DesktopPet.App/Ai/AiCoordinator.cs` (lines 274-410) - App 启停 Agent、管道接收循环与看门狗；I11 的 App 侧缺口。
8. `windows-native/src/DesktopPet.App/Windows/PetWindow.cs` (lines 572-660, 1026-1032) - 原生拖拽状态机、WndProc hook 与关闭清理。
9. `windows-native/src/DesktopPet.App/Windows/DanmakuWindow.cs` (lines 23-144) - Win2D 控件的 Paused、Update/Draw 与关闭生命周期。
10. `windows-native/src/DesktopPet.App/Ai/ModeService.cs` (lines 21-89) - DanmakuWindow 创建、路由、模式切换关闭。
11. `windows-native/src/DesktopPet.Core/Danmaku/DanmakuEngine.cs` (lines 24-119) - 活动弹幕快照、Tick 出屏回收；暂停判据来源。
12. `windows-native/src/DesktopPet.Core/Ai/AgentConfig.cs` (lines 7-23) 与 `AgentConfigBuilder.cs` (lines 12-27) - 3-30 秒字段当前明确命名为模型分析限频。
13. `windows-native/tests/DesktopPet.Agent.Tests/AnalysisEngineTests.cs` (lines 12-180) - 现有捕获/变化/节流测试。
14. `windows-native/tests/DesktopPet.Agent.Tests/AgentServiceTests.cs` (lines 11-201) - 现有配置、被动 Ping/Pong、断连、Shutdown、释放测试。
15. `windows-native/tests/DesktopPet.Agent.Tests/SwitchableScreenCaptureSourceTests.cs` (lines 5-43) - 按开关延迟创建和释放捕获源测试。
16. `windows-native/tests/DesktopPet.App.Tests/`（仅 3 个测试文件，无 PetWindow/DanmakuWindow 生命周期测试）- UI 生命周期测试盲区。

## Key Code

### I2 - Important：设置语义与捕获成本脱钩，结论成立

**根因**：`AgentConfig.MinAnalysisIntervalSeconds` 在 `AgentConfigBuilder` 被 3-30 秒钳制，但 `AnalysisEngine` 构造时 `_captureInterval` 默认固定 1 秒（`AnalysisEngine.cs:26-36`）；每轮先执行 `_capture.CaptureAsync` 和哈希（`:42-54`），仅屏幕变化后才由 `AnalysisThrottle` 限制模型调用（`:56-60, 147-154`）。`AgentService` 又固定传入自身默认 1 秒的 `_captureInterval`（`AgentService.cs:32-41, 159`）。真实源每个 FrameArrived 都先复制全分辨率 GPU surface 到 `SoftwareBitmap`（`GraphicsCaptureSource.cs:78-90`），之后消费时才缩到 320x180（`:95-123`），所以设置无法降低 1fps 全尺寸复制成本。

**不变量**：用户可见的“截屏间隔”必须控制昂贵捕获/复制的最大频率；模型分析频率若需独立，应使用独立字段和标签。关闭分析仍必须零捕获（现有 SwitchableSource 已满足）。

**精确修复点**：
- `DesktopPet.Core/Ai/AgentConfig.cs:7-23`、`AgentConfigBuilder.cs:12-27`：明确契约。推荐拆成 `CaptureIntervalSeconds` 与 `MinAnalysisIntervalSeconds`，避免一个值承担两种语义；若产品只保留一个设置，则该值同时驱动 capture cadence 和模型下限。
- `AgentService.cs:32-41, 159` / `AnalysisEngine.cs:26-36, 65-91`：循环延迟从最新配置动态读取，不应仅构造时快照；配置变化后不必为间隔重建引擎。
- `GraphicsCaptureSource.cs:78-90`：进一步降成本时，在 FrameArrived 入口按单调时钟丢弃过早帧，避免对每个系统帧执行 `CreateCopyFromSurfaceAsync`。仅把 `AnalysisEngine` 的消费 Delay 改慢，会减少消费，但当前 `_latest` 仍由 FrameArrived 高频全尺寸复制，不能完整修复描述中的成本问题。

**验证**：给可注入时钟/捕获源增加测试：3 秒窗口内 `CaptureCount` 不超过 1；运行时把 3 改 30 后立即采用新节奏；静态屏幕也遵循截屏间隔；关闭仍为 0。真实 Windows 4K 冒烟用 ETW/计数器验证 `CreateCopyFromSurfaceAsync` 频率，而不只验证模型 `CallCount`。现有 `Tick_Throttle_LimitsAnalysisFrequency` 只证明模型限频，无法覆盖该缺陷。

### I6 - Important：ABI 引用泄漏，结论成立

**根因**：`CreateDirect3D11DeviceFromDXGIDevice` 返回拥有引用的 `graphicsDevice`；`WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice)` 会为投影对象获取自己的引用，但 `GraphicsCaptureSource.cs:183-187` 没有在 `finally` 中 `Marshal.Release(graphicsDevice)`。当前只释放 `dxgiDevice`（`:189`）。每次分析开关重新启用都会由 `SwitchableScreenCaptureSource` 新建真实源，泄漏会累积。

**不变量**：每个 native API 返回的 owning COM pointer 必须恰好释放一次；投影对象持有的引用由其 `Dispose` 管理，原始 ABI 引用由调用者平衡，包括 `FromAbi` 抛异常路径。

**精确修复点**：`GraphicsCaptureSource.CreateD3DDevice()` 的 `graphicsDevice` 创建成功后包在内层 `try/finally`：`return FromAbi(...)`，finally `Marshal.Release(graphicsDevice)`。同时检查 `Marshal.QueryInterface` HRESULT；失败时不要对零指针调用后续 API。构造器部分失败也应局部释放已创建 `_device/_framePool/_session`，否则 I7 的重建路径会扩大泄漏。

**验证**：把 ABI 所有权转换抽成可测 helper 或用可注入 release delegate 做“成功/FromAbi 异常均 release 一次”单测；Windows 集成测试循环 enable/disable 100 次，观察进程 handle/private bytes 和 D3D debug layer live objects 不线性增长。现有测试仅验证外层 `IDisposable.Dispose` 被调用，不能证明 COM 引用平衡。

### I7 - Important：捕获终止后无恢复，结论成立

**根因**：源只订阅 `FrameArrived`（`GraphicsCaptureSource.cs:45-50`）。`GraphicsCaptureItem` 仅为构造器局部变量，未保存、未订阅 `Closed`；没有任何 device-lost/捕获故障状态。`OnFrameArrived` 广泛 catch 后静默（`:78-94`），`CaptureAsync` 只会持续返回 null；`AnalysisEngine.RunAsync` 又吞掉所有非取消异常（`AnalysisEngine.cs:70-83`），因此 GPU/显示拓扑故障不会冒泡到 `SwitchableScreenCaptureSource` 或 Agent 看门狗，捕获永久静默死亡。

**不变量**：启用分析时，捕获源必须处于 Running 或显式 Faulted/Recreating 状态；Closed/device-lost 后应停止旧 session、释放全部旧资源并有界重建，不能无限返回 null 伪装正常。重建必须留在创建源的 STA Dispatcher 上。

**精确修复点**：
- `GraphicsCaptureSource.cs:27-50`：持有 `_item`，订阅 `_item.Closed`；把设备/session/framePool 创建与 teardown 收敛为同一状态机。设备丢失检测应基于所用 D3D/WinRT API 的可用通知或捕获异常 HRESULT 分类，统一进入 Faulted/Recreate，不要继续吞掉致命异常。
- `GraphicsCaptureSource.cs:78-94, 194-203`：区分单帧可恢复错误与终止错误；Closed/DeviceLost 只触发一次重建，Dispose 与回调用同一锁/状态防止重建已释放对象；退订所有事件后再 Dispose。
- `Program.cs:15-34` / `SwitchableScreenCaptureSource.cs:48-58`：重建若放在外层，factory 必须通过 Dispatcher 调用；当前 factory 捕获 dispatcher，适合提供替换源，但需让 Fault 明确传播。

**验证**：抽象 capture session/device 适配器，测试 Closed 后旧事件退订、资源各释放一次、只重建一次、恢复后重新出帧；Dispose 与 Closed 并发不重建；连续重建失败采用有界退避并上报。再做 Windows 手工验收：锁屏/解锁、显示器拔插、显卡驱动重启后恢复。当前没有 GraphicsCaptureSource 测试。

### I8 - Important：鼠标捕获丢失后拖拽状态卡死，结论成立

**根因**：`PetWindow.WndProcHook` 仅处理 `WM_LBUTTONDOWN/MOUSEMOVE/LBUTTONUP`（`PetWindow.cs:572-594`）。按下即 `_pressed=true` 并 `CaptureMouse()`（`:603-618`），唯一清零路径是收到 `WM_LBUTTONUP` 的 `OnRawLeftUp()`（`:645-660`）。捕获被系统/其他窗口抢走时会收到 `WM_CAPTURECHANGED` 或 `WM_CANCELMODE` 而非可靠的 LBUTTONUP，状态因此永久保持；后续 down 被 `if (_dragging || _pressed) return` 拒绝。

**不变量**：任何 drag terminal event（button-up、capture-changed、cancel-mode、窗口关闭）都必须让 `_pressed=false`、`_dragging=false`、清除 drag 动画优先级并通知 roam 引擎结束/取消；取消路径不能当作点击，也不应持久化一次未完成拖拽。

**精确修复点**：`PetWindow.cs:572-594` 加 `WM_CAPTURECHANGED (0x0215)`、`WM_CANCELMODE (0x001F)`；抽出 `EndRawInteraction(commitDrag: bool)`。正常 LBUTTONUP 保留点击/FinishManualDrag/保存位置语义；取消路径清理状态、`ApplyDragRow(false)`，并调用 RoamEngine 新增或已有的明确 cancel API（不要用会产生抛掷/保存的正常 finish，除非领域契约确认）。`OnClosed:1026-1032` 也调用取消清理后再摘 hook。

**验证**：最好把拖拽状态机抽为纯逻辑类，覆盖 down->captureChanged、down->drag->cancelMode、取消后下一次 down 可开始、取消不触发 click/drag-finished、关闭幂等。另做 Win32 消息注入 smoke。当前 App.Tests 无 PetWindow 生命周期测试。

### I9 - Important：暂停恢复不完整且 Win2D 资源未显式释放，结论成立

**根因**：Canvas 初始 `Paused=true`，入队时改 false（`DanmakuWindow.cs:103-135`），但 Update 只 Tick，从不在 `_engine.Active.Count == 0` 后恢复 Paused（`:110-113`），最后一条出屏后仍持续 60fps。关闭只停止 timer、`DetachAndDispose` island 并把 `_canvas=null`（`:138-144`），未退订 Update/Draw、未显式 `CanvasAnimatedControl.Dispose()`，`CanvasTextFormat` 也从未 Dispose。匿名事件处理器还使可控退订困难。

**不变量**：活动集合从 0->1 时启动 loop，从 1->0 时暂停；关闭后 timer、Win2D loop、事件订阅、Canvas 控件、文本格式和 island 全部恰好释放一次，任何迟到回调不得访问已关闭资源。

**精确修复点**：
- `DanmakuWindow.cs:103-126`：将 Update/Draw 改为命名 handler；Update Tick 后读取一次 Active 快照/新增 `ActiveCount`，为空时通过控件线程安全方式设 `Paused=true`。避免每帧为判断和绘制各建一个数组快照。
- `DanmakuWindow.cs:138-144`：先 pause，退订 Update/Draw，`_canvas.RemoveFromVisualTree()`（按 Win2D 控件契约需要时）、`_canvas.Dispose()`，再 dispose `_textFormat`，最后 Detach island；加 `_closed` 幂等门。
- `ModeService.cs:55-65, 82-89`：现有模式切换确实调用 Close，入口无需改，但测试要证明 Close 后无渲染回调。

**验证**：将 canvas 包在适配器后测试首次 enqueue unpause、最后 item 出屏 pause、关闭时 pause/unsubscribe/dispose 各一次；模式反复切换不残留实例。Windows UI smoke 采样空闲 5 秒后 `Fps=0`/CPU 回落，并用诊断工具验证窗口关闭后无 Win2D render loop。当前无 DanmakuWindow 测试，Core 的 DanmakuEngine 测试无法覆盖 UI 资源生命周期。

### I11 - Important：Ping/Pong 是被动死代码，App 挂起时 Agent 无租约，结论成立

**根因**：Agent 只在收到 `RpcType.Ping` 时回 Pong（`AgentService.cs:88-99`），没有“最后心跳时间”和超时任务。App 的 `ConnectAndRunAsync` 握手、PushConfig 后永久等待 `rpc.ReceiveAsync`，仅处理 ScreenEvent（`AiCoordinator.cs:376-410`），从不发 Ping。正常管道断开会让 Agent `_shutdown.Cancel()`（`AgentService.cs:74-86`），但 App 线程/进程挂起且管道仍开时不会断开，AnalysisEngine 继续截屏和调用模型。

**不变量**：只有持有有效 App 租约时 Agent 才允许分析。App 按固定周期发送 Ping；Agent 每次收到 Ping 更新 deadline 并回 Pong；超过 N 个周期未收到 Ping 后必须先禁用/取消 capture+engine，再关闭服务/进程。初始连接也必须有首个心跳截止时间，且心跳不能被 ScreenEvent 接收循环阻塞。

**精确修复点**：
- `AiCoordinator.cs:376-410`：连接成功后启动独立 heartbeat send loop（使用同一 PipeRpcClient 的写锁能力），周期发送 Ping；接收循环消费 Pong 并更新 App 侧健康状态。两个 loop 用 linked CTS，任一失败取消另一方并走现有清理/看门狗。
- `AgentService.cs:45-105`：建立连接时启动 watchdog；Ping 到达原子更新时间/重置租约；超时调用统一 shutdown 路径，并在退出前 `SetEnabled(false)`、取消并 await engine。不要依赖只有 `DisposeAsync` 才停止 engine，因为 `RunAsync` 当前返回时并不自动取消 `_engineCts`。
- `AgentService.cs:133-162, 201-219`：收敛 `StopEngineAsync`，配置重建、租约过期和 Dispose 都先 cancel、再 await 旧 `_engineTask`、再 dispose CTS；当前 Rebuild 取消后立即 Dispose 且覆盖 `_engineTask`，未观察旧任务，也是生命周期残余风险。

**验证**：使用短周期注入的 heartbeat policy：持续 Ping 时服务/捕获继续；停止 Ping 后在 deadline 内 RunAsync 返回、捕获源禁用且 engine 停止；只有 ScreenEvent 没有 Ping 仍超时；Pong 丢失让 App 触发断连/重启；Shutdown 与超时竞态幂等。现有 `EndToEnd_AnalysisDisabled_NoEvents` 只手工发一次 Ping 并断言 Pong，不是心跳测试。

## Architecture

App 的 `AiCoordinator` 启动 PetAgent、连接命名管道并下发 `AgentConfig`；AgentHost 在 STA Dispatcher 上延迟创建 `SwitchableScreenCaptureSource` 内的 `GraphicsCaptureSource`。`AgentService` 每次 Config 都取消并重建 `AnalysisEngine`，引擎按固定 1 秒消费 capture，变化检测后才按 3-30 秒节流模型。真实 GraphicsCaptureSource 自系统 FrameArrived 高频复制全尺寸 surface，缓存最新一帧供引擎缩略化。

PetWindow 和 DanmakuWindow 都是自绘例外层：PetWindow 直接挂 Win32 WndProc 管拖拽，DanmakuWindow 由 ModeService 按需创建并用 Win2D 自驱循环。二者生命周期问题都在 native/渲染资源 terminal state 缺失，不能仅在 Core 测试解决。

## 实施顺序

1. **先 I11**：建立跨进程租约和统一 StopEngine，保证后续捕获故障/成本问题至少能在 App 不健康时终止；先补 AgentService 纯逻辑测试。
2. **再 I6 + I7**：先修 COM 引用平衡，再实现 GraphicsCaptureSource Running/Faulted/Recreating/Disposed 状态机，否则重建会放大泄漏；完成 Windows 真机故障恢复验证。
3. **再 I2**：在稳定捕获状态机上将 cadence 下推到 FrameArrived 前的昂贵复制边界，并澄清 capture/model 两个配置语义。
4. **I8**：独立修复拖拽 terminal state，抽纯逻辑状态机并补 App/Core 测试。
5. **I9**：独立修复 Win2D pause/dispose，最后做模式反复切换、CPU/FPS 和资源残留 UI 验收。
6. 跑 Agent/App/Core 定向测试，再跑 `dotnet build DesktopPet.sln -p:Platform=x64` 和 Windows 手工 smoke。

## Start Here

先打开 `windows-native/src/DesktopPet.Agent/AgentService.cs`。I11 的租约、I2 的引擎节奏、I6/I7 捕获源的拥有关系都由这里编排；先统一 engine 的启动/停止/await 生命周期，能避免后续修复继续叠加未观察任务和重复资源所有权。

## Residual Risks

- `GraphicsCaptureSource` 的具体 device-lost 通知能力取决于当前 CsWinRT 投影/API；实施前需用目标 SDK 类型确认事件，若无直接事件必须基于 HRESULT/Closed 加重建状态机，不能假造通知。
- Win2D `CanvasAnimatedControl` 在 WPF XAML Island 内的准确释放顺序需按当前包版本验证；静态审查可确认未 Dispose，但 UI 线程约束需要真机 smoke。
- 工作树已有大量用户/其他 agent 修改；本次只读审查未区分这些改动的来源，也未修改或回退它们。
- Agent 定向 20 个测试通过只证明当前行为；由于缺少上述故障注入和 UI 生命周期测试，不降低六项 finding 的严重性。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "逐项核实 I2、I6、I7、I8、I9、I11 均为 Important 且结论成立；每项给出根因、不变量、精确文件/行修复点、验证和测试盲区。"
    }
  ],
  "changedFiles": [
    ".pi-subagents/artifacts/outputs/1b49768b/context.md"
  ],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "rg -n 生命周期关键符号 windows-native/src windows-native/tests/DesktopPet.Agent.Tests",
      "result": "passed",
      "summary": "定位 AgentService、AnalysisEngine、GraphicsCaptureSource、PetWindow、DanmakuWindow 及测试引用。"
    },
    {
      "command": "dotnet test windows-native/tests/DesktopPet.Agent.Tests/DesktopPet.Agent.Tests.csproj --no-restore",
      "result": "passed",
      "summary": "20/20 通过，0 失败，0 跳过；现有测试未覆盖本报告所列故障注入和 UI 生命周期。"
    },
    {
      "command": "git status --short",
      "result": "passed",
      "summary": "确认工作树原先已有多项修改/未跟踪文件；本任务未修改源码。"
    }
  ],
  "validationOutput": [
    "DesktopPet.Agent.Tests: passed 20, failed 0, skipped 0, duration 446 ms.",
    "review-findings: I2/I6/I7/I8/I9/I11 均经源码与调用方/测试交叉核实为成立的 Important finding。",
    "residual-risks: GraphicsCapture device-lost API 与 Win2D XAML Island 释放顺序仍需目标 Windows SDK/真机验证。"
  ],
  "residualRisks": [
    "GraphicsCapture 的 device-lost 具体通知需按目标 SDK 投影确认并做真机显示器/GPU 故障验证。",
    "PetWindow 与 DanmakuWindow 当前没有对应 UI 生命周期自动化测试。",
    "现有 Agent 测试只覆盖被动单次 Ping/Pong，不覆盖心跳租约超时。"
  ],
  "noStagedFiles": true,
  "diffSummary": "未修改产品源码或测试；仅写入指定只读审查产物 context.md。",
  "reviewFindings": [
    "important: AnalysisEngine.cs:35-91 / GraphicsCaptureSource.cs:78-90 - I2 设置仅节流模型，未限制固定 1fps 消费及 FrameArrived 全尺寸复制。",
    "important: GraphicsCaptureSource.cs:183-187 - I6 graphicsDevice ABI owning pointer 在 FromAbi 后未 Release。",
    "important: GraphicsCaptureSource.cs:37-94 - I7 未保存/订阅 capture item Closed，致命捕获错误被吞后永久静默。",
    "important: PetWindow.cs:572-660 - I8 缺 WM_CAPTURECHANGED/WM_CANCELMODE terminal path，_pressed 可永久卡住。",
    "important: DanmakuWindow.cs:103-144 - I9 活动项清空后不恢复 Paused，关闭未显式 Dispose canvas/text format。",
    "important: AgentService.cs:45-105 / AiCoordinator.cs:376-410 - I11 仅被动 Ping/Pong，无 App 租约和超时停捕获。"
  ],
  "manualNotes": "只读核实完成；实施顺序建议 I11 -> I6/I7 -> I2 -> I8 -> I9。"
}
```
