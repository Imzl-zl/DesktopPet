# Task for reviewer

Independent read-only Important/Critical review of current uncommitted Windows product-hardening and I15 closeout. Focus only on: windows-native/src/DesktopPet.Infra/Diagnostics/*.cs; Infra/Providers/ProviderConfig.cs credential DeleteAll; AgentHost/Program.cs and Agent/AgentService.cs logging; App/Diagnostics/ProcessMetricsMonitor.cs, App/Fullscreen/FullscreenSuppression.cs, Ai/ModeService.cs, App.xaml.cs factory reset/restart/fullscreen wiring, SettingsWindow About diagnostics/reset UI, Rendering/SpriteLoader.cs, Windows/FloatingBallWindow.cs, Windows/PetWindowManager.ImportSprite, and AiCoordinator daily summary publication. Validate requirements: bounded rolling redacted logs and secret-free ZIP; no model/user content or secrets in logs; sampled CPU/current working set and timer disposal; reset confirmation, Agent/request stop, log handles close, sandbox root, DesktopPet credential prefix cleanup, idempotence, failure reporting and reliable restart; borderless/maximized/negative-coordinate/mixed-DPI fullscreen and in-flight proactive/danmaku suppression while capture/analysis continues; file/JSON persistence compensation and no silent I/O swallowing. Inspect focused tests and run them if useful. Do not modify files. Report findings by severity with exact file:line, or review-passed, plus residual native smoke gaps.

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