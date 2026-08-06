## Review
- Correct:
  - [RollingFileLogger.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/RollingFileLogger.cs:83) 在写入前对 component/message 做脱敏并移除换行；滚动文件数量由 `maxFiles` 限制。相关 rolling/export 测试通过。
  - [DiagnosticExporter.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/DiagnosticExporter.cs:27) 生成临时 ZIP、逐个日志条目再次脱敏，并在完成后原子替换目标 ZIP。
  - [AgentService.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Agent/AgentService.cs:265) 的日志只记录配置元数据；[AgentService.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Agent/AgentService.cs:351) 只记录事件类型和 revision，没有把 `screenEvent.Summary` 写入日志。屏幕摘要仅作为 IPC payload 传输到 App。
  - [ProcessMetricsMonitor.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Diagnostics/ProcessMetricsMonitor.cs:35) 正确计算按处理器数归一化的 CPU，并采样当前 working set；`Stop`/`Dispose` 清理前一采样状态。对应 3 个测试通过。
  - [SettingsWindow.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:1910) 诊断页按 1 秒采样 App/Agent CPU 和 working set；[SettingsWindow.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:1948) 停止 timer、解绑 Tick 并释放 monitor；窗口关闭路径也调用 `StopDiagnostics`。
  - [FullscreenSuppression.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Fullscreen/FullscreenSuppression.cs:48) 使用物理屏幕坐标、负坐标 monitor bounds、最大化状态和按 DPI 缩放的容差；[ModeService.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Ai/ModeService.cs:70) 在发布前再次检查全屏状态。全屏只关闭弹幕和抑制主动输出，不停止 Agent/capture。相关 5 个测试通过。
  - [AiCoordinator.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Ai/AiCoordinator.cs:977) 先原子写每日总结文本，图片失败时保留文本，最后才保存完成日期；失败时不更新 `_lastDiaryDate`，下一次 tick 可重试。
  - [PetWindowManager.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Windows/PetWindowManager.cs:197) 精灵导入在 JSON 保存失败时删除已暂存 PNG，并在补偿删除失败时报告聚合错误。
  - [FactoryResetService.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/FactoryResetService.cs:43) 先移动数据目录再删除凭据，凭据删除失败会尝试恢复数据；root 拒绝文件系统根目录和 root reparse point；测试覆盖 idempotence、凭据失败恢复和 root 防护。
  - [ProviderConfig.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Providers/ProviderConfig.cs:121) 使用 `CredEnumerate("DesktopPet/*")` 仅枚举 DesktopPet 命名空间，并对 `ERROR_NOT_FOUND` 做幂等处理。

- Blocker:
  - 无发现可直接判定为 Blocker/Critical 的问题。

- Important:
  - [RollingFileLogger.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/RollingFileLogger.cs:95) 的 `maxBytes` 不是硬上限：单条消息只要超过阈值，轮转后仍会整条写入，单个日志文件可大于 `maxBytes`。更严重的是，轮转失败后 `_stream` 已在 [RollingFileLogger.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/RollingFileLogger.cs:109) 被置为 `null`，异常在 [RollingFileLogger.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/RollingFileLogger.cs:99) 被吞并记录到 `LastError`；后续写入会在 `_stream!.Write(...)` 处抛出 `NullReferenceException`，因为该异常不属于捕获的 IO 异常。`DiagnosticExporter` 在 [DiagnosticExporter.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/DiagnosticExporter.cs:35) 打开日志 reader 时可能持有不允许 delete/move 的句柄，因此导出期间恰逢轮转可触发该状态。此问题同时影响 rolling bounded 和诊断导出并发稳定性。
  - [SecretRedactor.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/SecretRedactor.cs:20) 只覆盖少数 Bearer、`apiKey`/`token`、查询参数和 `sk-...` 形态，无法保证“secret-free ZIP”。例如 `client_secret=abc`、JSON 形式的 `"Authorization": "Bearer abc"`、自定义 provider 的 `credential=abc` 不会被完整识别。当前目标代码没有直接把 `screenEvent.Summary` 写入日志，但 [DiagnosticExporter.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/DiagnosticExporter.cs:31) 会打包目录中所有历史 `*.log*` 文件，因此旧日志或异常文本包含未覆盖形态时，ZIP 仍可能泄露秘密。
  - [App.xaml.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/App.xaml.cs:380) 在 factory reset 成功后直接调用 `RestartApplication()`；失败分支在 [App.xaml.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/App.xaml.cs:393) 显示错误后再次直接调用。`Process.Start` 失败、路径不可用或新进程启动失败时没有独立的失败报告/恢复路径，且此时 App 已经关闭窗口、Agent、logger 并可能已删除数据。重启失败会覆盖原始 reset 错误或从 UI 事件中未处理地抛出。
  - [App.xaml.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/App.xaml.cs:443) 的 `WaitForParentRestart` 存在真实启动竞态：父进程在 [App.xaml.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/App.xaml.cs:429) 启动子进程后立即 `Shutdown(0)`；如果子进程运行到 `Process.GetProcessById(parentId)` 时父进程已经退出，该调用抛出 `ArgumentException`，即使传入的 PID/start ticks 身份仍然能够证明这是预期的父进程退出。这样会导致 reset 后新实例启动失败，违背可靠重启要求。
  - [App.xaml.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/App.xaml.cs:257) 对迁移 JSON 损坏和结构错误只使用 `Debug.WriteLine`，保留文件后继续以空 store 启动。该错误没有通过 `PersistenceErrorPresenter`、logger 或用户可见状态报告，属于静默忽略 JSON 持久化错误；如果迁移文件包含用户数据，用户只会看到一个新建默认宠物，而不会得知迁移失败。

- Note:
  - [FactoryResetService.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/FactoryResetService.cs:111) 对历史 `.reset-*` 残留目录只按名称匹配，没有再次拒绝 reparse point。当前 root 本身有 reparse 防护，且这些残留通常由本服务创建；不过 factory reset 是破坏性操作，建议原生验收覆盖 junction/symlink 和锁定残留目录。
  - [AiCoordinator.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Ai/AiCoordinator.cs:875) 主动互动任务未被单独保存或等待。runtime cancellation 通常会取消其中模型请求，但在请求完成和 UI 发布之间发生 shutdown 时，仍存在 `RouteOutput` 在 reset 后执行的竞态。`ModeService.Shutdown()` 只关闭弹幕，不把 mode 设置为 `Silent`。这不是全屏路径的失败，因为全屏发布前检查存在；它是 reset/应用关闭生命周期的残余风险。
  - [AiCoordinator.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Ai/AiCoordinator.cs:1000) 每日总结通知使用 `FromAnalysis: true`，因此会经过全屏抑制；文本/图片生成仍继续执行，符合“capture/analysis continues”要求。
  - 没有实际 Windows 原生 smoke 证据：未验证 Credential Manager 中真实 `DesktopPet/*` 枚举/删除、凭据部分失败、锁定文件、重启父子进程竞态、实际 borderless/maximized 窗口在负坐标和 mixed-DPI monitor 上的 HWND 行为，以及 GraphicsCapture 在全屏抑制期间仍持续采集。单元测试只验证纯逻辑。

已运行的 focused checks：
- `DiagnosticsTests|FactoryResetServiceTests|ProviderCredentialMigratorTests`：17/17 通过。
- `ProcessMetricsMonitorTests|FullscreenSuppressionTests|SpriteSheetCacheTests`：14/14 通过。首次并行运行因 `VBCSCompiler` 锁定 Core DLL 失败，串行重跑通过。
- `AgentServiceTests`：14/14 通过。
- `git diff --check`：通过。
- 未修改任何文件；未产生 staged changes。