# Task for reviewer

最终只读审查，禁止编辑。审查范围严格限于：`windows/src/roam/window.ts`、`windows/src/roam/engine.ts`、`windows/src/roam/window.test.ts`、`windows/src-tauri/src/lib.rs` 的 visibility loop、`windows/src/performance-contract.test.ts`。忽略工作区其余既有未提交改动。

架构契约（已由独立咨询确认）：
- Tauri v2 没有保证 onMoved/onScaleChanged 来源或顺序，因此自动漫游的坐标真相只能是成功 `setLogical` 目标；非拖拽时 onMoved 必须不能覆盖缓存。
- 仅在用户原生拖拽期（engine 的 setDragging 显式通知）处理 onMoved，用当前 native scaleFactor 换算；拖拽开始/结束与 DPI 变化均使缓存失效，下一次需求允许执行一次合并 native scaleFactor+outerPosition 读取以恢复正确性。
- initial read、setLogical、native drag event、scale event 不得让过期坐标覆写缓存；自动路径不可每 30ms 调 outerPosition。
- listener rejection/synchronous throw 清理半注册且允许重试。
- known-hidden 不做 cursor/geometry/hit-test/persist；is_visible failure 强制 set_ignore_cursor_events(false)，仅成功后记缓存以重试失败。
- 不改漫游速度、物理、攀爬或设置。

目前局部通过：window unit 13/13、performance contract 11/11、npm build、cargo check。请只报告真实 Critical/Important/Minor（文件:行）；明确 ready/fixes-required。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-position-ownership-final.md
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