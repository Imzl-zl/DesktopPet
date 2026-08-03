# Task for reviewer

[Read from: C:\sudy\github\DesktopPet\windows\src\pet.ts, C:\sudy\github\DesktopPet\windows\src\pet-window.ts, C:\sudy\github\DesktopPet\windows\src\roam\engine.ts, C:\sudy\github\DesktopPet\windows\src\roam\physics.ts, C:\sudy\github\DesktopPet\windows\src\roam\environment.ts, C:\sudy\github\DesktopPet\windows\src\bubble.ts, C:\sudy\github\DesktopPet\windows\src\floating-ball.ts, C:\sudy\github\DesktopPet\windows\src\popover.ts, C:\sudy\github\DesktopPet\windows\src\settings.ts]

Read-only performance review of the Windows Tauri frontend runtime. Scope: windows/src/pet.ts, pet-window.ts, roam/**, bubble.ts, floating-ball.ts, popover.ts, settings.ts. User requests a performance audit of the current working tree, particularly transparent WebView2 desktop pet windows. Identify only evidence-backed continuous CPU/GPU wakeups, high-frequency IPC, image/canvas work, duplicate timers, and lifecycle leaks. Inspect code directly; do not edit files, do not run subagents. Report findings by Critical/Important/Minor with exact file:line, trigger/impact, and smallest safe remediation. Also list material validation gaps.

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