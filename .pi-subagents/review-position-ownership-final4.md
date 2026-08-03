## Review
- Correct: `windows/src/roam/engine.ts:153-169` fulfills the release-context invariant. It samples the initial mode before the first await, calls `getEnvironment(false)` for stable non-climb modes, rereads configuration after that await, and performs the `getEnvironment(true)` supplementation only for a transition into `climb` where the first sample lacked system windows. It obtains `getConfig()` again immediately before returning the context.
- Correct: `windows/src/roam/engine.ts:171-181` chooses `applyFall` versus `applyThrow` exclusively from `context.config`, the final configuration returned by `resolveReleaseContext`. There is no retained pre-await configuration path to select stale climb physics.
- Correct: `windows/src/roam/engine.ts:119` requests system-window enumeration only for `mode === "climb"`; the release path also avoids it for a stable `stay`, `wander`, or `cursor` configuration. A non-climb to climb transition incurs exactly the required supplemental read.
- Correct: `windows/src/roam/engine.test.ts:52-74` uses two real deferred promises and three sequential configuration values (`wander`, `climb`, `wander`). It verifies the first fetch is `false`, waits until the supplemental `true` fetch begins, resolves the second await, and asserts the returned configuration is finally `wander`.
- Correct: `windows/src/roam/window.ts:24-123` preserves position ownership through generation invalidation. Stale native reads and stale drag-scale updates cannot overwrite a newer programmatic/native position. `windows/src/roam/window.test.ts:115-149,203-231` covers stale reads, out-of-order moves, and a stale read racing with a native drag move.
- Correct: `windows/src-tauri/src/lib.rs:55-66,644-730` retains visibility safety and retry behavior. Known-hidden windows bypass cursor/geometry/persistence work; unknown visibility explicitly requests interactive cursor handling; `apply_ignore_state` only records the desired state after the native call succeeds, so failures retry on the next loop. The Rust unit test at `windows/src-tauri/src/lib.rs:813-832` verifies that retry invariant.
- Correct: `windows/src/performance-contract.test.ts:36-100` guards the climb-only enumeration path, final release-mode reread, hidden-window work suppression, and retry helper usage.
- Note: No Critical, Important, or Minor findings in the requested scope. Ready.
- Residual risk: Native Windows `onMoved`, `is_visible`, and cursor-event behavior remains OS/WebView runtime-dependent; this review has unit, type/build, and static-contract evidence but no live Windows GUI integration run.

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "Review findings cite concrete locations in engine.ts, engine.test.ts, window.ts, window.test.ts, lib.rs, and performance-contract.test.ts; no Critical/Important/Minor defects were found."
    }
  ],
  "changedFiles": [
    "windows/src/roam/engine.ts",
    "windows/src/roam/engine.test.ts",
    "windows/src/roam/window.ts",
    "windows/src/roam/window.test.ts",
    "windows/src-tauri/src/lib.rs",
    "windows/src/performance-contract.test.ts"
  ],
  "testsAddedOrUpdated": [
    "windows/src/roam/engine.test.ts",
    "windows/src/roam/window.test.ts",
    "windows/src/performance-contract.test.ts",
    "windows/src-tauri/src/lib.rs"
  ],
  "commandsRun": [
    {
      "command": "npm exec vitest run src/roam/engine.test.ts src/roam/window.test.ts src/performance-contract.test.ts",
      "result": "passed",
      "summary": "3 test files and 27 tests passed."
    },
    {
      "command": "cd windows && npm run build",
      "result": "passed",
      "summary": "TypeScript no-emit check and Vite production build completed."
    },
    {
      "command": "cd windows/src-tauri && cargo test",
      "result": "passed",
      "summary": "Rust retry helper test passed: 1 passed, 0 failed."
    },
    {
      "command": "git diff --check -- windows/src/roam/engine.ts windows/src/roam/engine.test.ts windows/src/roam/window.ts windows/src/roam/window.test.ts windows/src-tauri/src/lib.rs windows/src/performance-contract.test.ts",
      "result": "passed",
      "summary": "No whitespace errors in the requested diff scope."
    }
  ],
  "validationOutput": [
    "Vitest: 27/27 passed.",
    "npm build: tsc --noEmit and Vite build passed.",
    "cargo test: retry helper 1/1 passed.",
    "Targeted git diff --check produced no output."
  ],
  "residualRisks": [
    "Native Windows event and visibility APIs are not covered by a live GUI integration test; unit/build/static-contract checks passed."
  ],
  "noStagedFiles": true,
  "diffSummary": "Reviewed the requested release-context final-config fix, native position cache ownership, Rust visibility retry loop, and their focused contract/unit tests only.",
  "reviewFindings": [
    "No Critical findings.",
    "No Important findings.",
    "No Minor findings.",
    "Ready: final release configuration determines throw/fall; stable non-climb paths do not enumerate system windows."
  ],
  "manualNotes": "Review-only task. No reviewed source file was edited."
}
```
