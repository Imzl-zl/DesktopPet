# Task for reviewer

[Read from: C:\sudy\github\DesktopPet\windows\src\pet-window.ts, C:\sudy\github\DesktopPet\windows\src\window-drag.ts, C:\sudy\github\DesktopPet\windows\src\pet-pointer-drag.ts, C:\sudy\github\DesktopPet\windows\src\roam\engine.ts, C:\sudy\github\DesktopPet\windows\src\roam\window.ts, C:\sudy\github\DesktopPet\windows\src-tauri\src\lib.rs, C:\sudy\github\DesktopPet\windows\src-tauri\capabilities\default.json, C:\sudy\github\DesktopPet\windows\src\*drag*.test.ts, C:\sudy\github\DesktopPet\windows\src\roam\engine.test.ts, C:\sudy\github\DesktopPet\windows\src\roam\window.test.ts]

Read-only correctness review of the current DesktopPet Windows diff. Focus on pointer-captured pet drag, generic window-drag lifecycle, Rust drag lease and immediate persistence, engine sampling/throw release, DPI/cache races, Tauri permissions, and error paths. Inspect actual current code and tests. Do not edit. Return only concrete findings ordered by severity with file/line references; explicitly say if no blocking findings.

---
**Output:**
Write your findings to exactly this path: C:/Users/zhanglu/AppData/Local/Temp/desktop-pet-final-drag-review.md
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