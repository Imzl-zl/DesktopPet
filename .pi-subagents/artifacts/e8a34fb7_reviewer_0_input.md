# Task for reviewer

只读复审，不要修改文件。仅审查当前悬浮球修复的这些区域：`windows/src/floating-ball-drag.ts` 全文、`windows/src/floating-ball-drag.test.ts` 全文、`windows/src/floating-ball.ts` 中 FloatingBallDragController 的 Tauri 物理坐标适配和 Pointer Capture 事件绑定、`windows/src/floating-ball.test.ts`。忽略当前工作区其余未提交改动。

合同：修复 Windows Tauri 原生 startDragging 偶发吞 mouseup 后仍跟随鼠标；保留短左键菜单、拖动后最终位置持久化；支持混合 DPI 多显示器，不混用逻辑坐标与物理窗口坐标；无隐性错误吞没。首轮审查已指出逻辑/物理坐标混用及测试仅字符串断言，现已改为 PhysicalPosition + cursorPosition 和可测试控制器。

请验证：Pointer Capture 生命周期、physicalGrabOffset 假设、异步 cursor/setPosition 最终持久化顺序、错误传播、单测是否真正覆盖。输出仅 Critical/Important/Minor findings；无问题请明确说明残余原生手工验证缺口。

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