审查完成。以下按严重程度给出发现（所有结论均有代码证据支撑，审查为只读，未做任何修改）。

---

## Review

### Critical（必须修复）

**C1. 并发闸形同虚设：单 worker 循环把所有请求串行化**
- 位置：`ModelRequestScheduler.cs:74, 79-115`；相关：`AiCoordinator.cs:446-449`
- 问题：`_loop ??= Task.Run(() => RunLoopAsync(_loopCts.Token))` 只启动**一个**循环任务，而 `RunLoopAsync` 在循环体内 `dequeue → _gate.WaitAsync → ExecuteWithPolicyAsync（完整 await）→ _gate.Release` 内联执行，一个 job 跑完才取下一个。因此任何时刻只有 1 个请求在执行，`concurrency=3` 与 `_gate`（:58）完全失效——`_gate` 永远只有一个持有者，`concurrency` 参数是死代码。
- 为何是问题：违背三处既有承诺——本类注释"SemaphoreSlim 并发闸（默认 3）"、架构文档 §3.3、`AiCoordinator.cs:446`"多宠物并行独立请求不被串行化"。实际效果：`TryProactiveInteraction` 的多宠物并行（2 只 × 8s 超时）被串行化成最多 16s；P0 对话会被正在执行的 P1/P2 头阻塞（head-of-line blocking）。测试 `SchedulerTests.cs:66-78` 只断言 `MaxInFlight <= 2`，串行执行 MaxInFlight=1 也能通过，**测试无法暴露该缺陷**。
- 修复：按经典有界并行 worker 模式——循环 dequeue 后 `_ = ProcessJobAsync(job)` 生成独立任务，由 `_gate` 限流；或删除并发承诺改为显式单线程文档化。

**C2. ScreenEventLog 跨线程无锁访问（正常运行必然并发）**
- 位置：`FrameHasher.cs:121`（`_events.Enqueue`）、`:126`（`Recent()` → `_events.ToArray()`）
- 问题：写入方是 RPC 接收循环线程（`AiCoordinator.cs:426` `OnAgentEvent` → `_eventLog.Add`），读取方是 Timer 线程（`AiCoordinator.cs:526` `TryProactiveInteraction`、`:619` `TryDailySummary`）和 UI 线程（`SendChatAsync` → `ChatPipeline` → `_eventLog.Recent()`）。`Queue<T>` 非线程安全，`Add` 与 `ToArray` 交错会导致内部状态损坏或抛异常；调用点外层 try/catch 吞掉后表现为事件丢失/偶发失败，行为非确定性。
- 修复：加锁或改用 `ConcurrentQueue<T>`（容量裁剪逻辑保持）。

**C3. 并发对话的读-改-写竞态：token/XP/亲密度/画像丢账 + List 竞态**
- 位置：`AiCoordinator.cs:190-197`（`SendChatAsync` 无任何串行化）、`ConversationMemory.cs:32-49`、`App.xaml.cs:272-275`
- 问题：scheduler 并发（即使按 C1 修复后）允许多个对话同时在飞。`_conversation.Append`/`BuildContext` 对 `List<ChatMessage>` 并发增/遍历可损坏状态；`RecordChatSuccess` 并发 `MergeProfile` + `SaveMemoryProfile`、`_intimacy.RecordConversation` 都是读-改-写，后到者覆盖先到者。最严重的是 `App.xaml.cs:272-275` `RecordTokens`：`LoadCare() → FeedTokens(care) → SaveCare(states)` 整段读-改-写，两个并发记账保存互相覆盖 → **token/XP 静默丢账**（这正是评审点 6 的典型）。
- 修复：对"管道执行 + 记账/持久化"整体加一把异步锁（或每 petId 串行化）；持久化走单写队列。

---

### Important（明确缺陷，触发条件较窄或影响有限）

**I1. 超时重试错误契约：最终超时抛裸 `TaskCanceledException`；双重 30s 超时源竞态**
- 位置：`ModelRequestScheduler.cs:130-141`；`OpenAiCompatibleModelProvider.cs:33-34, 93-96`；`AiCoordinator.cs:208-218`
- 问题：(a) 重试耗尽后，调度器超时产生的 OCE 直接抛给调用方（测试 `SchedulerTests.cs:141` 明确断言 `ThrowsAsync<TaskCanceledException>`），`AiCoordinator.SendChatAsync` 的 `catch (ProviderException)`（:208）不命中 → 用户看到通用"（出错了，请稍后再试）"而非专门超时文案"（模型响应超时了…）"（:212），超时 UX 在重试耗尽路径上永远不可达。(b) provider 默认 `_http.Timeout = 30s` 与调度器对话超时 30s 是两个独立计时器竞速：若 HttpClient 先触发，provider 把超时包成 `ProviderException("timeout")`（:93-96），该异常**不是** OCE，重试 catch（:135）不命中 → 重试被静默跳过。重试行为非确定性。
- 修复：末次尝试且 `!job.Ct.IsCancellationRequested` 时把 OCE 统一转换为 `ProviderException("timeout")`；并把 `_http.Timeout` 设为严格大于调度器超时（如 +15s）消除竞态。

**I2. HttpClient 未复用 + 泄漏**
- 位置：`OpenAiCompatibleModelProvider.cs:33-34`、`OpenAiCompatibleImageProvider.cs:32-33`；`AiCoordinator.cs:437-453`
- 问题：每个 provider 实例 `new HttpClient()`（无 `IHttpClientFactory`/共享 `SocketsHttpHandler`）；`RebuildChatPipeline` 在每次设置/人格/连接变更时新建 provider，旧 HttpClient 及 socket 池无任何释放路径（provider 非 `IDisposable`，调度器 Dispose 也不涉及）→ 设置改动即泄漏连接句柄。另 `ListModelsAsync`（:151）未设置 `CompleteAsync` 中统一添加的 User-Agent，行为不一致。
- 修复：注入/共享 HttpClient（或工厂），provider 实现 IDisposable 并由调度器生命周期管理。

**I3. DisposeAsync 不取消在飞请求，App 侧 2s 截断导致遗留 loop 使用已释放信号量**
- 位置：`ModelRequestScheduler.cs:143-150`；`AiCoordinator.cs:443`
- 问题：每次尝试的 linked token 只观察 `job.Ct`，`_loopCts` 取消不影响正在执行的 provider 调用 → `DisposeAsync` 最长等待 30-60s；App 侧用 `Wait(2s)` 截断后 `_wake`/`_gate` 被 Dispose，遗留 loop 线程可能仍在 `_gate.WaitAsync`/`ExecuteWithPolicyAsync` 中 → `ObjectDisposedException`（未观察异常）、在飞 job 的 Completion 被弃置。
- 修复：把 `_loopCts.Token` 并入每次尝试的 linked source；DisposeAsync 取消在飞 job。

**I4. 非原子 JSON 写入 + 崩溃中断 → 静默数据丢失**
- 位置：`JsonStore.cs:80`（`File.WriteAllText` 直写目标文件，无 temp+rename）；`JsonStore.cs:118-129`（`LoadCare` 等 catch `JsonException` 静默返回空）
- 问题：与 C3 的并发 SaveCare 叠加，进程崩溃/断电在写中途会截断 care.json/app-settings.json 等，下次启动静默回到空状态，用户养成数据全丢。
- 修复：temp 文件 + `File.Replace` 原子替换；可选保留 `.bak`。

**I5. IntimacyEngine 衰减地板把自然低值抬升，与注释相悖**
- 位置：`IntimacyEngine.cs:45`
- 问题：`missedDays > 0 ? Math.Max(DecayFloor, decayed) : ...`——首次互动 value=2、隔两天再互动（missedDays=1，decayed=1）→ afterDecay=5，数值被"衰减"抬高到 5。注释明确写"地板仅作用于真实衰减（未衰减的自然低值不被抬升）"，实现与之矛盾，会产生可见的亲密度跳变。
- 修复：地板只作为真实衰减的下限，例如 `decayed >= DecayFloor ? decayed : Math.Min(State.Value, DecayFloor)` 语义。

---

### Minor

**M1. 已取消的排队任务仍被执行** — `ModelRequestScheduler.cs:96` `_gate.WaitAsync(ct)` 用的是 `_loopCts.Token` 而非 `job.Ct`；入队后外部取消不会移出队列，仍会消费一次执行机会（快速失败）。同时重试的 `Task.Delay`（:139）期间持有执行槽，P0 三次超时最长占用 ~90s。

**M2. `_loop ??=` 非原子初始化** — `ModelRequestScheduler.cs:74`：首批两个并发 `EnqueueAsync` 可能各自读到 null 启动两个 loop，破坏单一 worker/FIFO 语义（且"意外实现"了本不存在的并发，行为非确定）。

**M3. QuickBubbleDuration 死代码 + 双真值源** — `QuickBubbleController.cs:75-90`：`DurationKey`（"ap_quick_bubble_duration"）/`NormalizeSeconds`/`ReadDurationMs` 仅被测试引用；生产路径（`PetWindow.cs:297,312,349`）直接读 `AppSettings.QuickBubbleDurationSeconds` 并自行钳制。同一用户设置存在两套来源，Tauri localStorage key 从未被消费。

**M4. 默认值/常量重复** — 温度默认 0.7 双源（`PersonaEngine.cs:41` 与 `ChatRequest` 默认值 `ModelContracts.cs:57`）；频率档 low/medium/high 常量与"未知→medium"归一化在 `AiSettings.cs` 与 `InteractionEngine.cs` 各一份；`ModelContracts.cs:135` 注释"MaxOutputTokens 空 = 桌宠短句默认 120"与对话路径实际行为（`AiCoordinator.CurrentMaxOutputTokens` 返回 null → 不发送，上游默认）不符。

**M5. EdgeTtsSocket 控制帧递归 / 解析重复 / pong 长度截断** — `EdgeTts.cs:158-170`：ping/pong/未知 opcode 用 `return await ReceiveAsync(ct)` 递归，异常服务端可构造无界控制帧流（无界续延链、内存增长）；`ReadFrameCoreAsync`（:192-219）与 `ReceiveAsync` 帧头/长度解析重复（DRY）；`SendPongAsync`（:221-229）写 `(byte)payload.Length`，未校验控制帧 ≤125 字节，超长时长度字段截断损坏协议。

**M6. SapiTtsProvider 取消与异常吞没** — `SapiTtsProvider.cs:16-19`：`Task.Run(..., ct)` 的 ct 只在任务开头检查，`synth.Speak` 长文本不可中断；`:30-36` 两个 `catch (Exception)` 静默回退（有回退语义可接受，但无日志，调错时无从得知 SelectVoice 为何失败）。

**M7. 模型 Provider 错误分级不完整** — `OpenAiCompatibleModelProvider.cs:121-125`：响应体读取阶段 OCE 在 ct 已取消时被误标为 `invalid-response`（把超时误报为响应解析失败）；`OpenAiCompatibleImageProvider.cs:96-101`：url 回退 GET 的 `EnsureSuccessStatusCode` 抛裸 `HttpRequestException`（未归一为 `ProviderException`），`Convert.FromBase64String` 的 `FormatException` 也未包装。

**M8. AiCoordinator.Speak 事件处理器累积** — `AiCoordinator.cs:688-691`：每次朗读都给复用的 `_ttsPlayer` 追加一个 `MediaEnded` 闭包，处理器列表无界增长（每次播放触发 N 个删除操作，虽有 try/catch 兜底，属内存/行为泄漏）；且 `Speak` 强转 `(MemoryStream)stream`（:686）把调用方耦合到具体 TTS 实现（接口返回 `Stream` 的抽象被破坏）。

**M9. TauriMigration 边界数据丢弃** — `TauriMigration.cs:37-40`：`ap_pet_instances` 解析成功但零实例、同时存在 legacy slug 时，`MigrateLegacyPetStore` 因 store 非空直接返回空新 store，legacy 宠物与 care 数据一并丢弃（无提示）；`ParseCare` 吞 `JsonException` 返回空。

**M10. PetStoreModel 严格版本校验 → 静默数据不可见** — `PetStoreModel.ParsePetStore`：`version != 1` 或结构异常即返回 null，上层（`FileJsonStore.LoadPetStore`、`TauriMigration`）静默回退为空，未来格式文件导致全部宠物实例"消失"且无迁移提示。

**M11. OnAgentExited 线程池阻塞** — `AiCoordinator.cs:332`：Exited 回调内 `Thread.Sleep` 最长 30s 阻塞线程池线程；`:333-337` 在 sleep 之后才检查 `_restartFailures > 5`，第 6 次失败仍先睡 30s 再停止，浪费但非错误。

---

### 关于"硬编码值 vs 设置项对接"的结论（评审点 2）

- `AiSettings` 全部字段均被真实消费（`ScreenAnalysis`/`ScreenAnalysisIntervalSeconds` → `AgentConfigBuilder`；`Onboarded` → App.xaml.cs 引导窗；其余见 AiCoordinator），**未发现"设置文件有字段但未消费"**。
- Scheduler 并发数(3)、超时(30/8/60s)、退避(500ms)、温度(0.7)、token 上限(120) 均为硬编码常量且**不存在对应设置字段**，属架构既定；真正的问题是这些常量存在双真值源（M4）且并发参数因 C1 完全失效。
- 唯一真正"写了却没用"的配置键是 `QuickBubbleDuration.DurationKey`（M3）。

### 总体结论

核心缺陷集中在**调度器并发承诺未实现**（单 worker 串行化，连带并发闸、多宠物并行、"P0 不被阻塞"全部失效）与**多处跨线程/读-改-写数据竞态**（ScreenEventLog、会话记忆、token/亲密度记账），以及超时重试的错误契约断裂；Infra 层以 HttpClient 泄漏和 TTS socket 边界健壮性为主。建议按 C1→C3→C2→I1→I2 的优先级修复后再扩展功能。

---