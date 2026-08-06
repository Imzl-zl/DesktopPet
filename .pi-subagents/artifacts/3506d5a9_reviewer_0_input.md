# Task for reviewer

Perform an independent read-only code review of the current uncommitted Windows localization implementation for Important/Critical issues only. Review DesktopPet.Core/I18n/I18n.cs, all four Core Resources/i18n*.json catalogs, App/Localization/WpfLocalizer.cs and LanguageCoordinator.cs, App.xaml.cs language publication, and localized WPF surfaces Settings/Chat/FloatingBall/Welcome/SpritePreview/Pet/Tray/Danmaku plus AiCoordinator/HotkeySettingsCoordinator/PersistenceErrorPresenter. Requirements: English, Simplified Chinese, Traditional Chinese, Vietnamese; persist before publish; immediate refresh of all currently visible static UI; user/model content, prompts, logs, style keys never translated; formatted placeholders preserved; no mixed-language page. Inspect tests. Do not modify files. Report findings ordered by severity with exact file:line evidence, or explicitly review-passed; include residual native UI smoke gaps.

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