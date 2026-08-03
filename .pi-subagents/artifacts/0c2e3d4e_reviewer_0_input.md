# Task for reviewer

最终只读复审，禁止编辑。仅审查本轮范围：`windows/src/roam/window.ts`、`windows/src/roam/engine.ts`、`windows/src/roam/window.test.ts`、`windows/src-tauri/src/lib.rs`（apply_ignore_state/visibility loop/测试）、`windows/src/performance-contract.test.ts`。忽略其余工作区改动。

此前复审的唯一 Important 已修复：`handleDragRelease` 现在先按当前配置决定是否请求 system windows，fetchEnvironment 后重新读取配置；若从非climb切换到climb，补一次 fetchEnvironment(true)，再决定 applyFall / applyThrow。请检查此方案是否等价保留原先“环境读取后按最新配置决定释放物理”的语义，且正常非climb不会枚举系统窗口。

也重新确认：自动漫游仅 setLogical 成功目标写缓存，onMoved 只在原生拖拽；缓存/监听竞态安全；known-hidden 不参与 cursor/geometry/hit-test/persist；任何 set_ignore_cursor_events 失败不更新缓存、下轮重试。报告带 file:line 的 Critical/Important/Minor；无问题明确 ready。验证已通过：target Vitest 26/26，npm build，cargo check，cargo test（含 Rust retry unit），diff check。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-position-ownership-final3.md
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