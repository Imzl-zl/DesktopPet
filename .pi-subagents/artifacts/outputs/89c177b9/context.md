# I4 Provider 设置只读调查交接

## 结论摘要

I4 是真实的 Important 级凭据完整性与功能缺口。当前 UI 没有连接测试；模型编辑器在 `ApiKeyRef` 为空时固定生成 `model-key`，多个外部/历史连接会共享并互相覆盖同一 Credential Manager 项。凭据接口没有删除、枚举/前缀清理和失败契约，Windows 实现还忽略 `CredWrite` 返回值，因此现状无法实现可验证、可重试、可中断的迁移。

运行时 `AiCoordinator` 已有保存后配置的 revision + immutable generation，但它不适合未保存表单的 `/models` 测试：测试必须从当前控件草稿构造临时 `ProviderConfig` 和临时 secret 覆盖，异步执行并具备独立 cancellation/generation，且不得调用 `ApplyProviders`、不得写 `providers.json` 或 Credential Manager。

## Review Findings

### Important: 新连接固定 `model-key` 导致跨连接 secret 共享/覆盖

- `windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:2081-2089`：选择当前/第一条配置；API Key 用普通 `TextBox`，已有凭据时把 `（已配置，留空不修改）` 直接作为控件文本。
- `SettingsWindow.cs:2140-2150`：`cfg.ApiKeyRef` 为空就固定用 `model-key`；输入非占位文本便 `creds.Set(keyRef, keyBox.Text)`。任何两个空 ref 配置最终都指向同一 target `DesktopPet/model-key`，后保存者覆盖前者。
- `SettingsWindow.cs:2146-2165`：新建配置 ID 也固定为 `model`，UI 当前通常只能编辑一个连接，但导入/历史 `providers.json` 可含多条，因此共享风险在兼容配置中成立。`ProvidersFileModel.Normalize` 不检查重复 `Id` 或重复 `ApiKeyRef`（`ModelContracts.cs:166-180`）。
- `SettingsWindow.cs:2232-2236`：生图同样固定 `image-key`；虽不同于 `model-key`，仍不以连接身份派生，未来多生图连接会复现。
- `ProviderConfig` 的注释只要求 ref，不定义唯一性（`windows-native/src/DesktopPet.Core/Scheduling/ModelContracts.cs:109-123`）。任务清单已指定目标 schema 为 `connection-id-keyref`（`.tasks/windows-important-hardening/tasks/03-settings-provider/TODO.csv:3`）。

建议 invariant：每个连接具有稳定、唯一、不可由名称/BaseUrl 变化而变化的 connection ID；其 secret target 确定性派生为受控前缀（例如 `provider/model/{connectionId}/api-key`）。新连接 ID 使用完整 GUID/稳定随机 ID，不能继续使用 `model`/`model-key`。允许显式无鉴权时 `ApiKeyRef == ""`，但任何非空 ref 必须与连接 ID 一一对应。

### Important: CredentialStore 无法可靠写入、删除或迁移

- `windows-native/src/DesktopPet.Infra/Providers/ProviderConfig.cs:6-11`：`ICredentialStore` 只有同步 `Get`/`Set`，没有 `Delete`，也没有按应用前缀删除能力。
- `ProviderConfig.cs:59-83`：`WindowsCredentialStore.Set` 调用 `CredWrite` 后完全忽略 bool/Win32 error；UI 会继续持久化 JSON ref，即使 secret 实际未写入。
- `ProviderConfig.cs:24-27`：`CredRead` 对“缺失”和读取失败均返回 null，无法区分 not-found、权限/系统错误。
- `ProviderConfig.cs:12-18`：内存实现也无删除，现有测试仅覆盖覆盖写（`ProviderTests.cs:557-565`）。
- UI 保存顺序为先写 credential、后 `_ai.ApplyProviders`（`SettingsWindow.cs:2140-2165`）；但因写失败不可见，不能提供事务语义。反向顺序也不安全，会发布悬空 ref。

建议精确 API：把接口扩为可观察的 `Get`、`Set`、`Delete`（失败抛专用 `CredentialStoreException`，保留 Win32 error，不把 secret 放消息）；如恢复出厂需要，再增加受限的 `DeleteByPrefix`/`EnumerateRefs(prefix)`，不得提供无边界清理。`WindowsCredentialStore` 必须检查 `CredWrite`/`CredDelete` 返回值并用 `Marshal.GetLastWin32Error()` 分类；not-found 对 `Delete` 可视为成功以保证幂等。

### Important: 当前没有 UI 连接测试，且未保存草稿不能走 AiCoordinator runtime

- 正式契约：`IModelProvider.ListModelsAsync` 位于 `ModelContracts.cs:94-103`；架构要求测试按钮调用 `/models` 并分类提示（`docs/windows-architecture.md:98-103`）。
- 实现：`OpenAiCompatibleModelProvider.ListModelsAsync` 位于 `windows-native/src/DesktopPet.Infra/Providers/OpenAiCompatibleModelProvider.cs:159-220`。
- 编辑器只有保存按钮和同步 save handler（`SettingsWindow.cs:2119-2176`），没有测试按钮、状态、取消或 stale-result 防护。
- `AiCoordinator.ApplyProviders` 必然先写 `providers.json` 再变更内存/runtime（`AiCoordinator.cs:213-225`），所以不能用于“测试未保存表单”。
- `AiCoordinator` 的 `_runtimeRevision`/`ReconcileRuntimeAsync` 只协调已保存 App runtime（`AiCoordinator.cs:124-151,213-225,302-346`）；`AsyncGenerationOwner` 保护 in-flight chat runtime（`DesktopPet.Infra/Lifecycle/AsyncGenerationOwner.cs:7-105`），与编辑器请求结果是否仍对应当前草稿是两个问题。

建议调用链与依赖：

1. 从 `SettingsWindow.ShowModelConnectionEditorCore` 拆出可测试的 `ModelConnectionDraft`（BaseUrl、ModelName、existing ApiKeyRef、当前 `PasswordBox.Password`、Capabilities 等）和 `ModelConnectionTestController`/service。
2. 点击“测试连接”时只读取当前控件值，创建临时 connection ID/ref；secret 不落库。用一个仅测试生命周期的 `ICredentialStore` overlay：草稿 secret 命中临时 ref，已有 secret 则委托持久 store，空 secret + 空 ref 表示无鉴权。
3. 通过注入的 `IModelProviderFactory`（或窄接口 `IModelConnectionTester.TestAsync(draft, ct)`）构造 `OpenAiCompatibleModelProvider(draftConfig, overlayCredentials, sharedHttpClient, timeout)`，调用 `ListModelsAsync(ct)`。
4. 编辑器维护 `CancellationTokenSource _testCts` 与单调 `_testGeneration`。每次点击、任一输入变化、保存或关闭窗口都 cancel 前一次并递增 generation；await 后仅当 generation 仍相等且窗口仍活跃才更新状态。caller cancellation 不显示失败。
5. 测试成功显示模型数/可用模型摘要与能力；不得隐式改 `modelBox`、不得保存 secret/config。保存仍为独立明确动作。

App 测试工程当前只引用 Core，并以 linked source 测少量无 UI helper（`windows-native/tests/DesktopPet.App.Tests/DesktopPet.App.Tests.csproj:16-34`），没有 Infra 引用。最小可测结构是把 draft/controller 做成无 WPF helper，App.Tests 增加 Infra project reference或通过注入 fake tester 避免依赖具体 provider；`SettingsWindow` 只做控件绑定。不要尝试直接自动化私有 modal `Window`。

### Moderate: 明文 HTTP 会发送 Bearer secret，UI 无警告/阻断

- `OpenAiCompatibleModelProvider.ApplyAuth` 无论 URI scheme 都加 Bearer（`OpenAiCompatibleModelProvider.cs:277-284`）。`JoinUrl` 只是字符串拼接（`:286-287`），没有 URI/scheme 校验。
- 编辑器示例明确包含本地 `http://localhost` 合法场景，但任意远程 `http://` + secret 会明文传输；当前 UI 无告警。
- 建议统一 URL validator（模型保存与测试共用）：仅允许绝对 `http/https`；`http` 且 host 非 loopback 时，如草稿/既有 ref 有 secret，阻止测试与保存并返回专门 `insecure-transport`；无 secret 可给常驻 warning。loopback HTTP（localhost、127.0.0.0/8、::1）允许。不要仅靠字符串 `StartsWith("http://localhost")`。
- 若产品决定允许远程明文，至少需要显式逐连接确认并持久化决定；这是安全降级，未获批准前建议阻断。

### Moderate: 错误分类不完整，错误文本可能泄露 provider 返回内容/secret

- `ProviderException` 注释只列 auth/timeout/network/invalid-response，但实现还产生 `http`（`ModelContracts.cs:76-87`; provider `:124-135,190-195`），契约与 UI 映射不一致。
- `CompleteAsync` 把完整错误响应 body拼入 exception message（`OpenAiCompatibleModelProvider.cs:124-135`）。上游可能回显请求头、token、URL query 或用户数据；`SettingsWindow.ShowModelConnectionEditor` 又把完整 exception (`ex.ToString()`) 写到 `%TEMP%/desktoppet-ai.log`（`SettingsWindow.cs:2063-2074`）。这是 secret/敏感数据落盘风险。
- `ListModelsAsync` 当前 HTTP 非成功不读 body，较安全但信息不足（`:190-195`）。新 UI 不应显示/记录 raw body、Authorization、草稿 API key、含 userinfo/query 的完整 URL或 inner exception chain。
- 建议错误码至少稳定为：`invalid-url`, `insecure-transport`, `auth`, `timeout`, `network`（含 DNS/TLS/connect，可在安全内部 detail 再分）, `rate-limit`(429), `server`(5xx), `http`(其它 4xx), `invalid-response`, `credential`。caller cancellation 保留 `OperationCanceledException`，不包装成失败。
- Redaction 应在共享边界实现而非仅 UI 文案替换：结构化错误保存 status/code/provider display name；body只保留受限长度且经过 key-value/header/query redactor，默认不进入用户消息。必须覆盖 `Authorization: Bearer ...`、`api_key/api-key/token/access_token` JSON/表单/query、当前草稿 secret 的精确值。日志只记录错误码、status、已清洗 host/path；不记录 URL query。

## 可中断、幂等迁移方案

目标：把历史非空/共享 ref（如 `model-key`、任意相同 ref）迁移到 connection-scoped ref，任何一步崩溃后重启可安全重试；旧配置在新配置原子发布前始终可用。

1. 加载并 Normalize `providers.json`，为每条模型连接确定稳定 connection ID。已有唯一 `Id` 可保留；空/重复 ID 必须生成并先在迁移工作副本中固定。迁移若在发布前中断，重新生成不同 ID 只会留下未引用新凭据，功能不坏；更优方案是用原条目序号 + 规范化旧字段生成确定性迁移 ID，避免垃圾。
2. 对每条非空旧 ref 读取旧 secret。读取失败必须中止且不改 JSON；not-found 作为可报告的悬空 ref，不得静默写空 secret。
3. 派生新 ref `provider/model/{connectionId}/api-key`。若新 ref 已存在且值等于旧值，视为该条已复制；若不存在则写入并读回比对。若已存在但值不同，停止并报告冲突，不能覆盖。
4. 仅当所有需要的 secret 都复制/验证成功后，构造全部 ref 已更新的 `ProvidersFileModel`，通过现有原子 `FileJsonStore.SaveProvidersFile` 一次发布（`JsonStore.cs:346-354`；底层 `AtomicFilePublisher` `:54-109`）。这是迁移提交点。
5. 发布后重新读取并确认 JSON 引用新 refs。随后清理旧 refs，但仅删除不再被模型或 image 配置引用、且属于已知 DesktopPet legacy allowlist/受控前缀的项。共享旧 ref 必须等所有引用迁移完成后删除一次。
6. 清理失败不回滚新 JSON；留下孤儿凭据比悬空 ref 安全。下次启动根据“当前 JSON 已全部使用新格式”进入 cleanup retry，删除 not-found 视为成功。不要引入单独 migration flag 作为第二真值；schema/ref 形状和实际 credential 状态足以判定。

保存单条编辑同样遵循：写/验证新 secret -> 原子保存新 JSON -> runtime generation 发布 -> 若旧 ref 已无引用则 best-effort 删除。JSON 保存失败时删除刚创建且确认无其它引用的新 ref；删除失败记录已清洗诊断并留待 cleanup。绝不能先删除旧 ref。

## 建议测试（精确符号）

### Infra / Core

- `WindowsCredentialStore.Set`: `CredWrite` false 时抛 `CredentialStoreException`，消息/ToString 不含 secret；建议将 native 调用封装成可注入 `ICredentialNative`，避免测试依赖真实 Credential Manager。
- `WindowsCredentialStore.Delete`: 已存在删除、not-found 幂等、其它 Win32 error 可见；前缀清理严格限制 `DesktopPet/provider/`。
- `InMemoryCredentialStore`: `Delete`/冲突/枚举测试，提供 fault-injecting store 给迁移测试。
- 新 `ProviderCredentialMigrator.Migrate`（建议符号）：两个空/同 `ApiKeyRef` 连接得到不同 refs；共享旧 ref 被复制到两条并只在提交后删除；第 N 次 Set 失败、JSON publish 失败、cleanup 失败、每个中断点重跑；目标已存在同值幂等、异值冲突；缺失旧 credential 不发布。
- `ProvidersFileModel.Normalize/Serialize/Deserialize`: duplicate ID 策略、connection-scoped ref roundtrip、旧 schema 兼容；现有 `ProviderTests.cs:526-554` 只覆盖过滤与 camelCase。
- `OpenAiCompatibleModelProvider.ListModelsAsync`: 补 401/403=`auth`、429=`rate-limit`、500=`server`、其它 4xx=`http`、DNS/connect=`network`、header timeout/body timeout=`timeout`、caller cancellation 原样、invalid JSON/data shape=`invalid-response`、Authorization 正确/无 key 无头。现有覆盖仅成功、body timeout、invalid shape（`ProviderTests.cs:449-501`）。
- URL validator：HTTPS、loopback HTTP（localhost/IPv4/IPv6）、远程 HTTP+secret 阻断、无 secret warning、非绝对/非 http(s) 拒绝。
- Redactor：Bearer、JSON/query 常见 key、当前 exact secret、长 body truncation；断言 exception/message/log 不含测试 secret。

### App.Tests / UI helper

- 新 `ModelConnectionTestController.TestAsync`（建议符号）：传入的确是未保存 draft BaseUrl/model/key，而非 `_ai.Providers` 快照；调用一次 `IModelConnectionTester.ListModelsAsync`。
- 草稿 secret 测试后 credential store 和 JSON store均无写入；成功也不隐式保存/改变 model。
- 第二次测试取消第一次；第一次晚到成功/失败不得覆盖第二次结果（generation test）。编辑、关闭、保存时 cancel；取消不显示错误。
- busy 状态禁用测试按钮但允许取消/关闭；完成后恢复；异常分类映射为内联状态，不用 MessageBox。
- `PasswordBox` 为空且已有 ref：测试委托持久凭据；输入新 secret：overlay 优先；空 ref+空 password：无 Authorization。
- 保存 failure ordering：credential write fail 不调用 `ApplyProviders`；providers JSON fail 不发布 runtime，且新 ref cleanup；成功后才关闭窗口/刷新页面。
- 手工 smoke：编辑未保存 URL/key -> 测试成功 -> 取消窗口 -> 重开确认未保存；快速连续测试两个 endpoint；远程 HTTP+key 警告/阻断；401、超时、DNS、TLS、429、5xx 文案；日志和 UI 搜索确认 secret 不出现。

## 实施边界与建议拆分

- Core schema：`windows-native/src/DesktopPet.Core/Scheduling/ModelContracts.cs`（connection identity/ref invariant、错误码契约；若迁移服务需 IO，不放 Core）。
- Infra：`ProviderConfig.cs`（credential contract/Windows implementation/native adapter）、`OpenAiCompatibleModelProvider.cs`（URL/error/redaction）、可新增 provider credential migration/service。
- Storage：`JsonStore.cs` 已提供原子文件发布，无需重写；迁移需要使用 `FileJsonStore.SaveProvidersFile` 作为唯一提交点。
- App：`SettingsWindow.ShowModelConnectionEditorCore` 只负责 UI，测试 orchestration 拆为可测 helper；`AiCoordinator.ApplyProviders` 仅处理已保存配置，不承载 draft test。
- 不要把草稿 secret 塞进 `ProviderConfig.ApiKeyRef`、日志、异常 Data 或静态字段；不要在测试连接时调用 `ApplyProviders`。

## Residual Risks / Open Decisions

- 是否阻断“远程 HTTP + secret”属于安全产品决策；在未批准显式降级前按阻断实现最稳妥，本地 loopback HTTP 保持可用。
- 历史配置若重复 `Id`，单纯用 ID 派生 ref 仍冲突；迁移必须先解决重复 ID，不能只替换 `model-key`。
- Credential Manager 不支持跨 JSON + credential 的真正事务；上述 copy-before-publish + cleanup-after-publish 是可恢复的 saga，允许孤儿 secret，不允许悬空 ref。
- 当前 `ProvidersFileModel.Deserialize` 遇任意 JSON 异常直接返回空模型（`ModelContracts.cs:191-204`），损坏配置会静默丢失视图；虽不必扩张 I4，但迁移前应保证 parse failure 可区分，避免把损坏文件误判为“无迁移”。
- `SettingsWindow.ShowModelConnectionEditor` 的 catch 写完整异常到 temp 日志是现存泄密面；I4 的 redacted errors 必须覆盖这里或移除该 ad-hoc logging。

## Meta-prompt Contract

**Goal**：实现稳定、connection-scoped credentials 的可中断幂等迁移，并在模型编辑器提供使用未保存草稿的异步 `/models` 测试；保存/测试明确分离，错误分类清楚，远程明文 secret 受保护，任何错误/日志不泄露 secret。

**Evidence**：固定 `model-key` 在 `SettingsWindow.cs:2140`；credential API/忽略写失败在 `ProviderConfig.cs:6-11,59-83`；底层 `/models` 在 `OpenAiCompatibleModelProvider.cs:159-220`；保存 runtime generation 在 `AiCoordinator.cs:213-225,302-346`；原子 JSON 发布在 `JsonStore.cs:54-109,346-354`；验收目标见 TODO.csv task 2。

**Success criteria**：每个连接 ref 唯一稳定；旧共享 refs 中断后可重跑且不丢 key；credential 写/删失败可观察；连接测试读取当前表单且不保存；旧请求不能覆盖新结果；HTTP/401/超时/网络/429/5xx/无效响应分类；secret 不出现在 UI、异常或日志；Infra 与 App targeted tests 通过并完成 UI smoke。

**Hard constraints**：API key 只存 Credential Manager；`providers.json` 只存 ref；测试连接不得隐式保存；迁移不得先删旧 secret；Core 保持零 UI/IO；网络不阻塞 WPF UI；本地 loopback 无鉴权 HTTP 保持可用。

**Suggested approach**：先收紧 credential contract/native adapter并写迁移 failure-injection tests；再实现 copy-publish-cleanup migrator；统一 URL/error/redaction；抽出无 WPF draft test controller，最后接入 `PasswordBox`、状态按钮与 cancellation generation。

**Validation**：先跑 `dotnet test tests/DesktopPet.Infra.Tests/DesktopPet.Infra.Tests.csproj --filter Provider` 和新增 migrator/redactor tests，再跑 `dotnet test tests/DesktopPet.App.Tests/DesktopPet.App.Tests.csproj`，最后 `dotnet build DesktopPet.sln -p:Platform=x64` 与设置页真实 UI smoke。测试命令从 `windows-native/` 执行。

**Stop/escalation**：只有“是否允许远程明文 HTTP 携带 secret”需产品决定；其余按安全默认推进。若无法在 Credential Manager native 层稳定模拟 Win32 错误，抽 adapter 后测试 contract，不以真实用户凭据作为自动测试。证据足够时停止，不扩大到生图多连接 UI或全局日志系统重做。

**Resolved assumptions**：当前 UI 没有新增多模型入口，但外部/历史 providers.json 可有多条，因此共享 ref 是兼容性真实缺陷；`AiCoordinator` runtime generation 可复用设计思想但不能替代表单测试 generation；允许迁移后残留孤儿凭据，不能允许 JSON 指向未验证的 secret。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "review-findings 给出 SettingsWindow.cs:2140 固定 model-key、ProviderConfig.cs:59-83 忽略 CredWrite、OpenAiCompatibleModelProvider.cs:159-220 ListModels 调用链等具体路径/行号与 Important/Moderate 严重度；residual-risks 明列事务、重复 ID、HTTP 决策及静默反序列化风险。"
    }
  ],
  "changedFiles": [
    ".pi-subagents/artifacts/outputs/89c177b9/context.md"
  ],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "dotnet test tests/DesktopPet.Infra.Tests/DesktopPet.Infra.Tests.csproj --filter FullyQualifiedName~ProviderTests --no-restore",
      "result": "failed",
      "summary": "从仓库根执行时相对路径缺少 windows-native，MSBuild 报项目不存在；未进入测试执行。"
    },
    {
      "command": "dotnet test windows-native/tests/DesktopPet.Infra.Tests/DesktopPet.Infra.Tests.csproj --filter FullyQualifiedName~ProviderTests --no-restore",
      "result": "passed",
      "summary": "ProviderTests 33/33 通过，0 失败，0 跳过。"
    },
    {
      "command": "git diff --cached --name-only",
      "result": "passed",
      "summary": "无输出，确认没有 staged files。"
    }
  ],
  "validationOutput": [
    "现有 ProviderTests 定向套件 33/33 通过；本任务未新增或修改产品测试。",
    "git status 显示工作树已有大量用户/并行任务改动；本任务只写指定 context artifact，未触碰或回退既有改动。"
  ],
  "residualRisks": [
    "远程 HTTP + secret 是待确认的安全产品决策，建议默认阻断。",
    "Credential Manager 与 providers.json 无跨资源原子事务，只能采用可恢复 copy-publish-cleanup saga。",
    "重复 connection Id 必须在 ref 派生前迁移，否则仍会共享凭据。",
    "ProvidersFileModel.Deserialize 当前会把损坏 JSON 静默归为空配置，迁移入口需避免误判。"
  ],
  "noStagedFiles": true,
  "diffSummary": "仅新增指定调查交接 artifact；未修改产品代码或测试。",
  "reviewFindings": [
    "important: windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:2140 - 空 ApiKeyRef 固定归一为 model-key，多连接会共享并覆盖 secret。",
    "important: windows-native/src/DesktopPet.Infra/Providers/ProviderConfig.cs:6-11,59-83 - credential contract 无 Delete/失败语义且忽略 CredWrite 返回值，无法可靠迁移。",
    "important: windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:2119-2176 - 编辑器没有异步连接测试；未保存 draft 不能经过会持久化的 AiCoordinator.ApplyProviders。",
    "moderate: windows-native/src/DesktopPet.Infra/Providers/OpenAiCompatibleModelProvider.cs:277-287 - 任意 HTTP endpoint 都可附带 Bearer key，缺少远程明文保护。",
    "moderate: windows-native/src/DesktopPet.Infra/Providers/OpenAiCompatibleModelProvider.cs:124-135 and SettingsWindow.cs:2063-2074 - raw provider body/full exception 可进入异常或 temp 日志，存在 secret redaction 缺口。"
  ],
  "manualNotes": "建议实施者先写 failure-injection migration tests，再动 schema/UI；表单测试使用独立 cancellation generation，成功也不隐式保存。"
}
```
