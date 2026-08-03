# Task for advisor

只读架构咨询，不修改代码。Windows Tauri DesktopPet 当前想用 `onMoved`/`onScaleChanged` 取代每 30ms `outerPosition` + `scaleFactor` 查询。多轮审查发现：把所有 onMoved 当权威会有旧的 programmatic setPosition 事件晚到并覆盖新目标、DPI move/scale 事件乱序等问题。建议的结构是：自动漫游的唯一位置真相为成功 `setLogical` 的目标；只有用户原生拖拽期间才用 Tauri `onMoved` 事件（用当前 `scaleFactor` 计算）更新缓存；开始/结束拖拽和 scale-change 使缓存失效，下一次按需读取一次原生位置。请评估该模型的正确性、边界与最小实现接口；给出是否比事件全时跟踪更稳健的结论。参考 Tauri v2 官方 onMoved/onScaleChanged semantics，中文简短回答、带关键风险。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\position-cache-architecture.md
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