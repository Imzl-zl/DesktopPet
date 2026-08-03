# Task for reviewer

只读审查，不要修改文件。范围仅限当前任务新增的悬浮球拖拽修复：`windows/src/floating-ball.ts` 中约 188-297 行的 Pointer Capture / 手动 setPosition 逻辑，以及 `windows/src/floating-ball.test.ts` 新增的回归测试。不要把工作区中其他未提交改动算作本任务问题。

合同：修复 Windows Tauri `startDragging()` 偶发吞掉 mouseup，导致悬浮球松手后仍跟随鼠标、下一次点击才停止；保留左键短点击打开菜单、拖动后持久化最终位置；避免 hidden fallback 和竞态。

请检查正确性、Pointer Capture 生命周期、异步任务/最终位置持久化、DPI/跨屏风险、测试有效性。输出 findings，按 Critical/Important/Minor，含文件行和具体修复建议。若无问题明确说明，并指出残余手工验证缺口。

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