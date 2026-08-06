核查完毕，所有关键结论都有实证。以下为审查报告。

## Review

**审查范围**：Agent/AgentHost 全量 + Core 相关 + PipeRpc + App 侧对接代码（AiCoordinator.cs）。注：清单中的 `DesktopPet.Core/Ai/ModelContracts.cs` 实际位于 `DesktopPet.Core/Scheduling/ModelContracts.cs`。

**已验证为正确的点（非问题）**
- `Direct3D11CaptureFramePool` 在 `Dispose()` 中正确释放并退订 `FrameArrived`（GraphicsCaptureSource.cs:192-200）。
- 复帧无泄漏：`OnFrameArrived` 的 `frame` 走 `using`、`_latest` 替换前先 Dispose 旧帧、`CaptureAsync` 转换完 Dispose 帧。帧队列管理正确。
- `CreateDirect3D11DeviceFromDXGIDevice` 的 DllImport（d3d11.dll、ExactSpelling、StdCall）与 `IGraphicsCaptureItemInterop`（3628E81B…）、IDXGIDevice（54ec77fa…）、IActivationFactory IID 均正确；`GetActivationFactory` 的 RCW AddRef/Release 平衡正确。
- 未用 JPEG：`CapturedFrameExtensions` 用 PNG（320×180 L8 每事件约 30KB），无"JPEG 质量硬编码"问题。
- Agent 崩溃恢复链路完整：App 看门狗指数退避重启（3s→10s→30s、5 次封顶）、管道 EOF 触发 Agent 自终止、App 断连后重连。
- 测试覆盖良好：AnalysisEngine/AgentService/Switchable 均有单测；`dotnet build` 0 错误通过。

### Critical
无。最接近的两项（设备泄漏、设备丢失死寂）均为确定性问题但不导致崩溃/数据损坏，归入 Important。

### Important

1. **GraphicsCaptureSource.cs:186 — D3D 设备包装器引用泄漏（缺 `Marshal.Release(graphicsDevice)`）**
   经 IL 反汇编实证：pin 定运行时 `Microsoft.Windows.SDK.NET.Ref 10.0.19041.57` 中 `ObjectReference<T>.FromAbi` 内部调用 `Marshal.AddRef`。因此 `FromAbi(graphicsDevice)` 后引用计数为 2（CreateDirect3D11DeviceFromDXGIDevice 的 1 + FromAbi 的 1），但 finally 只释放 `dxgiDevice`，原始引用永远不还 → WinRT 设备包装器及其背后的 D3D11 设备在 Agent 进程生命周期内永不释放。对比同文件 137/141 行 `GraphicsCaptureItem.FromAbi(itemAbi)` 后正确 `Marshal.Release(itemAbi)`（该路径是平衡的），两条路径语义假设相反，必然一条错。而 `SwitchableScreenCaptureSource` 每次 enable/disable 都新建/销毁一个 `GraphicsCaptureSource` → 反复切换分析开关会累积泄漏多个 GPU 设备直至进程重启。
   修复：`CreateD3DDevice` 的 finally 中补 `Marshal.Release(graphicsDevice)`（与 137/141 行一致）。

2. **GraphicsCaptureSource.cs:39-50 / 108-121 — `Direct3D11CaptureFramePool.DeviceLost` 未订阅，捕获静默死亡**
   GPU 设备丢失（驱动更新、休眠恢复、RDP 断开、虚拟机宿主变更）时 pool 触发 DeviceLost 并停止出帧；`OnFrameArrived` 的 catch-all 与 `CaptureAsync` 的 catch-all 会把一切异常吞掉，分析引擎继续每秒空转，屏幕感知功能永久失效直到 Agent 进程被重启。`GraphicsCaptureItem.Closed`（显示器拔线）同样未处理。
   修复：订阅 `DeviceLost`（及 item `Closed`），重建 device/pool/session，或至少记录日志并回传状态让 App 侧可感知。

3. **GraphicsCaptureSource.cs:84 — STA 消息泵上做全分辨率同步复制，每帧三重全尺寸分配**
   `CreateCopyFromSurfaceAsync(...).GetAwaiter().GetResult()` 在 Dispatcher 线程上阻塞，4K 屏幕每帧 ~33MB GPU→CPU 拷贝 + 33MB `Buffer` + 33MB `byte[]`，每秒约 66MB 内存抖动（仅为了产出 320×180 灰度）。阻塞期间消息泵停摆：后续 FrameArrived 排队、`dispatcher.Invoke`（capture 工厂路径）被耦合延迟，1 缓冲 pool 丢帧。设计文档 §6.4 的"320×180 缩略图成本护栏"被全分辨率 CPU 转换路径抵消。
   修复建议：用 GPU 端缩小（把 `frame.Surface` 渲染到小纹理再回读）或至少把转换挪出 STA 线程、用 `SoftwareBitmap.LockBuffer` 直读避免双重拷贝。

4. **截屏间隔 1s 写死，且与 `ScreenAnalysisIntervalSeconds` 配置完全脱钩（AgentService.cs:41、AnalysisEngine.cs:35、AgentConfig 无截屏字段）**
   设置页"截屏分析间隔 3-30s"实际只作用于模型调用限频（`AnalysisThrottle`），截屏恒为 1fps——即使配置 30s 分析间隔，也以每秒 1 次全分辨率拷贝空转（30 次截屏换 1 次分析）。320×180 分辨率、1s 间隔、主显示器选择均写死且无配置通道。
   修复：让截屏节奏跟随 `ScreenAnalysisIntervalSeconds`（或拆分"截屏间隔/分析限频"两个语义并修正 UI 文案），截屏参数进 `AgentConfig`。

5. **心跳缺失——Ping/Pong 是死代码（AgentService.cs:94-96 仅响应；App/AiCoordinator.cs 从不发送 Ping）**
   Agent 只能靠管道 EOF 感知 App 进程退出；若 App 挂死但进程存活，Agent 会无限期持续截屏+调云端视觉模型，token 持续烧。协议定义了 Ping/Pong 却无人发起，"AI 关闭=无后台进程/无网络"的隐私基线在 App 异常挂死时被打破。
   修复：App 侧定时发 Ping（如 5s），Agent 侧超时（如 30s 无消息/Ping）自动退出，由看门狗负责恢复。

### Minor

6. **AgentService.cs:160,224-231 — `_ = PushEventAsync(e)` fire-and-forget，关停期抛 `ObjectDisposedException` 成为未观察异常**。`SafeSendAsync` 只捕 IOException；`DisposeAsync` 已释放 `_writeLock`/管道后，在途发送会抛 ObjectDisposedException。补捕获或关停时 drain 在途推送。

7. **AgentService.cs:156 — 每次 `RebuildEngineLocked`（即每次设置变更下发 Config）新建 `OpenAiCompatibleModelProvider` → 新 `HttpClient`（默认 handler），旧实例永不释放**。频繁切换设置会累积 socket/handler。按配置身份缓存 provider 或实现 IDisposable。

8. **AgentService.cs:131-135 — 旧 engine task 被丢弃（Cancel 后不 await 不观察）**。`RunAsync` 内部有兜底 catch，实际风险低，但新引擎启动时旧引擎可能仍在同一 Switchable capture 上做最后一拍（gate 串行化了，暂无竞态）。

9. **AgentHost/Program.cs:16-31 — 截屏初始化失败静默降级为 `OfflineFrameSource([])`**：若 GraphicsCapture 不可用/初始化失败，Agent 永久空转（`CaptureAsync` 恒 null），App 侧毫无感知，"屏幕分析"功能静默失效。建议在 Hello 握手载荷中带上能力状态，App 据此提示而非静默。

10. **GraphicsCaptureSource.cs:39-50 — 构造器部分失败泄漏**：`CreateCaptureItemForMonitor`/`CreateCaptureSession`/`StartCapture` 抛异常时已创建的 `_device`/`_framePool` 不释放（构造器无 finally）。加 try/catch 清理。

11. **GraphicsCaptureSource.cs:45 — 主显示器写死**：`MonitorFromPoint(0,0,MONITOR_DEFAULTTONEAREST)` 永远捕获主显示器，无多显示器选择或配置项。

12. **PipeRpc.cs:81 — 命名管道无任何鉴权**：同用户任意进程可连接 `\\.\pipe\DesktopPet.Agent`，下发含攻击者 `ProviderBaseUrl` + 受害者 `ProviderApiKeyRef` 的 Config，Agent 会把凭据管理器的 key 以 Bearer 头发往攻击者端点（凭据外带向量）；也可直接 Shutdown（DoS）。本地信任边界内低危，建议至少校验连接端 PID/进程名。

13. **PipeRpc.cs:11 — `Hello` 注释写反**："client → server"但实际由 server 发出（AgentService.cs:71），App 侧等待行为一致，仅文档性错误。

14. **AnalysisEngine.cs:119/139/144 — 事件时间戳用 `DateTime.Now` 而非 `TickAsync` 注入的 `now`**，破坏可测时钟注入的一致性。

15. **AgentConfig.cs — `MinAnalysisIntervalSeconds` 线上无界**：App 侧 clamp 3-30，但 Agent 侧只 `Math.Max(0,…)`，畸形/恶意配置 0 值可导致每次变化都调云端（1 次/秒）。

### 总体结论

结构清晰、分层与测试到位，双进程生命周期与崩溃恢复基本闭环，无崩溃级缺陷；核心风险集中在 WinRT 互操作资源生命周期（一处已实证的 D3D 设备引用泄漏 + 设备丢失静默死亡）和配置对接缺失（1s 截屏写死、心跳死代码）。GraphicsCaptureSource 是唯一无自动化测试的模块，恰好是问题集中地，建议补一次真机/真 GPU 冒烟。