# Task for reviewer

你是独立高级代码审查员，只读、不得编辑或运行破坏性命令。审查范围仅限以下本次性能改动，工作区其余大量未提交内容是用户原有工作，不能当作本次问题：
1. `windows/src/roam/window.ts` 中新增的 `cachedLogicalPos`、`cachedScaleFactor`、`pendingPositionRead`、`startPositionTracking` 与 `setLogical` 缓存更新。
2. 新增 `windows/src/roam/window.test.ts`。
3. `windows/src-tauri/src/lib.rs` 点击穿透循环中新增的 `visible_wins`、`is_visible().unwrap_or(true)` 和仅在可见宠物存在时读取 cursor 的改动。
4. `windows/src/performance-contract.test.ts` 中新增 hidden-pet 测试与 `nativeWindowCode` 读取。

实施目标/契约：依据 Tauri v2 官方文档，用 `onMoved` 和 `onScaleChanged` 替代每 30ms 的 `outerPosition`/`scaleFactor` 轮询；用户拖拽、程序移动及跨 DPI 显示器必须保持正确坐标；已知隐藏宠物不应参与每 60ms 光标与几何命中测试；若官方 visibility 查询失败，维持原先的安全交互行为。不得修改漫游速度、物理规则、攀爬功能或用户设置。

已运行且通过：`npm --prefix windows test`（8 文件/35 测试）、`npm --prefix windows run build`、`cargo check --manifest-path windows/src-tauri/Cargo.toml`、`cargo test --manifest-path windows/src-tauri/Cargo.toml`、`git diff --check`。Tauri 官方参考：https://v2.tauri.app/reference/javascript/api/namespacewebviewwindow/ 与 https://docs.rs/tauri/latest/tauri/webview/struct.WebviewWindow.html。

请检查正确性、竞态/生命周期、API 语义、测试质量和性能是否真实改善。按 Critical / Important / Minor 输出带 file:line 的发现；最后明确是否可以合并。

---
**Output:**
Write your findings to exactly this path: C:\sudy\github\DesktopPet\.pi-subagents\review-performance-cache.md
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