# Task for reviewer

最终只读复审，禁止编辑。仅检查本轮性能优化相关局部改动：`windows/src/roam/window.ts`、`windows/src/roam/engine.ts`、`windows/src/roam/window.test.ts`、`windows/src-tauri/src/lib.rs` 的 `apply_ignore_state` 和点击穿透 visibility loop、`windows/src/performance-contract.test.ts`。忽略该工作区中所有其他已有改动。

必须确认：
1. 正常自动漫游中 setLogical 成功目标是坐标真相，晚到 onMoved 不可覆盖；仅 native drag 期间 onMoved 可更新，drag begin/end 和 DPI 变更失效缓存并允许按需原生读取。
2. 旧 async 读取/事件不会写回过期位置；监听注册失败可重试且半注册清理。
3. known-hidden 不 cursor/outer_position/hit-test/persist；visibility 错误安全设为 interactive；任何 `set_ignore_cursor_events` 失败均不更新 `last_ignore`，下一轮重试。检查 `apply_ignore_state` 两个调用点。
4. 不改变漫游速度、物理、攀爬与设置语义。

报告 Critical/Important/Minor（带 file:line），没有问题则明确 ready。现有验证：Vitest target 25/25、Rust unit 1/1、cargo check/test、npm build 先前通过。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-position-ownership-final2.md
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