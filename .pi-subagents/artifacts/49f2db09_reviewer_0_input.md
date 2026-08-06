# Task for reviewer

Perform an independent read-only code review of current uncommitted Windows I5/I15 hardening for Important/Critical issues only. Review Infra/Diagnostics (AppDataPaths, SecretRedactor, RollingFileLogger, DiagnosticExporter, AtomicFileWriter, FactoryResetService), ProviderConfig credential prefix cleanup, AgentHost/Program and AgentService logging, App diagnostics ProcessMetricsMonitor and Settings About UI, App factory-reset orchestration/restart, Fullscreen suppression detector/monitor/policy and ModeService, SpriteLoader/ImportSprite/remove cleanup, and AiCoordinator daily-summary persistence ordering. Requirements: logs only AppData logs root, bounded rolling and secret-free ZIP; accurate sampled CPU/current working set and timer disposal; confirmed sandboxed idempotent reset stops Agent/requests, closes log handles, deletes app root and DesktopPet credentials, then reliably restarts; fullscreen multi-monitor/DPI and maximized/borderless detection suppresses in-flight proactive/danmaku but leaves capture/analysis running; persistence failures observable and old state preserved/compensated. Inspect tests. Do not modify files. Report findings ordered by severity with exact file:line evidence, or explicitly review-passed; include residual real-machine gaps.

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