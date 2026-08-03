# Task for reviewer

最终只读代码审查，禁止修改。审查范围只限：`windows/src/roam/window.ts`、`windows/src/roam/window.test.ts`、`windows/src-tauri/src/lib.rs` 的 visible_wins 点击穿透及位置持久化循环、`windows/src/performance-contract.test.ts`。不要评论工作区其他既有未提交改动。

目标：按 Tauri v2 官方 API 使用 `onMoved`/`onScaleChanged` 缓存坐标，防止异步读/跨 DPI 事件竞态；监听注册任一失败时清理已成功注册项并重试；已知隐藏宠物完全跳过点击穿透和每秒位置持久化的 cursor/geometry 查询，visibility 查询失败仍走安全的交互路径。不得改漫游速度、物理、攀爬或设置。

已验证：window 单测 7 项通过；performance contract 9 项通过；此前完整 37 个前端测试、生产构建、cargo check 均通过，但在这次最终复审后会重跑。请只报告 Critical/Important/Minor 带行号，确认是否 ready。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-performance-cache-final.md
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