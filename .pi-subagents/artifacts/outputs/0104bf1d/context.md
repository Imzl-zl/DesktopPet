# Code Context

## Files Retrieved
1. `docs/windows-code-review-2026-08.md` (lines 26-45) - I14-I17 的原始审查结论；该文档当前为未跟踪文件。
2. `windows-native/src/DesktopPet.Core/Scheduling/ModelRequestScheduler.cs` (lines 27-166) - I14 的调度超时、重试、取消和当前 C1 worker-pool diff。
3. `windows-native/src/DesktopPet.Infra/Providers/OpenAiCompatibleModelProvider.cs` (lines 16-112) - I14 的第二个 30s 超时源及 ProviderException 转换。
4. `windows-native/src/DesktopPet.App/Ai/AiCoordinator.cs` (lines 464-480) - 生产环境用默认 30s provider + 默认 30s scheduler 构建链路。
5. `windows-native/tests/DesktopPet.Core.Tests/SchedulerTests.cs` (lines 113-210, 212-263) - I14 现有测试错误地断言裸 TaskCanceledException；后半为当前 C1 diff 新增测试。
6. `windows-native/tests/DesktopPet.Infra.Tests/ProviderTests.cs` (lines 242-258) - provider 自身 HttpClient 超时映射为 `ProviderException(Code="timeout")` 的覆盖。
7. `windows-native/src/DesktopPet.Core/Storage/JsonStore.cs` (lines 41-87, 103-241) - I15 所有存储写入汇聚到直接 `File.WriteAllText`，IOException 被吞。
8. `windows-native/tests/DesktopPet.Core.Tests/JsonStoreTests.cs` (lines 11-132) - 只有 roundtrip/missing-file 覆盖，无原子性、旧文件保留或故障注入覆盖。
9. `windows-native/src/DesktopPet.Core/Care/IntimacyEngine.cs` (lines 1-55) - I16 当前实现已仅在真实衰减时应用地板。
10. `windows-native/tests/DesktopPet.Core.Tests/IntimacyTests.cs` (lines 18-98) - 覆盖自然低值同日增长和长期衰减地板。
11. `windows-native/src/DesktopPet.App/Rendering/SpriteLoader.cs` (lines 14-107) - I17 无上限 Dictionary 缓存及唯一按 slug 移除路径。
12. `windows-native/src/DesktopPet.App/Windows/PetWindowManager.cs` (lines 50-69, 153-180) - 删除宠物窗口时未逐出精灵缓存；导入精灵使用永久唯一实例 ID。
13. `windows-native/tests/DesktopPet.App.Tests/PetPreviewCardTests.cs` (lines 8-39) - 仅验证加载后命中共享缓存，无容量/逐出测试。

## Key Code

### I14 - Important - 未修，当前 diff 还扩大了取消语义风险

**当前状态**：问题成立。当前工作区修改了 `ModelRequestScheduler.cs`，但只修 C1 并发 worker 池；`ExecuteWithPolicyAsync` 的超时耗尽路径未改，测试仍明确要求 `TaskCanceledException`（`SchedulerTests.cs:131,151`）。

**根因**：

```csharp
// ModelRequestScheduler.cs:147-157
using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Ct, _loopCts.Token);
linked.CancelAfter(timeout);
return await _provider.CompleteAsync(job.Request, linked.Token);
// 最后一次 OperationCanceledException 没有 catch/translate，裸异常外泄
```

provider 又有独立计时器：

```csharp
// OpenAiCompatibleModelProvider.cs:32-33, 94-97
_http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    throw new ProviderException("timeout", ...);
}
```

生产构造 (`AiCoordinator.cs:476-478`) 两边都取默认 30s。若 scheduler 的 linked token 先取消，provider 透传 OCE，scheduler会重试，最终裸 TaskCanceledException；若 HttpClient.Timeout 先触发，provider 转为 `ProviderException("timeout")`，scheduler只捕获 OCE，因此第一次就停止，重试契约也随机失效。

**不变量**：外部调用方取消必须保留取消语义且不重试；调度器自身超时必须按 priority 执行确定的重试次数，耗尽后统一抛可展示的 timeout 领域异常；生命周期 dispose 取消不得被当成请求超时重试；同一请求只能有一个策略层超时真值。

**精确修复点**：
- `ModelRequestScheduler.ExecuteWithPolicyAsync` (`:134-159`)：显式区分 `job.Ct`、`_loopCts`、本次 timeout CTS；仅本次 timeout 触发重试，耗尽后转换为统一 `ProviderException("timeout", 用户文案, inner)` 或新增 Core 层 timeout 异常（若不允许 Core 依赖 provider 具体异常，则定义 Core 契约异常）。
- `OpenAiCompatibleModelProvider` 构造器 (`:24-34`) 与 `AiCoordinator.RebuildChatPipeline` (`:464-480`)：生产聊天链路禁用/放宽 HttpClient.Timeout，由 scheduler 独占截止时间；连接测试仍可保留独立 timeout。不能简单让两个值继续相等。
- 当前 diff 新并入 `_loopCts` 后，`ExecuteWithPolicyAsync:153` 会把 dispose 取消误判成可重试超时，且 backoff 只观察 `job.Ct`；修复过滤器必须排除 `_loopCts.IsCancellationRequested`。

**测试**：改写现有耗尽测试，断言统一 timeout code/message 与 3 次调用；P1/P2 断言统一 timeout 且仅 1 次；新增外部取消不转换、dispose 中止不重试、模拟 provider 自身 timeout 异常仍遵守唯一策略的测试。当前 scheduler/provider 测试分别覆盖了两个局部行为，却没有集成竞争条件。

**风险**：若只翻译最后一个 OCE，仍保留双计时器，会继续出现“有时重试、有时不重试”；若把所有 OCE 当超时，会破坏用户取消和 dispose。严重度：Important。

### I15 - Important - 未修

**当前状态**：问题成立，相关源码和测试均无 diff。`FileJsonStore.WriteFile` (`JsonStore.cs:76-87`) 对所有 10 类状态文件直接 `File.WriteAllText(target, content)`，并静默吞掉 IOException。

**根因**：目标文件以 create/truncate 方式原地覆盖。进程崩溃、磁盘/文件系统故障或中途写失败可留下空/半截 JSON；读取侧普遍捕获 `JsonException` 后返回 null/默认值，用户数据因此表现为静默重置。

**不变量**：任一时刻 target 要么是上一份完整内容，要么是新一份完整内容，绝不能可见半写内容；临时文件必须与 target 同目录并最终清理；替换失败时旧 target 保持可读；失败必须可诊断，不能静默伪装成功。

**精确修复点**：将 `JsonStore.cs:76-87` 的唯一 `WriteFile` 改为同目录唯一临时文件写入，flush/close 后原子替换：目标存在时用 `File.Replace`（必要时保留/删除 backup），不存在时原子 move；`finally` 删除临时文件。并发写入需要每个 target 串行化或唯一 temp 名，避免 writer 相互覆盖。IOException 至少写正式日志或向调用层返回失败，不应继续空 catch。所有 `Save*` 已汇聚此函数，无需逐项修。

**测试**：在 `JsonStoreTests` 增加“替换后无 temp/backup 残留”“目标不存在首次写”“故障发生时旧 JSON 保持完整”“并发保存最终文件始终可解析”。要可靠测故障路径，建议提取可注入的原子文件 writer；仅做高频循环测试不能证明崩溃安全。

**风险**：Windows 上目标文件被占用时 `File.Replace` 会失败；跨卷 temp 会丧失原子性，所以 temp 必须同目录。当前静默 catch 还会让磁盘满/权限错误不可观测。严重度：Important。

### I16 - 已修，审查文档状态过时

**当前状态**：当前 HEAD 已正确实现，不是工作区 diff。`IntimacyEngine.cs:43-45` 为：

```csharp
var missedDays = Math.Max(0, (today - lastDate).Days - 1);
var decayed = State.Value - missedDays;
var afterDecay = missedDays > 0 ? Math.Max(DecayFloor, decayed) : Math.Max(0, decayed);
```

因此自然低值 0 在同日对话只加基础 2，不会先被抬到 5；只有 `missedDays > 0` 的真实衰减才应用 floor。

**根因（原问题）**：无条件 `Math.Max(DecayFloor, decayed)` 混淆“衰减下限”和“全局最小值”。当前实现已将条件编码在一处。

**不变量**：未发生衰减时保持原值再结算增长；发生自然衰减时不低于 5；最终值限制 0..100。

**精确修复点**：无需源码修复。应更新 `docs/windows-code-review-2026-08.md` 的 I16 状态，但本任务只读未改文件。

**测试**：`IntimacyTests.cs:20-28` 已以 value=0、同日对话期望 2 覆盖原回归；`:78-84` 覆盖长期缺席触底后再 +2 得 7。建议补一个更直接命名的 `Decay_NaturalLowValue_IsNotRaisedWithoutMissedDays`，以及 value 0/3 且 missedDays>0 的产品语义确认测试。

**风险**：当前“低于 5 且确有 missed day”仍会被抬到 5，再加本轮增长；这与“衰减地板”字面仍有产品歧义，但与当前注释明确的“地板仅作用于真实衰减”一致。严重度：已解决/文档陈旧。

### I17 - Important - 未修

**当前状态**：问题成立，相关源码和测试无 diff。`SpriteLoader.cs:23` 使用无上限 `Dictionary<string, SpriteSheet>`，`Cache` (`:100-106`) 永久保留解码后的 sheet。唯一逐出是同 slug 再导入时 `SaveLocal:47-54`；宠物删除时 `PetWindowManager.Reconcile:58-65` 只关闭窗口并移除窗口字典，不逐出 sprite。

**根因**：缓存没有容量/字节预算/LRU，也没有与宠物生命周期关联的 eviction API。自定义导入用唯一 pet instance ID (`PetWindowManager.cs:153-180`)，因此每次导入产生新 key，进程生命周期内无法自然覆盖或回收。

**不变量**：缓存持有的解码像素总量必须有确定上界；正在显示/近期使用的 sheet 应保留；逐出只影响内存缓存，不删除落盘精灵；并发 `TryGet/Load/SaveLocal/evict` 必须线程安全。

**精确修复点**：
- `SpriteLoader.cs:22-23, 36-44, 100-106`：将 Dictionary 改为受 `_cacheLock` 保护的有界 LRU（优先按估算解码字节预算，而非仅条目数；`SourceWidth * SourceHeight * 4` 可作为主要成本），命中提升 recency，插入后逐出最旧非 pinned 项。
- 增加明确 `Evict(slug)`；在 `PetWindowManager.Reconcile:58-65` 删除实例后，仅当没有剩余实例引用同 `SpriteSlug` 时调用。若采用纯 LRU，可不绑定删除，但仍需硬上限。
- 若 sheet 后续持有 disposable native 资源，逐出时同步 Dispose；当前 `SpriteSheet` 是托管数组，不需 Dispose。

**测试**：`SpriteLoaderTests` 增加超过预算后最旧项被逐出、命中更新 LRU、替换同 key 不增加计数、并发加载不越界；manager 测试覆盖共享 slug 删除一只不误逐出、最后引用删除才逐出。现有测试只证明“加载会缓存”。

**风险**：只按条目数限制会被超大自定义图片绕过；加载并发目前还可能对同 slug 重复 decode，峰值内存高于稳态预算；错误逐出共享 catalog slug 会增加磁盘 decode/网络开销但不应影响正确性。严重度：Important。

## Architecture

I14 链路为 `AiCoordinator` 创建 provider 和 scheduler -> `ChatPipeline` 入队 -> scheduler 创建 linked cancellation token -> provider 同时受 token 与自己的 HttpClient.Timeout 控制。当前两层策略重叠，异常类型反过来决定 scheduler 是否重试。

I15 的全部产品 JSON 持久化都经 `IJsonStore` 的 `FileJsonStore.WriteFile`，因此这是单一修复点；读取侧以默认值容错，放大了截断数据的静默丢失风险。

I16 是 Core 纯领域逻辑，当前不变量已在 `RecordConversation` 内集中实现且由 Core 测试覆盖。

I17 的 `SpriteLoader` 是 App 级单例（`App.xaml.cs:56`），被窗口、设置预览和浮球共享，因此缓存寿命等于整个进程；逐出策略应留在 loader，宠物生命周期只提供可选提示。

## Current Diff

相关范围内只有：
- `M windows-native/src/DesktopPet.Core/Scheduling/ModelRequestScheduler.cs`：C1 worker pool、启动锁、dispose cancellation 改动；未修 I14。
- `M windows-native/tests/DesktopPet.Core.Tests/SchedulerTests.cs`：C1 并发/优先级测试；I14 旧断言未改。

I15、I16、I17 源码和对应测试均无当前 diff。整个工作区另有无关修改和未跟踪审查/子代理文件，本次未触碰。

## Validation

- `dotnet test windows-native/tests/DesktopPet.Core.Tests/DesktopPet.Core.Tests.csproj --filter "FullyQualifiedName~SchedulerTests|FullyQualifiedName~IntimacyTests|FullyQualifiedName~JsonStoreTests" --no-restore`：通过 40/40。该结果确认当前行为，但 I14 测试固化裸取消异常、I15 缺少故障覆盖。
- `dotnet test windows-native/tests/DesktopPet.App.Tests/DesktopPet.App.Tests.csproj --filter "FullyQualifiedName~SpriteLoaderTests" --no-restore`：通过 1/1，仅覆盖缓存填充。
- `git diff --check -- windows-native`：通过，无 whitespace error。

## Start Here

先打开 `windows-native/src/DesktopPet.Core/Scheduling/ModelRequestScheduler.cs` 的 `ExecuteWithPolicyAsync`。I14 同时影响用户可见错误、重试确定性和当前 C1 diff 新增的 dispose 取消语义，是 I14-I17 中风险最高、也最容易因局部修补留下竞态的一项。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "逐项核实 I14-I17，给出精确源码/测试路径、Important 严重度、根因、不变量、修复点、测试与风险；I14/I15/I17 未修，I16 已修且文档过时。"
    }
  ],
  "changedFiles": [],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "dotnet test windows-native/tests/DesktopPet.Core.Tests/DesktopPet.Core.Tests.csproj --filter FullyQualifiedName~SchedulerTests|FullyQualifiedName~IntimacyTests|FullyQualifiedName~JsonStoreTests --no-restore",
      "result": "passed",
      "summary": "40/40 passed"
    },
    {
      "command": "dotnet test windows-native/tests/DesktopPet.App.Tests/DesktopPet.App.Tests.csproj --filter FullyQualifiedName~SpriteLoaderTests --no-restore",
      "result": "passed",
      "summary": "1/1 passed"
    },
    {
      "command": "git diff --check -- windows-native",
      "result": "passed",
      "summary": "no whitespace errors"
    }
  ],
  "validationOutput": [
    "I14 current tests pass while explicitly asserting TaskCanceledException, proving the broken public timeout contract remains encoded.",
    "I15 roundtrip tests pass but no atomic-write or failure-preserves-old-data test exists.",
    "I16 current implementation and tests preserve natural low values when no decay occurs.",
    "I17 sole test confirms cache population only; no bound or eviction behavior exists."
  ],
  "residualRisks": [
    "I14 dual 30-second timers race, so retries and user-visible exception type are nondeterministic; current dispose cancellation can also enter retry handling.",
    "I15 process interruption can leave truncated JSON which loaders silently convert to defaults, causing data loss.",
    "I17 decoded sprite memory remains unbounded for repeated custom imports; concurrent duplicate loads can increase peak memory.",
    "I16 behavior for pre-existing values below floor when missedDays > 0 is consistent with current comments but may need product confirmation."
  ],
  "noStagedFiles": true,
  "diffSummary": "Read-only review. Relevant current diff changes scheduler concurrency/disposal and scheduler tests for C1 only; no I14 contract fix and no I15-I17 diff.",
  "reviewFindings": [
    "important: windows-native/src/DesktopPet.Core/Scheduling/ModelRequestScheduler.cs:147 - I14 timeout exhaustion leaks raw cancellation; competing provider timeout makes retry behavior nondeterministic.",
    "important: windows-native/src/DesktopPet.Core/Storage/JsonStore.cs:76 - I15 overwrites targets non-atomically and swallows IOException, permitting silent data loss.",
    "resolved: windows-native/src/DesktopPet.Core/Care/IntimacyEngine.cs:43 - I16 already conditions DecayFloor on actual missed days; review document is stale.",
    "important: windows-native/src/DesktopPet.App/Rendering/SpriteLoader.cs:23 - I17 decoded-sheet cache has no capacity or lifecycle eviction."
  ],
  "manualNotes": "No files were modified except this required report artifact. docs/windows-code-review-2026-08.md itself is currently untracked."
}
```
