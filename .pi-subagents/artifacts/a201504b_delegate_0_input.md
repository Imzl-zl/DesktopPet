# Task for delegate

Read-only focused code review. Files are EXACTLY:
1) C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/RollingFileLogger.cs
2) .../DiagnosticExporter.cs
3) .../SecretRedactor.cs
4) C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/App.xaml.cs, only InitializeStore, ExecuteFactoryResetAsync, TryRestartApplication, WaitForParentRestart
5) C:/sudy/github/DesktopPet/windows-native/tests/DesktopPet.Infra.Tests/DiagnosticsTests.cs
Review whether these prior Important issues are fixed: single log line hard max; failed rotation cannot leave null stream and exporter permits rename/delete; secret redaction covers JSON Authorization/Bearer/Basic, client_secret, credential/password, common high entropy; Process.Start failure is user-visible and parent-already-exited/reused PID allows restart; malformed migration JSON is logged and user-visible. Report only remaining Important/Critical with exact lines, or review-passed. Do not modify and do not run tests. Use at most 10 tool calls.

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