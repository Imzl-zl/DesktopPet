## Review
- Correct: `windows/src/roam/window.ts:30-122` uses a generation-invalidated logical-position cache. A successful `setLogical` is the automatic-roam source of truth; `onMoved` updates only while `nativeDragActive`; drag start/end and scale events invalidate the cache. Pending reads and drag updates are generation-guarded, preventing stale async results from overwriting newer state.
- Correct: `windows/src/roam/window.ts:50-73` wraps listener registrations so synchronous throws become rejected promises, unregisters any listener from a partial registration, clears `pendingTracking`, and permits the next read to retry. `windows/src/roam/window.test.ts:178-271` covers synchronous/rejected registration and partial-registration cleanup.
- Correct: `windows/src/roam/engine.ts:182-193` explicitly forwards native drag state to the window-position owner without changing roam mode, physics, climbing, or movement constants.
- Correct: `windows/src-tauri/src/lib.rs:622-715` classifies visibility before cursor, native geometry, hit testing, and persistence. Known-hidden windows do not reach cursor reads, `outer_position`, hit testing, or position persistence. Visibility-query failures take the interactive safety path and cache the false state only after that safety call succeeds.
- Important: `windows/src-tauri/src/lib.rs:675-677` records `last_ignore[label] = ignore` even when `set_ignore_cursor_events(ignore)` fails. If a transition from click-through to interactive (`false`) fails, the native window remains click-through but the cache says it is interactive; subsequent visible ticks skip the required retry. A later `is_visible` failure at `:638-643` also skips its safety reset because the incorrect cached value is already `false`. Cache updates must occur only after a successful `set_ignore_cursor_events`, with a regression test for the failed visible-path transition followed by an `is_visible` failure.
- Note: `windows/src/performance-contract.test.ts:58-65` only asserts source text for the visibility-error safety branch. It cannot detect the failed visible-path state transition above; add behavioral coverage when fixing it.

Ready status: fixes-required. No Critical findings; one Important finding must be fixed before acceptance.

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "Concrete Important finding at windows/src-tauri/src/lib.rs:675-677, with failure sequence and affected visibility safety branch at :638-643."
    }
  ],
  "changedFiles": [],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "npm --prefix windows test -- --run src/roam/window.test.ts src/performance-contract.test.ts",
      "result": "passed",
      "summary": "2 test files passed; 24 tests passed."
    },
    {
      "command": "npm --prefix windows run build",
      "result": "passed",
      "summary": "TypeScript check and Vite production build completed."
    },
    {
      "command": "cargo check --manifest-path windows/src-tauri/Cargo.toml",
      "result": "passed",
      "summary": "Rust dev profile check completed."
    },
    {
      "command": "git diff --check -- windows/src/roam/window.ts windows/src/roam/engine.ts windows/src/roam/window.test.ts windows/src-tauri/src/lib.rs windows/src/performance-contract.test.ts",
      "result": "passed",
      "summary": "No whitespace errors in the scoped diff."
    }
  ],
  "validationOutput": [
    "Scoped Vitest run: 24/24 passed.",
    "Production frontend build passed.",
    "Cargo check passed.",
    "Scoped staged diff is empty."
  ],
  "residualRisks": [
    "An unsuccessful visible-path set_ignore_cursor_events call is treated as applied, leaving an opaque window click-through and suppressing both normal and visibility-error-path retries."
  ],
  "noStagedFiles": true,
  "diffSummary": "Read-only review of the scoped position-ownership, visibility-loop, and performance-contract changes; no source files were modified.",
  "reviewFindings": [
    "important: windows/src-tauri/src/lib.rs:675-677 - last_ignore is updated even when set_ignore_cursor_events fails, suppressing required retry and potentially defeating the visibility-error safety reset."
  ],
  "manualNotes": "Review scope was limited to the five requested files and the lib.rs visibility loop."
}
```