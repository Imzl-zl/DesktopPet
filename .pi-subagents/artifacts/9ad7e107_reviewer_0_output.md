## Review

**结论：fixes-required。** 未发现影响漫游速度、物理、攀爬或用户设置的改动；但下列 Critical/Important 问题使最终性能与并发契约尚未满足。

- Correct: `windows/src/roam/window.ts:46-52` 使用了 Tauri v2 的 `onMoved` 与 `onScaleChanged`，移动处理在 `windows/src/roam/window.ts:27-33` 以当次原生 `scaleFactor()` 换算物理坐标。`windows/src/roam/window.ts:53-64` 对异步注册失败会释放另一成功 listener，并在下一次读取时允许重试；`windows/src/roam/window.test.ts:138-175` 覆盖了两种异步失败组合。
- Correct: `windows/src-tauri/src/lib.rs:629-637,646-675,690-707` 将已知隐藏窗口排除在 cursor 查询、点击穿透的 `outer_position` 查询及每秒位置持久化之外。无可见窗口时不会读取 cursor；已知可见性状态时的失败 cursor 路径会计算为 interactive。`windows/src/performance-contract.test.ts:51-66` 静态断言了这些调用边界。
- Correct: 定向命令 `npm --prefix windows test -- --run src/roam/window.test.ts src/performance-contract.test.ts` 通过，17/17；本次也执行了 `git diff --check`，通过。

- Critical: `windows/src/roam/window.ts:100-103` 直到 `await win.setPosition(...)` 完成后才递增 `cacheGeneration` 并写入缓存。若初始 `currentLogicalPos()` 的 `scaleFactor/outerPosition` 读取在此 `await` 期间返回，读取在 `windows/src/roam/window.ts:77-86` 仍认为 generation 未变，会把 setPosition 前的坐标写进缓存；此时下一次读取可返回过期逻辑坐标。并发的两个 `setLogical` 也可依 Promise 完成顺序由较早请求覆盖较晚请求的缓存。应在发起 setPosition 前使旧读失效，并以请求/代际令牌仅允许当前 setPosition 完成时提交目标坐标。`windows/src/roam/window.test.ts` 没有覆盖“初始读取 + pending setPosition”或乱序 setPosition 完成。

- Critical: `windows/src-tauri/src/lib.rs:629-632` 用 `window.is_visible().unwrap_or(true)` 将可见性查询错误当作可见窗口，随后 `windows/src-tauri/src/lib.rs:646-665` 仍会按 cursor/hit rect 计算，并可调用 `set_ignore_cursor_events(true)`。因此在 visibility 查询失败且 cursor 位于 hit rect 外时，窗口会进入点击穿透，而不是契约要求的安全 interactive 路径。应把 `Err` 单独处理为 `set_ignore_cursor_events(false)`（必要时更新 `last_ignore`），而不是纳入正常 hit-test。现有 `windows/src/performance-contract.test.ts:51-57` 只匹配 `unwrap_or(true)`，恰好未检测此错误语义。

- Important: `windows/src/roam/window.ts:23-42,49-52,72-95` 在跨 DPI 顺序 `onMoved -> onScaleChanged` 中，若 moved 回调的 `scaleFactor()` 尚未完成，`onScaleChanged` 会递增 generation；待 moved 换算完成后因 `generationAtMove !== cacheGeneration` 丢弃该结果。`currentLogicalPos()` 随即从 `pendingMovedPosition` 返回后调用 `outerPosition()`（第 76-92 行）。这违反“事件换算 pending 时避免多余 outerPosition 读取”的契约。`windows/src/roam/window.test.ts:82-96` 仅模拟了 moved 换算在 scale 事件之前完成，未覆盖该挂起交错。应保留/采纳以当前 native scaleFactor 完成的 moved 物理位置，或将 scale 事件与该 pending moved 换算协调，使其不被无条件失效。

- Minor: `windows/src/roam/window.ts:46-53` 仅能处理 listener API 返回 rejected Promise；若 `onMoved` 或 `onScaleChanged` 同步抛错，后续注册/`Promise.allSettled` 清理逻辑不会运行。Tauri API 通常返回 Promise，因此这是低概率桥接异常路径，但“任一监听注册失败”按字面并未完整覆盖。用 `Promise.resolve().then(() => win.onMoved(...))` / `onScaleChanged` 纳入 all-settled 注册流程可完整覆盖；测试也应增加同步 throw 情况。

- Fixed: 无。本次为只读复审，未修改任何源文件。

- Note: `windows/src/performance-contract.test.ts` 是源码文本匹配，无法证明 Rust 的 visibility-error 分支或 TypeScript 异步代际行为。上述关键交错需要行为测试（deferred Promise 和 listener 回调），而非仅静态断言。

- Residual risk: 用户声明的全量 `npm --prefix windows test`、build、Cargo 检查/测试已通过；本次未重跑这些全量命令，只重跑了与审查目标直接相关的 17 个 Vitest 用例。全量通过不覆盖上述未建模的异步交错与 visibility error 路径。