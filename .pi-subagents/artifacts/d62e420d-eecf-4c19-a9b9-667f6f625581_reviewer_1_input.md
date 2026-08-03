# Task for reviewer

[Read from: C:\sudy\github\DesktopPet\windows\src-tauri\src\lib.rs, C:\sudy\github\DesktopPet\windows\src-tauri\src\sys_windows.rs, C:\sudy\github\DesktopPet\windows\src\pet-window.ts]

Read-only performance review of the Windows native/Tauri runtime. Scope: windows/src-tauri/src/lib.rs and related sys_windows.rs plus call sites in windows/src/pet-window.ts. User requests a performance audit of the current dirty working tree. Focus on polling cadence, Win32 calls, mutex/file I/O, background thread lifecycle, Tauri IPC volume, multiple desktop-pet scaling, and logging costs. Do not edit files and do not run subagents. Report evidence-backed findings by Critical/Important/Minor with exact file:line, impact, and smallest safe remediation; list validation gaps.

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