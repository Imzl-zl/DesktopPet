# Task for reviewer

最后一次只读复审，禁止编辑。仅审查：`windows/src/roam/window.ts`、`windows/src/roam/window.test.ts`、`windows/src-tauri/src/lib.rs` 中 visible_wins 轮询/持久化、`windows/src/performance-contract.test.ts`。忽略工作区其他既有未提交内容。

最终契约：
- 使用 Tauri v2 官方 `onMoved`/`onScaleChanged`，不再每 30ms 调 `outerPosition`；移动事件需用当前原生 `scaleFactor()` 换算，以正确处理 `onMoved` 早于 `onScaleChanged` 的跨 DPI 顺序。
- 初次读取、移动、缩放和程序 setPosition 的并发不得写入过期逻辑坐标；事件换算 pending 时应避免多余 outerPosition 读取。
- 任一监听注册失败时清理另一条已成功 listener 并允许重试。
- 已知隐藏宠物不执行点击穿透与每秒持久化的 cursor/geometry 查询；visibility 查询失败走安全交互路径。
- 不改变漫游速度、物理、攀爬或用户设置。

验证刚通过：`npm --prefix windows test` 40/40，`npm --prefix windows run build`，`cargo check --manifest-path windows/src-tauri/Cargo.toml`，`cargo test --manifest-path windows/src-tauri/Cargo.toml`，`git diff --check`。请按 Critical/Important/Minor 给出带行号结论，并明确 ready/fixes-required。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-performance-cache-final2.md
This path is authoritative for this run.
Ignore any other output filename or output path mentioned elsewhere, including output destinations in the base agent prompt, system prompt, or task instructions.

## Acceptance Contract
Acceptance level: attested
Completion is not accepted from prose alone. End with a structured acceptance report.

Criteria:
- criterion-1: Return concrete findings with file paths and severity when applicable

Required evidence: review-findings, residual-risks

Finish with a fenced JSON block tagged `acceptance-report` in this shape:
Use empty arrays when no items apply; array fields contain strings unless object entries are shown.
`criteriaSatisfied[].status` must be exactly one of: satisfied, not-satisfied, not-applicable.
`commandsRun[].result` must be exactly one of: passed, failed, not-run.
`manualNotes` and `notes` are optional strings; an empty string means no note and does not satisfy `manual-notes` evidence.
```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "specific proof"
    }
  ],
  "changedFiles": [
    "src/file.ts"
  ],
  "testsAddedOrUpdated": [
    "test/file.test.ts"
  ],
  "commandsRun": [
    {
      "command": "command",
      "result": "passed",
      "summary": "short result"
    }
  ],
  "validationOutput": [
    "validation output or concise summary"
  ],
  "residualRisks": [
    "none"
  ],
  "noStagedFiles": true,
  "diffSummary": "short description of the diff",
  "reviewFindings": [
    "blocker: file.ts:12 - issue found, or no blockers"
  ],
  "manualNotes": "anything else the parent should know"
}
```