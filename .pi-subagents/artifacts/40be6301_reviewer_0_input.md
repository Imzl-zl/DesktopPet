# Task for reviewer

最终只读复审，禁止编辑。范围仅限 `windows/src/roam/engine.ts`、新 `windows/src/roam/engine.test.ts`、`windows/src/roam/window.ts`、`windows/src/roam/window.test.ts`、`windows/src-tauri/src/lib.rs` 的 visibility helper/loop、`windows/src/performance-contract.test.ts`。忽略所有其他工作区改动。

特别验证刚完成的最终修复：`resolveReleaseContext` 先按初始模式 fetchEnvironment；首次 await 后若当前模式变 climb 且初始未带系统窗口则补 fetchEnvironment(true)；无论是否补读，返回前调用 getConfig 获取最终配置，handleDragRelease 只能按该 config 选 applyFall/applyThrow。`engine.test.ts` 用真实异步 deferred 测试 wander -> climb -> wander 的两次 await 翻转，必须最终返回 wander。请检查是否存在仍会按过期配置走错误物理的路径，或不必要地在稳定非climb枚举系统窗口。

也确认之前的 position ownership / visibility retry 不回归。报告 Critical/Important/Minor 含 file:line；若无问题，明确 ready。最近验证：目标 Vitest 27/27、npm build、cargo test（Rust retry 1/1）、diff check。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-position-ownership-final4.md
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