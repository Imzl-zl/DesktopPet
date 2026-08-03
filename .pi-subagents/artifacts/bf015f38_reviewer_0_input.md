# Task for reviewer

只读最终复审，不修改文件。范围仅限本次悬浮球修复：`windows/src/floating-ball.ts` 中控制器接入、`windows/src/floating-ball-drag.ts`、`windows/src/floating-ball-pointer.ts`，及三个对应的 `*.test.ts`。忽略工作区其他未提交改动。

合同：Windows Tauri `startDragging()` 偶发丢失 mouseup 导致悬浮球松手后仍跟随鼠标。修复必须：不再调用该 API；保留短左键菜单；Pointer Capture 的 up/cancel/lost-capture 全部安全收尾；使用 Tauri 全局物理 cursorPosition + PhysicalPosition 避免混合 DPI 漂移；最后一次移动完成后才持久化；任何移动错误必须显式传播到页面日志。请只报告 Critical/Important/Minor findings；无此类问题请明确说明。

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