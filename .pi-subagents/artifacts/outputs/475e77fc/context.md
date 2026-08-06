# Code Context

## Files Retrieved
1. `docs/windows-code-review-2026-08.md` (lines 18-42) - I2/I6/I7 的原始 Important 级审查结论。
2. `windows-native/src/DesktopPet.Agent/Capture/GraphicsCaptureSource.cs` (lines 16-203) - D3D/WinRT 捕获对象创建、每帧复制、ABI 引用和释放路径。
3. `windows-native/src/DesktopPet.Agent/Capture/SwitchableScreenCaptureSource.cs` (lines 1-90) - 真实捕获源的启停、串行访问与唯一 owner 边界。
4. `windows-native/src/DesktopPet.Agent/Analysis/AnalysisEngine.cs` (lines 14-192) - 固定 1 秒消费循环、变化检测及模型节流。
5. `windows-native/src/DesktopPet.Agent/AgentService.cs` (lines 16-73, 217-310, 329-390) - 配置应用、引擎重建、capture 激活/停用及最终释放。
6. `windows-native/src/DesktopPet.Core/Ai/AgentConfig.cs` (lines 6-25) - IPC 配置只定义 `MinAnalysisIntervalSeconds`，注释语义是云端分析限频。
7. `windows-native/src/DesktopPet.Core/Ai/AgentConfigBuilder.cs` (lines 10-32) - UI 的 `ScreenAnalysisIntervalSeconds` 被映射为模型最小分析间隔并钳制到 3-30 秒。
8. `windows-native/src/DesktopPet.AgentHost/Program.cs` (lines 15-39, 68-113) - STA Dispatcher 创建真实源并提供 FrameArrived 消息泵；外层 switchable/service 生命周期。
9. `windows-native/tests/DesktopPet.Agent.Tests/AnalysisEngineTests.cs` (lines 7-235) - 覆盖禁用零捕获、变化、模型 deadline 和模型 throttle，但不覆盖生产端复制 cadence。
10. `windows-native/tests/DesktopPet.Agent.Tests/SwitchableScreenCaptureSourceTests.cs` (lines 1-39) - 仅覆盖延迟创建和 disable 时 Dispose 一次。
11. `windows-native/tests/DesktopPet.Agent.Tests/AgentServiceTests.cs` (lines 75-103, 199-261, 274-323, 372-400) - 覆盖 heartbeat/config/disconnect 停捕获与引擎不重叠；没有故障重建或配置 cadence 测试。

## Key Code

### I2 - Important: capture cadence 与配置语义脱钩

根因链：

```csharp
// AgentConfigBuilder.cs:30
MinAnalysisIntervalSeconds: Math.Clamp(settings.Ai.ScreenAnalysisIntervalSeconds, 3, 30)

// AnalysisEngine.cs:37, 50, 63-64, 89
_captureInterval = captureInterval ?? TimeSpan.FromSeconds(1);
var frame = await _capture.CaptureAsync(ct);
SyncThrottle(cfg);
if (!_throttle.TryTake(now)) return null;
await Task.Delay(_captureInterval, ct);

// GraphicsCaptureSource.cs:82-89
using var frame = sender.TryGetNextFrame();
var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)...;
_latest?.Dispose();
_latest = bitmap;
```

`ScreenAnalysisIntervalSeconds` 实际只进入模型调用 throttle。`AgentService.cs:31,69,292` 仍把独立的默认 1 秒传给引擎。更严重的是，即使引擎每 1 秒只消费一次，`FrameArrived` 仍会按桌面合成帧到达频率执行全分辨率 `CreateCopyFromSurfaceAsync`，不断替换 `_latest`；4K BGRA 单帧约 31.6 MiB（若计读写流量约 63 MiB），不是“每秒仅一次复制”。因此仅改 `Task.Delay` 不能完整修复 I2。

最小结构性方案：

1. 明确一个配置契约。若产品只有当前一个“屏幕分析间隔”设置，建议 IPC 字段改为 `CaptureIntervalSeconds`，它控制昂贵捕获/复制的最大频率；模型最小间隔可同值派生。若确实需要独立节奏，则显式增加 `CaptureIntervalSeconds` 和 `MinAnalysisIntervalSeconds` 两字段，UI 分别命名，禁止一个字段暗含两种语义。
2. cadence 必须下推到 `GraphicsCaptureSource.OnFrameArrived` 的昂贵复制边界。源接收动态 cadence provider 或 `SetCaptureInterval`，用 `TimeProvider.GetTimestamp()`/单调时钟在调用 `CreateCopyFromSurfaceAsync` 前拒绝过早帧；不要用 `DateTime.Now`。
3. `AnalysisEngine.RunAsync` 的 delay 同样从最新配置读取，避免空轮询；但它只是消费 cadence，不能替代 producer gate。配置更新时现有 `AgentService` 会重建引擎，因此最小实现可构造快照；更干净的实现是读取动态配置，间隔变化无需重建纯分析状态。
4. 保持 `ScreenAnalysis=false` 的现有不变量：`AgentService` 先停止并 await engine，再 `SetEnabled(false)`，真实源立即释放。

可测 seam：抽出纯逻辑 `CaptureCadenceGate(TimeProvider, Func<TimeSpan>)`，API 如 `bool TryAcquire()`。单测 fake time 验证 3 秒内只放行一次、运行时 3→30 秒生效、时钟回拨不影响。给 `GraphicsCaptureSource` 的 surface copier 注入接口/委托，断言 gate 拒绝时 `CopyAsync` 调用为 0；否则只测 `IScreenCaptureSource.CaptureAsync` 次数会漏掉真正的 GPU→CPU 成本。Windows 集成测试用计数/ETW 在静态和动态 4K 桌面验证复制频率。

### I6 - Important: `graphicsDevice` ABI owning reference 未平衡

`GraphicsCaptureSource.cs:184-190` 中 `CreateDirect3D11DeviceFromDXGIDevice` 返回 owning COM pointer；`WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice)` 建立投影对象自己的引用，但原始 `graphicsDevice` 没有 `Marshal.Release`。外层 finally 只释放 `dxgiDevice`。每次 screen analysis disable/enable，`SwitchableScreenCaptureSource` 会销毁并新建真实源，泄漏随切换累积。

最小结构性方案：`graphicsDevice` 初始化为零，native 调用成功后，用内层 `try/finally` 包住 `FromAbi`，finally 中只要非零就 `Marshal.Release(graphicsDevice)`；投影后的 `_device.Dispose()` 仍负责其自身引用。同步检查 `Marshal.QueryInterface` HRESULT，并使构造失败路径释放已经创建的 device/framePool/session，避免 I7 重建放大部分构造泄漏。

可测 seam：把“owning ABI pointer → projected object”的引用平衡抽成内部 helper，注入 projector/release delegate；测试成功和 projector 抛异常时均恰好 release 一次，native API 失败/零指针不 release。真实 Windows 循环 enable/disable 100 次，配合 D3D debug layer live-object report 和 private bytes/handle 趋势验收。

### I7 - Important: capture 终止不可观察，owner 无法重建

`GraphicsCaptureSource.cs:45` 把 `GraphicsCaptureItem` 留在局部变量，没有保存或订阅 `Closed`；对象只订阅 `FrameArrived`（`:48`）。`OnFrameArrived` 的所有异常都被吞（`:78-95`），`CaptureAsync` 的转换异常也转 null（`:64-75`）。随后 `AnalysisEngine.RunAsync` 再吞掉所有非取消异常（`AnalysisEngine.cs:83-86`）。结果是 monitor/capture item 关闭、设备丢失或持续 frame-pool 故障都退化为永久 null，`SwitchableScreenCaptureSource` 和 Host 均看不到 fault，无法重建，进程看门狗也不会触发。

最小结构性方案：

1. `GraphicsCaptureSource` 保存 `_item` 并订阅 `Closed`；建立最小状态 `Running/Faulted/Disposed`，首个 terminal fault 原子记录原因并发出一次通知。单帧可恢复错误可以计数，连续阈值后升级 terminal fault；不要继续无边界吞异常。
2. 生命周期 owner 只能有一个：由 `SwitchableScreenCaptureSource` 订阅 active source 的 fault/closed，在 gate 下 detach 并清空当前实例，在 gate 外 Dispose；下次 `CaptureAsync` 通过已有 factory 创建替代源。不要让 `GraphicsCaptureSource` 自己递归重建 D3D 对象，否则会形成双 owner。
3. fault 需可观察。可扩展为 `IRecoverableScreenCaptureSource`（`event Faulted` 或 `CaptureAsync` 抛专用 `ScreenCaptureUnavailableException`）。`AnalysisEngine` 对该专用异常不可用通用 catch 吞掉；应让 switchable 消费并重试，重试采用有界 backoff，日志包含状态与原因。
4. 所有 WinRT session/framePool/item 的创建、订阅、解除订阅和 Dispose 尽量 marshal 到 AgentHost 的 STA Dispatcher。当前 factory 经 `dispatcher.Invoke` 创建（`Program.cs:18-38,75`），但 disable/Service dispose 可能在线程池 continuation 上执行；引入重建后线程亲和风险更高，建议 owner 注入 dispatcher/executor 统一资源生命周期。

可测 seam：定义小接口 `ICaptureSession : IDisposable`（`FrameArrived`、`Closed/Faulted`）及 factory，由 `GraphicsCaptureSource` 状态机依赖它。测试 Closed 只通知一次、fault 后不再发布 frame、Dispose 解除订阅且幂等。对 switchable 用脚本化 factory：source1 fault 后恰好 Dispose 一次，下一 capture 创建 source2；disable 与 fault 并发不会双 Dispose；factory 连续失败按 fake `TimeProvider` backoff 且可取消。Host 另做 Windows 真机 smoke：显示器拔插/睡眠恢复/禁用显卡后恢复捕获。

## Architecture

`AgentHost.Program` 在 STA WPF Dispatcher 上创建 `SwitchableScreenCaptureSource`，其 factory 再 marshal 到 Dispatcher 创建 `GraphicsCaptureSource`。`AgentService` 是编排 owner：收到配置时串行停止旧 `AnalysisEngine`，切换 capture enabled 状态，再创建新 engine。`AnalysisEngine` 每轮从 switchable 消费最新帧，做 hash/change detection，最后用 `MinAnalysisIntervalSeconds` 节流模型。

建议维持 ownership 链：`AgentService` owns switchable，switchable owns at most one real source，real source owns item/session/framePool/device/latest bitmap。cadence 配置由 service 传到 switchable/real source，但 producer gate 在 real source；terminal fault 向上报告并由 switchable 替换。这样资源释放、重建和 cadence 各只有一个真值。

## Risks

- **线程亲和**：WinRT GraphicsCapture 对象是否全部 agile 不能靠当前测试证明；重建/Dispose 应通过 Dispatcher executor，且不得在 `FrameArrived` 回调内同步等待同一 Dispatcher 导致死锁。
- **回调与 Dispose 竞态**：`OnFrameArrived` 可能正在同步等待 surface copy；Dispose 必须先标记 terminal、解除订阅，再等待/取消 in-flight copy，锁内不能 Dispose 外部 WinRT 对象。
- **最新帧语义**：producer 降频会延迟捕获至 interval 边界；配置变化时必须定义是立即允许一帧还是保留上次 deadline，并用测试固定。
- **恢复风暴**：持续 device lost 下无界立即重建会造成 GPU/日志/CPU 风暴；需有界指数 backoff 和取消。
- **fallback 隐藏故障**：Host 当前构造失败直接返回永久空 `OfflineFrameSource`（`Program.cs:22-37`）。若用于运行期重建，这会把可恢复故障永久固化；应记录明确 unavailable 状态并允许后续重试。
- **测试能力边界**：纯单测可证明状态、ownership、cadence gate 和 release 委托调用，不能证明 CsWinRT ABI AddRef 行为、D3D live objects 或真实 device-lost 事件顺序，仍需目标 Windows SDK/硬件集成验收。

## Start Here

先打开 `windows-native/src/DesktopPet.Agent/Capture/SwitchableScreenCaptureSource.cs`。它应成为唯一的真实源 owner 和恢复边界；先定义 fault/recreate 与 dispatcher 生命周期，再调整 `GraphicsCaptureSource` 的 ABI 平衡、状态通知和 producer cadence，最后接入 `AgentConfig`/`AgentService`。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "review-findings: 已用精确文件与行范围核实 I2、I6、I7 均为 Important，并分别给出根因、最小结构性方案、可测 seam 和 residual-risks。"
    }
  ],
  "changedFiles": [
    ".pi-subagents/artifacts/outputs/475e77fc/context.md"
  ],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "只读 find/grep/read 与 nl -ba 定位目标源码、Host 和测试",
      "result": "passed",
      "summary": "完成 capture ownership/cadence 数据流与测试覆盖核实；未运行构建或测试，因任务要求只读分析。"
    }
  ],
  "validationOutput": [
    "review-findings: I2 - AgentConfigBuilder 的 3-30 秒值只进入 AnalysisThrottle；AgentService/AnalysisEngine 保持独立 1 秒循环，GraphicsCaptureSource 更在每个 FrameArrived 做全尺寸 surface copy。",
    "review-findings: I6 - GraphicsCaptureSource.cs:184-190 未释放 CreateDirect3D11DeviceFromDXGIDevice 返回的 graphicsDevice owning ABI pointer。",
    "review-findings: I7 - GraphicsCaptureSource.cs:45-95 不保存/订阅 item Closed 且吞掉捕获异常，AnalysisEngine.cs:83-86 再吞异常，owner 无法观察并重建。",
    "residual-risks: CsWinRT 引用计数、STA 线程亲和、真实 device-lost/显示拓扑恢复只能在 Windows 真机与 D3D debug layer 下最终验证。"
  ],
  "residualRisks": [
    "WinRT capture 对象跨线程 Dispose/重建可能违反线程亲和，需 Dispatcher executor 和真机验证。",
    "FrameArrived 与 Dispose/fault 并发可能产生 use-after-dispose 或死锁，需显式 in-flight 生命周期测试。",
    "持续设备故障若没有可取消 backoff 会形成重建风暴。",
    "现有测试不观察 CreateCopyFromSurfaceAsync 次数或 COM live objects，不能单独作为 I2/I6 验收证据。"
  ],
  "noStagedFiles": true,
  "diffSummary": "未修改产品源码或测试；仅写入指定只读分析产物 context.md。",
  "reviewFindings": [
    "important: windows-native/src/DesktopPet.Agent/Analysis/AnalysisEngine.cs:37-89, windows-native/src/DesktopPet.Agent/Capture/GraphicsCaptureSource.cs:78-90 - I2 配置仅节流模型，未约束固定消费循环，更未约束 FrameArrived 的全尺寸 GPU-to-CPU 复制。",
    "important: windows-native/src/DesktopPet.Agent/Capture/GraphicsCaptureSource.cs:184-190 - I6 graphicsDevice owning ABI pointer 在 FromAbi 后未 Release。",
    "important: windows-native/src/DesktopPet.Agent/Capture/GraphicsCaptureSource.cs:45-95, windows-native/src/DesktopPet.Agent/Analysis/AnalysisEngine.cs:83-86 - I7 Closed/device fault 不可观察且异常被吞，捕获可永久静默死亡。"
  ],
  "manualNotes": "建议实施顺序：先建立 switchable 单一 owner/fault seam，再修 I6 引用平衡和构造失败释放，随后把配置 cadence 下推到 GraphicsCaptureSource producer copy gate。"
}
```
