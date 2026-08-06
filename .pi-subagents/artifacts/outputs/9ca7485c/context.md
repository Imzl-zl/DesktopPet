# I3 快捷键只读调查上下文

## 结论

I3 属于结构性缺口，不只是补一个设置控件。当前四个快捷键只在 `App.xaml.cs` 启动时逐个硬编码注册，`bool` 失败结果被忽略；`HotkeyManager.Register` 重绑时先销毁旧注册，因此新注册失败会直接丢失旧快捷键。要满足“4 动作可解绑、JSON 兼容、重复校验、完整候选集事务注册、失败恢复旧集、错误反馈”，最小可靠边界是：Core 持久化完整四动作配置，Infra 以“整套替换”作为唯一变更原语，App 协调运行时注册和 JSON 提交，SettingsWindow 只维护草稿与显示明确结果。

## Review findings

1. **high** — `windows-native/src/DesktopPet.App/App.xaml.cs:367-388` `RegisterGlobalHotkeys`：四个组合在启动代码中硬编码，且四次 `Register(...)` 返回值全部丢弃。外部程序占用某个组合时，应用会带着部分集合继续运行，用户无从知道哪个动作不可用。此项正是 `docs/windows-code-review-2026-08.md:24` 的 I3。
2. **high** — `windows-native/src/DesktopPet.Infra/Hotkey/HotkeyManager.cs:63-78` `HotkeyManager.Register`：同动作重绑先 `Unregister`/删除两张映射，再尝试新注册；新注册失败后旧注册和旧映射都不恢复。单动作 API 无法保证完整四动作配置的一致性。
3. **high** — `HotkeyManager.cs:81-95`：`Unregister`/`UnregisterAll` 忽略底层注销失败却无条件清映射。实现事务回滚时必须保留并报告注销/恢复失败，否则内存视图可能与 Win32 实际注册集不同。
4. **medium** — `HotkeyManager.cs:15-36`：底层抽象只返回 `bool`，`Win32HotkeyRegistration` 虽声明了 `SetLastError=true`，但没有在 P/Invoke 后立即捕获 `Marshal.GetLastWin32Error()`。因此无法把 `ERROR_HOTKEY_ALREADY_REGISTERED (1409)` 与其他注册错误明确反馈给用户。
5. **medium** — `windows-native/src/DesktopPet.Core/Storage/AppSettings.cs:11-28,38-55,57-81`：`AppSettings` 没有快捷键字段、默认值或旧 JSON 归一化入口；四动作目前没有持久化来源。`FileJsonStore.LoadSettings/SaveSettings` 已在 `JsonStore.cs:248-264` 使用 camelCase + normalize，适合沿用。
6. **medium** — `windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:157-167,220-235`：导航和页面分发没有快捷键页；`SettingsWindow` 构造函数也没有运行时快捷键服务/回调。架构明确要求“全局快捷键常显在设置页（可自定义）”，见 `docs/windows-architecture.md:227`。
7. **medium** — 当前没有整套候选的重复检查。两个动作配置成同一个 `(modifiers, virtualKey)` 时，第二次 Win32 注册会失败；由于启动端忽略结果，会静默得到部分集合。
8. **low** — `HotkeyManager.cs:45-47` 当前未使用 `MOD_NOREPEAT (0x4000)`。按住显隐/模式快捷键可能连续收到 `WM_HOTKEY`，导致状态快速反复。建议注册时统一附加该位，但重复比较和 JSON 不应持久化该基础设施位。

## 当前启动注册与消息分发调用链

1. `App.OnStartup` 在 `App.xaml.cs:83-88` 从 `%APPDATA%/DesktopPet/app-settings.json` 加载并归一化 `AppSettings`；当前快捷键不在 settings 中。
2. 非 bench 启动完成 AI/窗口接线后，`App.xaml.cs:127` 调用 `RegisterGlobalHotkeys()`。
3. `RegisterGlobalHotkeys` 在 `App.xaml.cs:369-391` 创建 0x0、透明、不进任务栏的隐藏 WPF `Window`；`host.Show()` 触发 `SourceInitialized`。
4. `SourceInitialized` 在 `App.xaml.cs:381-384` 取得 HWND，获取 `HwndSource`，安装 `HotkeyHook`，创建绑定此 HWND 的 `HotkeyManager`。
5. `App.xaml.cs:385-388` 逐个注册 TogglePets=`Ctrl+Alt+H`、ToggleMode=`Ctrl+Alt+M`、OpenSettings=`Ctrl+Alt+S`、Quit=`Ctrl+Alt+Q`。Infra 最终走 `Win32HotkeyRegistration.Register` -> user32 `RegisterHotKey`（`HotkeyManager.cs:21-36`）。
6. Windows 向隐藏窗口投递 `WM_HOTKEY (0x0312)`；`HotkeyHook` 在 `App.xaml.cs:394-415` 用 `wParam` 的注册 id 调 `_hotkeys.Resolve`（映射在 `HotkeyManager.cs:53-54,97-99`）。
7. 分发动作：TogglePets -> `PetWindowManager.SetGlobalVisible`；ToggleMode -> `CycleOutputMode`；OpenSettings -> `PetWindowManager.OpenSettings`；Quit -> `Application.Shutdown`（`App.xaml.cs:400-413`）。
8. 退出时 `App.OnExit` 在 `App.xaml.cs:248-256` dispose manager、移除 hook、关闭隐藏 host；该清理链完整，但注销失败目前不可观察。

## 最小结构性实现

### 1. Core：配置是唯一持久化真值

建议新增 `windows-native/src/DesktopPet.Core/Storage/HotkeySettings.cs`：

- `HotkeyGesture`：领域值对象，字段为 `[Flags] HotkeyModifiers`（Control/Alt/Shift/Windows）和 `int VirtualKey`。不要让 Core 引用 WPF `Key` 或 user32；枚举值可由 Infra 显式转换。
- `HotkeySettings`：固定四个 nullable 属性 `TogglePets`、`ToggleMode`、`OpenSettings`、`Quit`。`null` 明确表示该动作解绑。固定属性比 enum-keyed dictionary 更稳：JSON 可读，新增未知 enum 不会令整份 `app-settings.json` 反序列化失败。
- `HotkeySettings.Defaults`：四个现有组合。
- `HotkeySettings.ValidateCandidate()`：校验所有非 null 项；拒绝非法 virtual key、仅修饰键作为主键、没有修饰键（建议安全默认，避免普通字母劫持全局输入），并以规范化后的 `(modifiers, virtualKey)` 检测动作间重复，返回动作对和可展示消息。解绑项不参与重复检查。
- 在 `AppSettings` 最后新增 nullable/可归一化的 `HotkeySettings Hotkeys` 参数；`Defaults` 写默认整套；`Normalize` 对 `raw.Hotkeys == null` 回退 `HotkeySettings.Defaults`。这是现有旧字段兼容模式（`IdleChatterLines` 在 `AppSettings.cs:76-78`、`Ai` 在 `:81` 已如此处理）。

JSON 兼容要点：旧文件完全没有 `hotkeys` 时，System.Text.Json 会给新增引用参数 `null`，Normalize 必须补旧默认；显式 `null` 不能同时表示“全部解绑”，因此“全部解绑”必须序列化为 `hotkeys` 对象且四属性均为 null。保留 `FileJsonStore` 现有 camelCase/enum-string options（`JsonStore.cs:356-361`）。不要在反序列化异常时静默覆盖用户其他设置。

### 2. Infra：整套替换，不从 UI 循环调用单项 Register

在 `windows-native/src/DesktopPet.Infra/Hotkey/HotkeyManager.cs`：

- 将公开变更入口改为 `TryReplaceAll(HotkeySettings candidate)`（或等价 `IReadOnlyDictionary<HotkeyAction, HotkeyGesture?>`），返回结构化 `HotkeyApplyResult`，至少含 success、失败动作/gesture、native error、rollback 是否完整及 rollback 错误。
- manager 必须额外保存当前成功集合的 gesture 快照，不能只保存 id/action 两张表。
- 先对**完整候选集**做领域重复/合法性校验；校验失败时不触碰当前注册。
- 快照旧集合 -> 注销旧集合 -> 按固定动作顺序注册全部非 null 候选到新 id，并暂存在局部映射；只有全部成功才一次性交换 `_byId`/`_byAction`/当前 gesture 快照。
- 任一候选注册失败：注销本轮已成功的新 id，重新注册快照中的完整旧集合并恢复映射；返回失败，不发布候选映射。恢复也可能失败，结果必须明确标为 rollback incomplete，不能声称旧集已恢复。
- `IHotkeyRegistration` 返回包含 native error 的结果；Win32 实现必须紧跟 P/Invoke 捕获 last error。注销也需要可观察结果。
- id 使用有界/可复用分配策略，防止每次编辑让 `_nextId` 无限制越过 `0xFFFF`。注册时可附加 `MOD_NOREPEAT`，但配置比较忽略该位。

Win32 并不提供多热键原子事务，所以“事务”只能是补偿事务。旧集合注销到恢复之间，另一个进程可能抢占旧组合；这时必须把 rollback incomplete 明确反馈并保留 manager 对实际成功注册项的准确映射。

### 3. App：启动使用配置，并协调运行时与持久化

精确位置：`windows-native/src/DesktopPet.App/App.xaml.cs` 的 `OnStartup`、`RegisterGlobalHotkeys`，以及建议新增的 app-layer `HotkeyService`/`HotkeySettingsCoordinator`。

- 把 `App.xaml.cs:127` 改为传入已归一化的 `settings.Hotkeys`；`SourceInitialized` 创建 manager 后调用一次 `TryReplaceAll(完整配置)`，不再四次单项注册。
- 启动注册失败应显示一次明确反馈（失败动作、组合、冲突/系统错误）；允许应用继续运行，但说明哪些快捷键未生效。若采用 rollback-incomplete 状态，反馈实际生效集合。
- 新增轻量应用服务协调“运行时集合 + app-settings.json”：设置提交先让 manager 事务替换运行时整套；失败则不保存。运行时成功后保存新的完整 `AppSettings`；若 `SaveSettings` 抛 `JsonStoreException`，立刻调用 `TryReplaceAll(old.Hotkeys)` 恢复运行时，并沿用 `PersistenceErrorPresenter`。若这一恢复也失败，显示双重错误。
- 这层协调逻辑应可在 `DesktopPet.App.Tests` 用 fake registrar + fake store 单测，而不是把事务散落在 WPF click handler 中。

`PetWindowManager` 当前在 `PetWindowManager.cs:194-200` 创建 `SettingsWindow`，建议沿用现有注入模式：增加 `SetHotkeySettingsHandler`/服务字段，App 在启动时注入，构造设置窗时传入。现有输出模式也是通过 `SetOutputModeHandler` 注入（`:267-275`），可直接仿照。

### 4. SettingsWindow：完整草稿、可解绑、内联错误

精确位置：

- `SettingsWindow.NavigationIcon`：`SettingsWindow.cs:100-112` 增加 hotkeys 图标。
- `BuildNavigation`：`:157-167` 增加“快捷键”页。
- `ShowPage`：`:220-235` 分发到 `BuildHotkeysPage`。
- 构造函数 `:50-58` 接收应用层提交服务/回调。
- 新增 `BuildHotkeysPage` 与捕获控件；不要复用通用 `Save`（`:1899-1959`）直接先落盘，因为快捷键需要运行时事务和持久化补偿。

页面维护四项的**完整 draft**，每行包含动作名、当前组合、录入按钮和清除图标按钮；清除只把该项置 null，点击统一“应用”才提交整套。键盘捕获使用 `PreviewKeyDown`，正确处理 `Key.System`，忽略纯 modifier，`KeyInterop.VirtualKeyFromKey` 仅留在 App/WPF 边界。提交前一次性重复校验，重复项应在对应两行内联标红；OS 占用/注册失败显示页内 danger 状态（至少明确动作和组合），并保持旧集和旧持久化值。按钮/录入框设置 AutomationProperties.Name，确保键盘可达。

## 建议测试

### `windows-native/tests/DesktopPet.Core.Tests/AppSettingsTests.cs`

- `Defaults_HotkeysUseLegacyPresets`：四个默认组合与当前 H/M/S/Q 一致。
- `Normalize_MissingHotkeysUsesDefaults`：`Hotkeys = null!` 模拟旧 JSON。
- `Normalize_PreservesExplicitUnboundActions`：单项 null、四项 null 均保持解绑，不能回填默认。
- `ValidateCandidate_RejectsDuplicateAcrossActions`，并验证解绑不算重复。
- 非法主键/仅 modifier/无 modifier（若采纳该安全规则）的校验。

### `windows-native/tests/DesktopPet.Core.Tests/JsonStoreTests.cs`

- 写入一份**没有 `hotkeys` 字段的真实旧版 app-settings JSON**，`LoadSettings()` 后得到四默认值且其他旧值不变。
- 四动作含部分 null/全部 null 的 save-load roundtrip；断言 camelCase 和 enum string。
- 重复配置 roundtrip 后仍可由验证器报告，不应导致整份 settings 变 null。

### `windows-native/tests/DesktopPet.Infra.Tests/HotkeyManagerTests.cs`

现有 6 个测试（`:40-115`）只覆盖逐项 API，应扩为集合语义：

- 首次完整集合成功并可由每个 id Resolve。
- 候选含解绑项，只注册非 null 项，旧动作确实注销。
- 候选内部重复在任何 P/Invoke 前失败，旧集合完全未动。
- 第 N 个候选注册失败：撤销本轮前 N-1 个，并完整恢复旧四项；Resolve 只映射恢复后的旧 id。
- 首个候选失败、最后一个候选失败分别覆盖边界。
- rollback 自身某项注册失败：结果为 rollback incomplete，manager 映射只包含实际恢复成功项，错误可观察。
- 注销失败路径不静默清映射。
- native error 1409 原样出现在结果中。
- 多次整套替换不越过/耗尽合法 id 范围；Dispose 注销当前实际集合。

### `windows-native/tests/DesktopPet.App.Tests`

建议对抽出的 `HotkeySettingsCoordinator` 测：

- runtime apply 失败 -> 不调用 SaveSettings、返回可展示错误、旧配置不变。
- runtime 成功 + SaveSettings 成功 -> runtime/JSON 同为候选。
- runtime 成功 + SaveSettings 失败 -> 调用 registrar 恢复旧整套，并传播持久化错误。
- 上述补偿恢复也失败 -> 返回组合错误且不伪报成功。
- 启动初始化使用持久化集合，不再使用硬编码 preset。

UI 自动化/手测：分别重绑四动作、逐个解绑、全部解绑、制造重复、使用已被其他应用占用的组合、保存失败；重启确认 JSON 恢复；按住 ToggleMode 验证不会连续循环；退出后确认组合可被其他进程注册。

## 实现约束与已解析假设

- 硬约束：Core 保持零 WPF/零 IO；Win32 转换和错误码留在 Infra/App 边界。
- 硬约束：配置提交单位始终是完整四动作集合；SettingsWindow 不逐项直接调用 `Register`。
- 硬约束：`null` 表示解绑；旧 JSON 缺整个 `hotkeys` 节点才回退历史默认。
- 已解析：继续使用当前四动作和默认 H/M/S/Q，不新增动作。
- 建议假设：主键必须是非 modifier，且至少带一个 modifier；这是防止全局劫持普通输入的安全默认。若产品明确允许裸键，只需放宽验证，不改变结构。
- 风险：Win32 无原子批量 API，恢复旧集是 best effort；rollback failure 必须成为一级错误状态。
- 风险：当前工作树已有大量与本调查无关的未提交改动，包括本次读取的 `App.xaml.cs`、`SettingsWindow.cs`、`PetWindowManager.cs`、`JsonStore.cs`；实施代理必须基于当前内容合并，不能回退用户改动。

## Meta-prompt handoff

**Goal**：实现 Windows I3：四个动作可自定义/解绑并持久化；启动从 JSON 注册；设置提交完整候选集；重复配置在调用 Win32 前拒绝；任一注册失败补偿恢复旧完整集合；启动和设置页都有明确错误反馈。

**Evidence**：硬编码及静默失败在 `App.xaml.cs:367-388`；破坏性单项重绑在 `HotkeyManager.cs:63-78`；设置持久化入口在 `AppSettings.cs:11-81`、`JsonStore.cs:248-264`；设置导航/保存模式在 `SettingsWindow.cs:157-167,220-235,1899-1959`；窗口构造注入点在 `PetWindowManager.cs:194-200,267-275`；架构承诺在 `docs/windows-architecture.md:227`。

**Success criteria**：旧 JSON 保留原设置并得到历史默认热键；部分/全部解绑可 roundtrip；完整候选重复不触碰运行时旧集；第 N 项注册失败后旧集恢复且 Resolve 正确；rollback failure 明确可见；设置保存失败也补偿恢复运行时旧集；启动不再含四次硬编码 Register；窄测和 x64 build 通过。

**Hard constraints**：不让 Core 引用 WPF/user32；不把 API key/新秘密写入文件；不回退工作树现有改动；不要用逐动作 UI 提交模拟事务；不能吞掉注册、注销、rollback 或 JSON 错误。

**Suggested approach**：先建立 Core `HotkeySettings`/验证器，再把 Infra manager 收敛为 `TryReplaceAll`，随后加 App coordinator 和启动接线，最后增加 SettingsWindow 完整 draft 页面与错误状态。让事务/补偿逻辑进入可单测服务，WPF 只做捕获和呈现。

**Validation**：运行 `dotnet test` 的 Core/Infra/App 定向测试，再运行 `dotnet build windows-native/DesktopPet.sln -p:Platform=x64`；最后 Windows 手测冲突组合与重启持久化。若无法运行真实 Win32 冲突测试，至少用 fake registration 精确注入第 N 次失败和 rollback 失败。

**Stop/escalation rules**：只有“是否允许无 modifier 裸键”会改变验证策略；默认按至少一个 modifier 实施即可，不必阻塞。若要求强原子性（rollback 永不失败），应立即说明 Win32 API 无法保证并升级产品决策；否则 best-effort 补偿 + 明确 degraded 状态已足够。

## 调查验证

- `dotnet test windows-native/tests/DesktopPet.Infra.Tests/DesktopPet.Infra.Tests.csproj --filter FullyQualifiedName~HotkeyManagerTests --no-restore`：通过 6/6。
- `dotnet test windows-native/tests/DesktopPet.Core.Tests/DesktopPet.Core.Tests.csproj --filter "FullyQualifiedName~AppSettingsTests|FullyQualifiedName~JsonStoreTests" --no-restore`：通过 19/19。
- `git diff --cached --quiet`：通过，无 staged 文件。
- 本任务未修改产品源码或测试；只写本调查产物。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "context.md 的 Review findings 给出 8 项带 severity、精确路径/行号/符号的发现，并包含 residual risks、调用链、最小结构方案和测试路径。"
    }
  ],
  "changedFiles": [
    ".pi-subagents/artifacts/outputs/9ca7485c/context.md"
  ],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "dotnet test windows-native/tests/DesktopPet.Infra.Tests/DesktopPet.Infra.Tests.csproj --filter FullyQualifiedName~HotkeyManagerTests --no-restore",
      "result": "passed",
      "summary": "现有 HotkeyManagerTests 6/6 通过。"
    },
    {
      "command": "dotnet test windows-native/tests/DesktopPet.Core.Tests/DesktopPet.Core.Tests.csproj --filter FullyQualifiedName~AppSettingsTests|FullyQualifiedName~JsonStoreTests --no-restore",
      "result": "passed",
      "summary": "现有 AppSettings/JsonStore 定向测试 19/19 通过。"
    },
    {
      "command": "git diff --cached --quiet",
      "result": "passed",
      "summary": "无 staged 文件。"
    }
  ],
  "validationOutput": [
    "Infra hotkey baseline: 6 passed, 0 failed.",
    "Core settings/store baseline: 19 passed, 0 failed.",
    "只读调查未修改产品源码或测试。"
  ],
  "residualRisks": [
    "RegisterHotKey/UnregisterHotKey 不支持批量原子事务；补偿恢复期间旧组合可能被其他进程抢占，因此 rollback 必须允许并报告 incomplete。",
    "支持无 modifier 裸键的产品策略未在文档定义；建议默认拒绝以避免劫持普通输入。",
    "工作树已有大量无关未提交改动，后续实施必须增量合并且不得回退。"
  ],
  "noStagedFiles": true,
  "diffSummary": "仅新增只读调查产物 context.md；无产品代码或测试变更。",
  "reviewFindings": [
    "high: windows-native/src/DesktopPet.App/App.xaml.cs:367 - 四动作硬编码且忽略所有注册失败。",
    "high: windows-native/src/DesktopPet.Infra/Hotkey/HotkeyManager.cs:63 - 重绑先销毁旧注册，新注册失败不会恢复旧动作。",
    "high: windows-native/src/DesktopPet.Infra/Hotkey/HotkeyManager.cs:81 - 注销失败被忽略并清除内存映射，无法可靠回滚。",
    "medium: windows-native/src/DesktopPet.Core/Storage/AppSettings.cs:11 - 无快捷键持久化模型、默认兼容或重复校验。",
    "medium: windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:157 - 设置导航与页面没有可自定义/解绑入口。"
  ],
  "manualNotes": "调查基于当前脏工作树；App.xaml.cs、SettingsWindow.cs、PetWindowManager.cs、JsonStore.cs 已有用户/其他任务改动。"
}
```
