## Review

结论：**fixes required**。本次代次、pending promise、注册失败清理和可见性查询失败的安全路径均正确；但隐藏窗口优化在持久化分支中仍被绕过，未完全满足新增性能契约。

### Critical
- 无。

### Important
- `windows/src-tauri/src/lib.rs:691-694`：`visible_wins` 已在 `:629-632` 排除 `is_visible() == Ok(false)` 的窗口，点击穿透分支也只在 `:646` 遍历该集合；但每约 1 秒的持久化分支仍遍历 `wins`，并对每一个（包括已知隐藏）窗口调用 `window.outer_position()`。这与同文件 `:626-628` 的“known-hidden overlays 跳过所有 cursor and geometry work”注释，以及 `windows/src/performance-contract.test.ts:51` 的测试名称“does not query cursor or geometry for known-hidden pet windows”不一致。隐藏全部宠物时仍会产生每个窗口一次的几何 IPC/原生查询。应使持久化读取也仅处理可见窗口，或明确缩窄并调整性能契约；按当前契约需要修复。

### Minor
- `windows/src/roam/window.test.ts:121-135` 仅验证 `onMoved` 注册失败、`onScaleChanged` 注册成功这一方向，并未验证相反的部分成功方向，也没有断言成功注册返回的 unlisten 函数确实被调用。实现 `windows/src/roam/window.ts:38-49` 对两种结果是对称处理，因此这不是已证实的行为错误；但这是本次专门修复的半注册清理路径的测试缺口。
- `windows/src/performance-contract.test.ts:51-57` 是源文本匹配，不会捕获 `lib.rs:691-694` 这类位于 `visible_wins` 点击穿透循环之外的隐藏窗口几何访问。建议在修复时将断言收紧到实际循环边界，或以可测试的提取函数验证隐藏窗口不调用几何查询。

### Correct
- `windows/src/roam/window.ts:23-49`：Tauri v2 `onMoved`/`onScaleChanged` 的实际类型为 `Promise<UnlistenFn>`。实现同步发起两项注册、以 `Promise.allSettled` 等待结果；任一失败时对另一项已成功注册的监听器执行 unlisten，并在 `finally` 清除 `pendingTracking`。因此不会永久占用注册状态，后续 `currentLogicalPos()` 可重试。
- `windows/src/roam/window.ts:54-77`：首次读取捕获 `generationAtReadStart`，事件处理器在 `:26`、`:34` 递增 `cacheGeneration`。异步 scale/position 读取完成后代次不匹配即不写入旧值，转而返回事件缓存或 `null`，避免旧的 DPI/坐标组合覆盖新事件数据。`pendingPositionRead` 将并发调用合并为一次 native 查询，且调用方获得拷贝，不能篡改缓存。
- `windows/src/roam/window.test.ts:92-119`：可控 deferred 交错测试让初始读取在 `outerPosition` 等待期间收到 scale 和 moved 事件，并验证旧读取不能覆写 `{ x: 200, y: 300 }` 的新 DPI 逻辑坐标。
- `windows/src-tauri/src/lib.rs:629-665`：可见性查询失败使用 `unwrap_or(true)`，即按可见窗口继续命中判断；这保留了查询失败时“不因未知状态而跳过窗口”的安全路径。已知隐藏窗口不进入光标读取/点击穿透的每 tick 几何计算，且 `visible_wins` 为空时不会读取系统光标。

### Validation
- `git diff --check -- windows/src/roam/window.ts windows/src/roam/window.test.ts windows/src-tauri/src/lib.rs windows/src/performance-contract.test.ts`：通过。
- `npm --prefix windows test -- --run src/roam/window.test.ts src/performance-contract.test.ts`：通过，2 个测试文件、14 个测试。
- 未重复执行调用方已报告通过的完整 `npm --prefix windows test`、build 与 `cargo check`；本次只读复审已从本地依赖源码确认 Tauri v2 API 签名。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "已给出一个 Important 发现（windows/src-tauri/src/lib.rs:691-694）和两个 Minor 测试覆盖发现，均含文件与行号；并列明已核实的正确行为。"
    }
  ],
  "changedFiles": [],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "git diff --check -- windows/src/roam/window.ts windows/src/roam/window.test.ts windows/src-tauri/src/lib.rs windows/src/performance-contract.test.ts",
      "result": "passed",
      "summary": "指定范围内无空白错误。"
    },
    {
      "command": "npm --prefix windows test -- --run src/roam/window.test.ts src/performance-contract.test.ts",
      "result": "passed",
      "summary": "2 个测试文件、14 个测试全部通过。"
    },
    {
      "command": "npm --prefix windows test; npm --prefix windows run build; cargo check --manifest-path windows/src-tauri/Cargo.toml",
      "result": "not-run",
      "summary": "未重复执行调用方已报告通过的完整验证；本次运行了与审查范围直接相关的 Vitest 子集。"
    }
  ],
  "validationOutput": [
    "Tauri v2 前端定义确认 onMoved/onScaleChanged 返回 Promise<UnlistenFn>；Rust 依赖 tauri-2.11.2 确认 WebviewWindow::is_visible 返回 Result<bool>。",
    "指定 Vitest 子集通过：14/14。"
  ],
  "residualRisks": [
    "已知隐藏窗口仍在约每秒一次的持久化分支调用 outer_position，未达到测试名称和代码注释所述的完全跳过 geometry work。",
    "事件注册失败测试未覆盖 onScaleChanged 失败/onMoved 成功及 unlisten 调用。"
  ],
  "noStagedFiles": true,
  "diffSummary": "复审的性能改动为窗口逻辑坐标缓存和事件驱动失效、以及 Tauri 点击穿透循环对已知隐藏窗口的可见性筛选；未修改任何源文件。",
  "reviewFindings": [
    "important: windows/src-tauri/src/lib.rs:691-694 - 持久化分支对已知隐藏窗口仍执行 outer_position，违反隐藏窗口不进行 geometry work 的性能契约。",
    "minor: windows/src/roam/window.test.ts:121-135 - 仅覆盖 onMoved 失败方向，未覆盖反向部分成功和 unlisten 断言。",
    "minor: windows/src/performance-contract.test.ts:51-57 - 静态文本断言无法发现 visible_wins 之外的隐藏窗口几何读取。"
  ],
  "manualNotes": "结论为 fixes required；除上述隐藏窗口持久化查询外，代次防覆写、pending promise 合并、部分成功清理重试和 is_visible 查询失败的安全命中路径均已核实。"
}
```
