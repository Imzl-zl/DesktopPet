## Review
- Correct: `windows/src/roam/window.ts:24-33` 使用的 Tauri v2 API 和负载类型正确。已核对本地 `@tauri-apps/api` 2.11.0 声明：`onMoved` 的 payload 为 `PhysicalPosition`，`onScaleChanged` 的 payload 含新的 `scaleFactor`；两者均覆盖官方文档列出的跨显示器 DPI 变更场景。
- Correct: `windows/src-tauri/src/lib.rs:629-636` 先以 `WebviewWindow::is_visible()` 筛除已知隐藏窗口，仅当 `visible_wins` 非空时读取 cursor；`windows/src-tauri/src/lib.rs:631` 的 `unwrap_or(true)` 会把 visibility 查询错误放回旧的命中检测路径。已核对 Tauri 2.11.2 Rust API，该 getter 返回 `Result<bool>`。因此隐藏窗口不会再执行 `outer_position`、矩形命中或 `set_ignore_cursor_events`，而 visibility 查询失败仍保持安全交互行为。
- Critical: `windows/src/roam/window.ts:45-49` 的首次缓存读取不是原子操作，也没有版本/代次保护。它先取得旧 `scaleFactor`，再等待 `outerPosition`；在两次 await 之间，窗口跨 DPI 显示器时 `onScaleChanged` 会在 `:31-34` 写入新比例并清空缓存，随后 `onMoved` 可在 `:24-30` 用新比例写入正确坐标。悬挂的读取最终仍会在 `:47-48` 用旧比例覆写这个新缓存。之后 `currentLogicalPos()` 在 `:41` 直接返回该错误缓存，不再轮询纠正，漫游/拖拽会基于错误逻辑坐标运行，违反跨 DPI 坐标正确性契约。示例：读取捕获 scale=1；切换到 scale=1.5 后 move 事件报告 physical `(300,450)` 并正确缓存 `(200,300)`；旧读取返回相同 physical 坐标后却缓存 `(300,450)`。应在 move/scale 事件递增代次，并仅在读取开始和结束代次相同才提交读取结果；否则重读或返回事件已写入的缓存。
- Important: `windows/src/roam/window.ts:21-34` 在订阅 Promise 尚未成功前就将 `trackingStarted` 固定为 `true`，并以 `void` 丢弃两个 Promise。任一监听注册失败（例如窗口销毁期间或 IPC/权限错误）会产生未处理 rejection，且后续所有 `currentLogicalPos()` 都不会再尝试注册监听，退化为永久缓存而不是可恢复的事件跟踪。应保留/await 注册任务，捕获失败并允许后续调用重试；若模块并非与窗口同寿命，也应保留 unlisten 生命周期。
- Minor: `windows/src/roam/window.test.ts:31-84` 未覆盖 Critical 中的读请求与 move/scale 事件交错的顺序，也未覆盖任一监听注册拒绝。当前 scale 测试在触发事件前完成初始读取，不能证明跨 DPI 迁移期间缓存不会被旧读取覆写。
- Minor: `windows/src/performance-contract.test.ts:51-56` 只是源文本断言，未执行或模拟 Rust 的 `is_visible=false` 和 `is_visible=Err` 分支，也未断言 `cursor_position` 在全隐藏时未调用。它能防止明显结构性回退，但不能验证所声明的性能和安全行为。
- Note: `windows/src-tauri/src/lib.rs:629-636` 的优化在全隐藏或部分隐藏时真实省去了 cursor 和隐藏窗口的几何/命中调用；但每个宠物每 60ms 新增一次同步 `is_visible()` 查询。因此全部窗口可见时，原有的 1 次 cursor + N 次 geometry 变为 N 次 visibility + 1 次 cursor + N 次 geometry。该成本符合当前“已知隐藏窗口跳过命中”的限定契约，但不构成全部可见场景的性能改善，应在性能结论中明确其适用范围。

结论：**不可合并**。先修复 `window.ts` 的跨 DPI 读取/事件竞态并补充可控交错测试；建议同时处理监听注册失败的生命周期。Rust 可见性筛选的目标行为和安全回退可以保留。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "已给出带严重级别和精确 file:line 的 4 项发现，并以本地 Tauri 2.11 API 声明、实际差异及新增测试执行结果为依据。"
    }
  ],
  "changedFiles": [
    "windows/src/roam/window.ts",
    "windows/src/roam/window.test.ts",
    "windows/src-tauri/src/lib.rs",
    "windows/src/performance-contract.test.ts"
  ],
  "testsAddedOrUpdated": [
    "windows/src/roam/window.test.ts",
    "windows/src/performance-contract.test.ts"
  ],
  "commandsRun": [
    {
      "command": "npm --prefix windows test -- --run windows/src/roam/window.test.ts windows/src/performance-contract.test.ts",
      "result": "failed",
      "summary": "Vitest 从 windows 工作目录解析筛选路径，给定的 windows/src 前缀导致未找到测试文件；未执行任何测试。"
    },
    {
      "command": "npm --prefix windows test -- --run src/roam/window.test.ts src/performance-contract.test.ts",
      "result": "passed",
      "summary": "2 个测试文件、12 个测试通过。"
    },
    {
      "command": "git diff --check -- windows/src/roam/window.ts windows/src-tauri/src/lib.rs",
      "result": "passed",
      "summary": "无空白错误。"
    }
  ],
  "validationOutput": [
    "本地 @tauri-apps/api 2.11.0 声明确认 onMoved payload 为 PhysicalPosition，onScaleChanged 在跨 DPI 显示器移动时发出。",
    "本地 tauri 2.11.2 源码确认 WebviewWindow::is_visible() 返回 Result<bool>。",
    "新增 TypeScript 测试在正确项目相对路径下通过。"
  ],
  "residualRisks": [
    "Critical: 首次 scaleFactor/outerPosition 异步读取可在跨 DPI 事件后以旧比例覆写正确缓存，且不再轮询恢复。",
    "监听注册失败被忽略且 trackingStarted 锁定，事件跟踪不能恢复。",
    "可见窗口场景额外执行每窗口 is_visible()；性能收益限于至少一个宠物隐藏，尤其是全部隐藏时。",
    "Rust 可见性分支仅由文本测试覆盖，未执行 false 和 error 行为测试。"
  ],
  "noStagedFiles": true,
  "diffSummary": "指定范围将位置读取改为事件驱动缓存，并在 Rust 点击穿透循环中跳过已知隐藏宠物的 cursor/geometry 命中工作。",
  "reviewFindings": [
    "critical: windows/src/roam/window.ts:45 - 异步初始读取可在 onScaleChanged/onMoved 后用旧 scaleFactor 覆写正确缓存，跨 DPI 坐标会持续错误。",
    "important: windows/src/roam/window.ts:21 - 监听订阅 Promise 被丢弃且 trackingStarted 过早锁定，失败会产生未处理 rejection 并禁止重试。",
    "minor: windows/src/roam/window.test.ts:31 - 缺少初始读取与 DPI/move 事件交错、监听注册失败的测试。",
    "minor: windows/src/performance-contract.test.ts:51 - 仅检查源码文本，未验证隐藏和 visibility-error 的 Rust 行为。"
  ],
  "manualNotes": "审查严格限定于请求列出的性能改动；未将工作区其他未提交改动纳入发现。结论不可合并，原因是跨 DPI 核心坐标契约存在可复现的异步竞态。"
}
```