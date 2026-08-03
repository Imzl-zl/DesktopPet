# Task for reviewer

Read-only post-change review of the current Windows performance optimization diff only. Inspect windows/src/settings.ts, pet-window.ts, pet.ts, popover.ts, roam/environment.ts, roam/engine.ts, and performance-contract.test.ts. Verify that targeted Tauri emitTo updates preserve settings/popover/pet behavior; that no global event or sync path needed for instance configuration was accidentally removed; that skipping system window enumeration outside climb preserves roam and drag-release behavior; and that URL/cached-instance changes cannot leave a stale sprite or size. Review against Tauri v2 events API semantics and the user constraint: do not change WebView2 internals, visibility behavior, drag behavior, or tick rate. Do not edit files. Report only evidence-backed Critical/Important/Minor findings with file:line and smallest safe fix.

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