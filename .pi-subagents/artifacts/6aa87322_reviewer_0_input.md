# Task for reviewer

最终只读审查。仅限 `windows/src/roam/window.ts`、`windows/src/roam/window.test.ts`、`windows/src-tauri/src/lib.rs` 的窗口可见性循环、`windows/src/performance-contract.test.ts`；忽略全部其他既有工作区改动，禁止编辑。

必须验证以下最终契约：
1. Tauri v2 `onMoved` 以当前 native `scaleFactor()` 换算物理事件，跨 DPI 的 moved-before-scale 与 scale-before-moved 都不得产生错误缓存或额外 outerPosition 读取。
2. 初始读取、任意数量/顺序的 setLogical、moved/scale 事件之间不会让旧位置覆写最新逻辑位置。
3. Tauri listener 返回 rejection 或同步 throw 时，半注册 listener 清理，随后可重试。
4. `is_visible` 为 false 时不做 cursor/outer_position/hit-test/persist；错误时强制 `set_ignore_cursor_events(false)`，不做 cursor/geometry。
5. 变更不调整漫游速度、物理、攀爬或用户设置。

验证已重新通过：`npm --prefix windows test` 45/45、`npm --prefix windows run build`、`cargo check --manifest-path windows/src-tauri/Cargo.toml`、`cargo test --manifest-path windows/src-tauri/Cargo.toml`、`git diff --check`。请给 Critical/Important/Minor 行号；若没有阻断问题，明确 ready。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-performance-cache-final3.md
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