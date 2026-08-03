# Task for reviewer

只读复审本次性能改动，禁止编辑。上次审查发现并已修复两项阻断问题：1) 首次异步 scaleFactor/outerPosition 读取与 DPI/move 事件竞争；现用 `cacheGeneration` 阻止过时读取覆写事件缓存，新增可控交错测试。2) `onMoved`/`onScaleChanged` 订阅失败会永久锁住状态；现用 `Promise.allSettled`，失败清理半注册监听并允许下一次读取重试，新增测试。

严格审查范围：`windows/src/roam/window.ts`、`windows/src/roam/window.test.ts`、`windows/src-tauri/src/lib.rs` 中 visible_wins 点击穿透分支、`windows/src/performance-contract.test.ts` 新增测试。其他工作区改动不是本次范围。

契约：遵循 Tauri v2 官方 `onMoved`/`onScaleChanged`/`WebviewWindow::is_visible` 语义，保留拖拽、程序移动、跨 DPI 坐标正确性和 visibility 查询失败时的安全命中行为；不改漫游速度、物理、攀爬或设置。已有验证：`npm --prefix windows test` 37/37 通过，`npm --prefix windows run build` 通过，`cargo check --manifest-path windows/src-tauri/Cargo.toml` 通过，`git diff --check` 通过。

请特别验证代次、pending promise、事件注册失败/部分成功和隐藏窗口分支。输出中文：Critical / Important / Minor（带文件行号），明确判断 ready 或 fixes required。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-performance-cache-followup.md
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