# Windows DesktopPet CPU 只读审计

## 结论

**CPU 偏高是可能的，主要发生在多个可见宠物同时处于活动漫游（尤其跟随鼠标）时。** 最可能的根因不是 Canvas 帧率，而是每个宠物窗口独立以 30ms 节奏执行跨 WebView2/Tauri 的窗口 IPC（读位置、读 DPI、设置位置；跟随鼠标还会读鼠标位置）。其成本随宠物数线性放大，最多 12 个实例。[`windows/src/roam/types.ts:53`](C:/sudy/github/DesktopPet/windows/src/roam/types.ts:53)、[`windows/src-tauri/src/lib.rs:35`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:35)

静止窗口仍有确定的原生轮询：点击穿透线程每 60ms 枚举所有宠物并对每个窗口调用 `outer_position`，约 **16.7 次/秒/宠物**；隐藏窗口也没有被过滤。因此，12 个静止或隐藏宠物仍会产生约 **200 次 `outer_position`/秒**，外加全局每秒约 16.7 次光标读取。这是第二优先级的常驻 CPU 来源。[`windows/src-tauri/src/lib.rs:607`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:607)、[`windows/src-tauri/src/lib.rs:617`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:617)、[`windows/src-tauri/src/lib.rs:624`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:624)、[`windows/src-tauri/src/lib.rs:637`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:637)

## 可证实发现

### 高风险：活动漫游的高频窗口 IPC，按宠物数线性增长

`TICK_MS = 30`，即活动循环理论上约 **33.3 tick/秒/宠物**。每一轮先获取环境，再读取当前位置；实际移动时再设置窗口位置。[`windows/src/roam/engine.ts:117`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:117)、[`windows/src/roam/engine.ts:121`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:121)、[`windows/src/roam/engine.ts:130`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:130)、[`windows/src/roam/engine.ts:158`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:158)、[`windows/src/roam/types.ts:53`](C:/sudy/github/DesktopPet/windows/src/roam/types.ts:53)

`currentLogicalPos()` 串行调用 `scaleFactor()` 和 `outerPosition()`，所以活动状态固定带来约 **66.7 IPC/秒/宠物**；移动后 `setPosition()` 再增加约 **33.3 IPC/秒/宠物**。[`windows/src/roam/window.ts:14`](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:14)、[`windows/src/roam/window.ts:26`](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:26)

环境的显示器查询在每个 WebView 上有 500ms 缓存，故约 **2 IPC/秒/宠物**，而不是 33.3 次；缓存为模块变量，不能跨宠物窗口共享。[`windows/src/roam/environment.ts:34`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:34)、[`windows/src/roam/environment.ts:37`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:37)、[`windows/src/roam/environment.ts:81`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:81)

量化（不含点击穿透线程和 Canvas）：

| 模式 | 连续移动时每宠物的可证实 IPC 上限近似值 | 12 宠物近似值 | 说明 |
| --- | ---: | ---: | --- |
| 普通漫游 `wander` | 约 **102 次/秒** | 约 **1,224 次/秒** | `currentMonitor` ~2 + `scaleFactor`/`outerPosition` ~66.7 + `setPosition` ~33.3 |
| 攀爬 `climb` | 约 **105 次/秒** | 约 **1,260 次/秒** | 普通漫游基础上另有 `list_system_windows` 前端 IPC ~2/秒/宠物 |
| 跟随鼠标 `cursor` | 约 **169 次/秒** | 约 **2,028 次/秒** | 普通漫游基础上每 tick 额外 `scaleFactor` 和 `cursorPosition`，约 66.7 次/秒 |
| `stay` 或 roam disabled | **0 个漫游窗口 IPC** | **0** | 引擎仍以 200ms 醒来，但在读取环境/位置之前返回 |

跟随鼠标的额外调用来自每 tick 的 `scaleFactor()` 与 `cursorPosition()`；这是三个模式中最重的路径。[`windows/src/roam/modes.ts:43`](C:/sudy/github/DesktopPet/windows/src/roam/modes.ts:43)、[`windows/src/roam/modes.ts:46`](C:/sudy/github/DesktopPet/windows/src/roam/modes.ts:46)、[`windows/src/roam/modes.ts:48`](C:/sudy/github/DesktopPet/windows/src/roam/modes.ts:48)

**最小可行优化方向：** 在漫游引擎内缓存本窗口的 scale factor（监听 DPI/显示器变更时失效），并让位置成为引擎持有的逻辑状态，而不是每 30ms 通过两个 IPC 回读。`setPosition` 后直接更新该状态；仅在拖拽开始、拖拽结束、显示器切换和疑似外部移动时读取原生位置。此改动保留 30ms 的视觉/物理步长，但把正常 `wander` 的窗口 IPC 从约 102/秒/宠物降为接近 33.3/秒/宠物；`cursor` 则可同时复用 scale factor，降为约 66.7/秒/宠物（`cursorPosition` + `setPosition`）。

### 中风险：攀爬的系统窗口查询已明显减负，但 IPC 仍按窗口数增长

当前未提交修改已经让 `fetchEnvironment` 仅在 `mode === "climb"` 时请求系统窗口；普通漫游和跟随鼠标现在传入 `false`，返回空窗口列表。这一项已解决，**不应再将“普通漫游枚举所有系统窗口”报告为当前问题**。[`windows/src/roam/engine.ts:119`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:119)、[`windows/src/roam/environment.ts:81`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:81)、[`windows/src/roam/environment.ts:95`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:95)、[`windows/src/performance-contract.test.ts:35`](C:/sudy/github/DesktopPet/windows/src/performance-contract.test.ts:35)

攀爬模式自身仍在每个 renderer 以 500ms TTL 调用一次 `list_system_windows`，即约 **2 IPC/秒/宠物**。Rust 端 150ms 全局缓存限制真实 `EnumWindows` 至理论最多约 **6.7 次/秒/进程**，并复用 `Arc`，因此昂贵的 Win32 枚举已经受控；但多宠物仍会产生约 `2N` 次/秒的命令调度、JSON 序列化和 WebView 消息传输。[`windows/src/roam/environment.ts:51`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:51)、[`windows/src/roam/environment.ts:58`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:58)、[`windows/src-tauri/src/sys_windows.rs:16`](C:/sudy/github/DesktopPet/windows/src-tauri/src/sys_windows.rs:16)、[`windows/src-tauri/src/sys_windows.rs:105`](C:/sudy/github/DesktopPet/windows/src-tauri/src/sys_windows.rs:105)

**最小可行优化方向：** 不必先改 Rust 的枚举缓存。把系统窗口快照作为 native 端共享数据，按 500ms 事件推送或由单一前端协调者拉取后广播给攀爬宠物，先消除每个 WebView 的重复 invoke/反序列化。若只处理最显著 CPU，优先处理上一项的位置 IPC。

### 中风险：静止、隐藏和“休息”状态仍保留常驻原生轮询

点击穿透线程没有退出条件，且 `webview_windows()` 后只按 `pet-` 前缀过滤，不检查可见性或鼠标接近度。每 60ms 对每个宠物调用 `outer_position()`，每约 1.02 秒又为持久化再次读取每个宠物位置；`set_ignore_cursor_events` 本身仅在内外状态翻转时调用，这一点是正确的。[`windows/src-tauri/src/lib.rs:612`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:612)、[`windows/src-tauri/src/lib.rs:624`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:624)、[`windows/src-tauri/src/lib.rs:637`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:637)、[`windows/src-tauri/src/lib.rs:654`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:654)、[`windows/src-tauri/src/lib.rs:671`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:671)、[`windows/src-tauri/src/lib.rs:684`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:684)

按代码频率，单个宠物静止时：

- 约 **16.7 次 `outer_position`/秒/宠物**，用于点击穿透。
- 约 **1 次额外 `outer_position`/秒/宠物**，用于保存位置。
- **16.7 次/秒/进程** 的共享 `cursor_position`。
- `stay`/禁用漫游的 JS 引擎每 200ms（5Hz）执行一次 `tick`，但在环境和位置 IPC 前返回，故不加重漫游 IPC。[`windows/src/roam/engine.ts:103`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:103)、[`windows/src/roam/engine.ts:161`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:161)

隐藏宠物仍被该 native 线程处理，故“隐藏所有桌宠”不保证移除这部分 CPU。这个风险可从 `set_desktop_pets_visible` 只调用 `hide()`、而轮询仍无可见性判断直接证实。[`windows/src-tauri/src/lib.rs:554`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:554)、[`windows/src-tauri/src/lib.rs:624`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:624)

**最小可行优化方向：** 点击穿透循环跳过 `is_visible() == false` 的宠物，并在隐藏时设置一次 ignore 状态/清理缓存。对可见但 `stay` 的宠物，可将 60ms 改成自适应：鼠标在窗口扩展边界外时按 200-250ms 检查，接近边界后恢复 60ms；需要先确认透明区点击穿透响应是否仍符合产品要求。

### 中风险：注释宣称的睡眠节流未被控制流兑现

引擎注释称睡眠/静止使用 200ms 并“跳过多数 IPC”，但 `sleeping` 没有作为 `tick()` 的提前返回条件。对于 roam 已启用的宠物，代码在每次 tick 的 `wake()` 后仍进入 `stepMode()`；若目标处于 `restUntil` 或攀爬找不到表面，仍会读取环境和窗口位置。`enterSleep()` 后的下一 tick 又会无条件 `wake()`，因此 sleep 姿态不会成为持续的计算节流。[`windows/src/roam/engine.ts:29`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:29)、[`windows/src/roam/engine.ts:109`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:109)、[`windows/src/roam/engine.ts:111`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:111)、[`windows/src/roam/modes.ts:80`](C:/sudy/github/DesktopPet/windows/src/roam/modes.ts:80)、[`windows/src/roam/modes.ts:106`](C:/sudy/github/DesktopPet/windows/src/roam/modes.ts:106)

当 roam 已启用但本 tick 未移动时，循环确实退到 200ms：普通漫游休息期间近似 **12 IPC/秒/宠物**（位置读取 10 + 显示器缓存约 2）；攀爬近似 **14 IPC/秒/宠物**（再加系统窗口 invoke 约 2）。这是比活动状态低得多、但不是注释暗示的“睡眠时无 IPC”。

**最小可行优化方向：** 为真正睡眠状态增加明确提前返回，或将 `wake()` 延后到 `stepMode()` 确定需要移动时；使用一个有限的下一次唤醒时间（如 `restUntil`）来避免 5Hz 空转。该修复需保证拖拽、模式变更和 mood 事件仍能立即调用 `wake()`。

### 低风险：渲染负载存在但被节流，当前不是首要根因

每个宠物 Canvas 使用 `setTimeout` 而非 `requestAnimationFrame`，当前状态帧率为 idle/done 3 FPS、waiting 4 FPS、working/celebrate 8 FPS；宠物窗口自身只设置 idle、done、celebrate，所以通常为 **3 FPS**，庆祝时为 **8 FPS**。这比 30ms 的移动 IPC 低一个数量级。[`windows/src/pet.ts:23`](C:/sudy/github/DesktopPet/windows/src/pet.ts:23)、[`windows/src/pet.ts:277`](C:/sudy/github/DesktopPet/windows/src/pet.ts:277)、[`windows/src/pet.ts:282`](C:/sudy/github/DesktopPet/windows/src/pet.ts:282)、[`windows/src/pet-window.ts:200`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:200)

每个宠物窗口还有 500ms 的 render 定时器（2Hz）。`renderSig` 避免稳定状态下重复气泡 DOM 更新，但每 tick 仍调用 `snugBubble()` 和 `reportHitRect()`，会读取 `clientHeight`/`getBoundingClientRect`；签名不变时不触发 `set_hit_rect` IPC。快速气泡持续 4 秒的期间会以 2Hz 调用 `renderLine`，而该方法对相同文本也是 no-op。[`windows/src/pet-window.ts:185`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:185)、[`windows/src/pet-window.ts:227`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:227)、[`windows/src/pet-window.ts:231`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:231)、[`windows/src/pet-window.ts:241`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:241)、[`windows/src/pet-window.ts:364`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:364)、[`windows/src/bubble.ts:15`](C:/sudy/github/DesktopPet/windows/src/bubble.ts:15)

CSS 中有透明窗口气泡阴影，但当前宠物 CSS 未定义 `#pet.bob` 的实际动画规则；`applyPet()` 添加该 class 不会产生已定义的连续 bob 动画。现有活跃气泡动画规则针对 working/waiting 行，而当前宠物窗口的单行气泡路径不创建这些结构。因此，不能从当前代码证明存在 idle 状态的 60Hz CSS 动画。[`windows/src/pet-window.ts:75`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:75)、[`windows/src/styles.css:1186`](C:/sudy/github/DesktopPet/windows/src/styles.css:1186)、[`windows/src/styles.css:1260`](C:/sudy/github/DesktopPet/windows/src/styles.css:1260)、[`windows/src/styles.css:1682`](C:/sudy/github/DesktopPet/windows/src/styles.css:1682)

## 已有未提交优化，避免重复报问题

- 普通漫游/跟随鼠标不再调用 `list_system_windows`；仅攀爬请求系统窗口，且有契约测试覆盖。[`windows/src/roam/engine.ts:119`](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:119)、[`windows/src/roam/environment.ts:95`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:95)、[`windows/src/performance-contract.test.ts:35`](C:/sudy/github/DesktopPet/windows/src/performance-contract.test.ts:35)
- `Pet.load()` 对相同 URL 直接返回，并用版本号丢弃过期加载回调，避免设置变更/事件重复解码和 `slice()` 的整张图片像素扫描。[`windows/src/pet.ts:167`](C:/sudy/github/DesktopPet/windows/src/pet.ts:167)、[`windows/src/pet.ts:174`](C:/sudy/github/DesktopPet/windows/src/pet.ts:174)、[`windows/src/performance-contract.test.ts:30`](C:/sudy/github/DesktopPet/windows/src/performance-contract.test.ts:30)
- 当前 `pet-window.ts` 通过缓存实例状态、`renderSig` 和 `lastHitSig` 避免稳定状态的持久化读取、气泡 DOM 重写和重复 `set_hit_rect` IPC；性能契约还确认已移除旧的 120ms `bubble.dirty` 轮询。[`windows/src/pet-window.ts:25`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:25)、[`windows/src/pet-window.ts:188`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:188)、[`windows/src/pet-window.ts:363`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:363)、[`windows/src/performance-contract.test.ts:41`](C:/sudy/github/DesktopPet/windows/src/performance-contract.test.ts:41)、[`windows/src/performance-contract.test.ts:46`](C:/sudy/github/DesktopPet/windows/src/performance-contract.test.ts:46)
- Rust 侧点击穿透已从每窗口锁改为每 tick 单次 hit-rect 快照，`set_ignore_cursor_events` 仅在状态翻转时调用；系统窗口枚举也已有 150ms 进程级缓存。这些都是现有缓解措施，而非新缺陷。[`windows/src-tauri/src/lib.rs:630`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:630)、[`windows/src-tauri/src/lib.rs:654`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:654)、[`windows/src-tauri/src/sys_windows.rs:105`](C:/sudy/github/DesktopPet/windows/src-tauri/src/sys_windows.rs:105)

## 优先级与验证建议

1. **P0 测量并优化活动漫游位置 IPC。** 在 Tauri command 边界为 `scaleFactor`、`outerPosition`、`setPosition`、`cursorPosition` 计数，分别用 1/4/12 个宠物运行 60 秒，比较 `stay`、wander、cursor、climb。预期 cursor > wander ≈ climb（CPU/IPC），且总量近似线性增长。
2. **P1 验证并降低点击穿透常驻轮询。** 隐藏全部桌宠后，用 WPA/Windows Performance Recorder 或 ETW 观察 `outer_position`/Win32 `GetWindowRect` 调用是否仍约 16.7N/秒；修复后应接近零。可见静止宠物再验证鼠标从透明区穿入精灵时的点击命中延迟。
3. **P1 修正 sleep 状态的提前返回后增加行为测试。** 断言 sleep 时不调用 `fetchEnvironment`/`currentLogicalPos`，拖拽、mood 和设置变更可立即唤醒；现有性能测试只做源码字符串契约，未覆盖该运行时不变量。
4. **P2 用 Chromium 性能轨迹验证渲染。** 观察 12 宠物静止时 Canvas `drawImage` 是否约 36 FPS 总计、render 定时器约 24Hz 总计；检查透明 WebView2 合成是否仍是主线程/ GPU 热点。静态审计不能量化 WebView2/DWM 的实际 CPU，需在目标 Windows、实际 DPI 和显卡驱动下取样。

## 验证记录

- `cd windows && npm test`：通过，7 个测试文件、30 个测试通过。
- `cd windows && npm run build`：通过，TypeScript 检查和 Vite 构建完成。
- 初次尝试 `npm test -- --runInBand` 失败：Vitest 4 不支持该 Jest 参数；随后按项目脚本原样运行并通过。
- 仅做静态审计与短时测试/构建；未启动开发服务器、Tauri 应用或其他长期进程。
- 未修改项目源码。工作区在审计开始前已有大量未提交变更；本报告已将其中已实现的性能缓解与仍存风险分开处理。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "已按 wander/cursor/climb/stay 区分高频循环、IPC、渲染来源，提供具体频率、严重级别及文件行号；reviewFindings 与 residualRisks 已列出。"
    }
  ],
  "changedFiles": [
    ".pi-subagents/artifacts/outputs/3e93be98/.pi-subagents/cpu-audit.md"
  ],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "cd windows && npm test -- --runInBand",
      "result": "failed",
      "summary": "Vitest 4 不支持 --runInBand；未执行测试。"
    },
    {
      "command": "cd windows && npm test",
      "result": "passed",
      "summary": "7 个测试文件、30 个测试通过。"
    },
    {
      "command": "cd windows && npm run build",
      "result": "passed",
      "summary": "tsc --noEmit 与 vite build 通过。"
    },
    {
      "command": "git diff --check",
      "result": "passed",
      "summary": "未输出 whitespace 错误。"
    }
  ],
  "validationOutput": [
    "静态审计覆盖 windows/src/roam/、windows/src/pet-window.ts、windows/src/pet.ts、windows/src-tauri/src/lib.rs 及 sys_windows.rs。",
    "未启动长期进程；未采集目标 Windows 上的 ETW/CPU 实测数据。"
  ],
  "residualRisks": [
    "活动 wander/cursor/climb 的窗口 IPC 按宠物数线性放大；12 个 cursor 宠物理论约 2,028 次/秒漫游 IPC。",
    "隐藏宠物未被 native 点击穿透轮询排除，仍约 16.7 次 outer_position/秒/宠物。",
    "sleep 标志未形成 tick 提前返回，已启用漫游且不能移动时仍有 5Hz 环境/位置查询。",
    "实际 CPU 还取决于 WebView2、DWM、GPU、显示器 DPI 和系统窗口数量，静态分析不能替代目标机 trace。"
  ],
  "noStagedFiles": true,
  "diffSummary": "未修改项目源码；仅生成指定 CPU 审计产物。已识别未提交差异中普通漫游跳过系统窗口枚举、重复 spritesheet 解码防护和 120ms 气泡轮询移除等既有优化。",
  "reviewFindings": [
    "high: windows/src/roam/engine.ts:117 与 windows/src/roam/window.ts:14 - 活动 wander 每宠物约 102 次/秒窗口 IPC，按实例数线性增长。",
    "high: windows/src/roam/modes.ts:43 - cursor 每 tick 额外读 DPI 与鼠标位置，活动状态约 169 次/秒/宠物，为最重模式。",
    "medium: windows/src-tauri/src/lib.rs:612 - 点击穿透无限循环对每个宠物每 60ms 读取位置，隐藏窗口同样参与。",
    "medium: windows/src/roam/engine.ts:109 - sleeping 在下一 tick 被 wake，不能持续阻止环境/位置查询。",
    "correct: windows/src/roam/engine.ts:119 - 当前未提交优化已限制系统窗口枚举仅用于 climb；不得将普通漫游枚举视为现存问题。"
  ],
  "manualNotes": "报告基于当前含未提交变更的工作区。审计请求为只读，除用户指定的审计产物外未写入项目文件。"
}
```
