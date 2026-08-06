# Windows 版代码审查报告（2026-08-06）

> 审查范围：windows-native/ 全量（App / Core / Infra / Agent / AgentHost）。
> 方法：主 agent 通读核心链路 + 3 个并行 reviewer 分模块独立审查，关键 claim 已人工复核源码。
> 用途：修复交接清单。按波次推进，每波完成后更新状态。

## Critical（波次 1）

| # | 问题 | 位置 | 状态 |
|---|---|---|---|
| C1 | 模型请求调度器单 worker 串行化，并发闸(3)/多宠物并行/P0 优先全部失效；测试 MaxInFlight<=2 无法暴露 | ModelRequestScheduler.cs:74-115 | ✅ 已修：固定 worker 池（worker=concurrency）+ 新增 2 个回归测试 |
| C2 | 精灵导入预览把 PNG/WebP 文件字节当像素缓冲（BitmapSource.Create），异常在 async void OnDrop 中无 handler → 崩溃 | SpritePreviewWindow.cs:46-48, PetWindow.cs:247 | ✅ 已修：SpriteSheet 保留解码 SourceRgba，预览用真像素 + 透明回退 |
| C3 | 多宠物 token 记账恒记第一只宠物；并发对话读-改-写竞态丢账（care/画像/亲密度/会话记忆） | AiCoordinator.cs:450-456, App.xaml.cs:272-275 | ✅ 已修：对话串行闸 _chatSerial + 记账改记选中宠物 |
| C4 | 跨线程无锁：DanmakuEngine（Win2D 渲染线程 vs UI 线程）、ScreenEventLog（RPC 线程 vs Timer/UI 线程） | DanmakuWindow.cs:112/132, FrameHasher.cs:121-126 | ✅ 已修：两处均加锁 + Active 快照 |
| C5 | 对话窗"朗读"按钮点击无效：按钮只改窗口内状态，Speak 读持久设置 | ChatWindow.cs, AiCoordinator.cs:679 | ✅ 已修：TtsToggled 事件 → _ttsSessionEnabled 生效状态 |
| C6 | 设置窗持有构造时快照：浮球/热键切输出模式后，设置页任意保存会回滚旧值 | SettingsWindow.cs, App.xaml.cs ApplyOutputModeFromBall | ✅ 已修：RefreshFromStore() + 外部变更通知 |

## Important（波次 2）

| # | 问题 | 位置 |
|---|---|---|
| I1 | 语言设置完全不生效：I18nService.T() 零调用，UI 全硬编码中文 | 全部 UI 文件 |
| I2 | 截屏间隔 3-30s 设置只限频模型调用，截屏恒 1fps 全分辨率拷贝（4K ~66MB/帧），语义与实现脱钩 | AnalysisEngine.cs:35, AgentService.cs:41 |
| I3 | 全局快捷键硬编码 Ctrl+Alt+H/M/S/Q 不可自定义（架构 §7 承诺）；注册失败静默 | App.xaml.cs:279-295 |
| I4 | 模型连接编辑器无"测试连接"（架构 §3.1 承诺）；多连接 API Key 固定 keyRef "model-key" 互相覆盖 | SettingsWindow.cs:2044, 2110 |
| I5 | 文档承诺缺失：恢复出厂设置 / 日志导出 / CPU 内存自采样 / 全屏暂停弹幕互动 | SettingsWindow About 页 |
| I6 | D3D 设备引用泄漏：CreateDirect3D11DeviceFromDXGIDevice 后缺 Marshal.Release（FromAbi 内部 AddRef） | GraphicsCaptureSource.cs:186 |
| I7 | DeviceLost/Closed 未订阅：GPU 设备丢失后捕获静默死亡 | GraphicsCaptureSource.cs:39-50 |
| I8 | 拖拽无 WM_CAPTURECHANGED/WM_CANCELMODE 兜底：捕获被抢占后 _pressed 恒 true 永久无响应 | PetWindow.cs:591 |
| I9 | CanvasAnimatedControl Paused 永不恢复（无弹幕也 60fps 空转）；关闭未 Dispose（泄漏 loop + CanvasTextFormat） | DanmakuWindow.cs:108-143 |
| I10 | 命名管道无鉴权：同用户进程可下发恶意 BaseUrl 外带凭据 / Shutdown | PipeRpc.cs:81 |
| I11 | 心跳缺失：Ping/Pong 死代码，App 挂死时 Agent 无限截屏烧 token | AgentService.cs:94-96 |
| I12 | HttpClient 未复用未释放：每次设置变更新建 provider + new HttpClient()，socket 泄漏 | OpenAiCompatibleModelProvider.cs:33, AiCoordinator.cs:446 |
| I13 | UI 线程同步阻塞：设置保存 → DisposeAsync().Wait(2s) + StopAgent .Wait(1s) | AiCoordinator.cs:443, 305-310 |
| I14 | 超时重试契约断裂：重试耗尽抛裸 TaskCanceledException（用户看不到超时文案）；provider 30s HttpClient.Timeout 与调度器 30s 竞态 | ModelRequestScheduler.cs:130-141 |
| I15 | JSON 非原子写入（File.WriteAllText 直写），崩溃截断 → 数据静默丢失 | JsonStore.cs:80 |
| I16 | IntimacyEngine 衰减地板 Math.Max(DecayFloor, decayed) 把自然低值抬升，与注释相悖 | IntimacyEngine.cs:45 |
| I17 | SpriteLoader._sheetCache 无上限（导入自定义精灵永久持有解码帧） | SpriteLoader.cs:23,102 |

## Minor（波次 3，择机）

- 弹幕参数硬编码（字号 30 / 速度 220-420 / 轨道 10 / Microsoft YaHei UI）
- PetInstance.Size 死字段（零引用）；PetWindow 窗口 260×320 硬编码
- PetWindowManager.PresetPoolJson 与 AppSettings.Defaults 双真值
- QuickBubbleController.DurationKey 死代码（Tauri key 从未消费）
- 温度 0.7 双源（PersonaEngine.cs:41 vs ModelContracts.cs:57）
- AiSettingsJsonConverter.Write 手写全字段序列化，易漏字段
- 截屏只捕主屏（MonitorFromPoint(0,0)）；弹幕层 Left=0 漏负坐标副屏
- AgentService 构造器部分失败泄漏；_ = PushEventAsync fire-and-forget；旧 engine task 不观察
- OnAgentExited Thread.Sleep(30s) 阻塞线程池线程
- 日志写 %TEMP% 无滚动（架构承诺 %APPDATA%/DesktopPet/logs/）
- EdgeTts 控制帧递归解析 + pong 长度截断（>125 字节）
- AiCoordinator.Speak 强转 (MemoryStream) 破坏接口抽象；MediaEnded 处理器无界累积
- PetWindowRoamHost.SetLogical 死代码 `_ = x; _ = y;`；AppSettings.Normalize `roam with { Speed = roam.Speed }` 无意义
- PetWindow.OnMouseLeftButtonDown/Up 覆写死代码（WndProc 已 handled）
- TauriMigration 零实例边界丢弃 legacy 数据；PetStoreModel 严格版本校验静默不可见
- PipeRpc Hello 注释方向写反
- SpriteLoader catalog.json 缓存永久不过期
- SpritePreviewWindow 网格线用 SourceWidth 但 Stretch.Uniform 显示 384×320，比例可能不一致
