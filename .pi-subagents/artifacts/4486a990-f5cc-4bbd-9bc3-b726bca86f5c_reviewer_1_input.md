# Task for reviewer

[Read from: C:\sudy\github\DesktopPet\windows\src\quick-bubble.ts, C:\sudy\github\DesktopPet\windows\src\pet-window.ts, C:\sudy\github\DesktopPet\windows\src\pets.ts, C:\sudy\github\DesktopPet\windows\src\roam\pause.ts, C:\sudy\github\DesktopPet\windows\src\roam\types.ts, C:\sudy\github\DesktopPet\windows\src\roam\modes.ts, C:\sudy\github\DesktopPet\windows\src\settings.ts, C:\sudy\github\DesktopPet\windows\settings.html, C:\sudy\github\DesktopPet\windows\src\i18n.ts, C:\sudy\github\DesktopPet\windows\src\styles.css, C:\sudy\github\DesktopPet\windows\src\performance-contract.test.ts, C:\sudy\github\DesktopPet\windows\src\*quick*.test.ts, C:\sudy\github\DesktopPet\windows\src\pets.test.ts, C:\sudy\github\DesktopPet\windows\src\roam\*.test.ts]

Read-only design/regression review of the current DesktopPet Windows diff. Focus on quick-bubble expiry/config, per-instance wander pause persistence/UI/mode transitions, i18n and settings behavior, idle performance changes, test completeness, and unintended behavior changes. Inspect actual current code and tests. Do not edit. Return only concrete findings ordered by severity with file/line references; explicitly say if no blocking findings.

---
**Output:**
Write your findings to exactly this path: C:/Users/zhanglu/AppData/Local/Temp/desktop-pet-final-config-review.md
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