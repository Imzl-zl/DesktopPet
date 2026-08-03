## Review
- Correct: `windows/src/roam/window.ts:30-48,76-123` uses generation invalidation and pending-promise coordination. `setLogical` writes the logical target only after `setPosition` resolves successfully, and `onMoved` updates the cache only while `nativeDragActive` is true. `windows/src/roam/window.test.ts:115-175,203-232` covers stale reads, out-of-order programmatic writes, drag updates, and scale invalidation; listener registration retry/partial cleanup is covered at `windows/src/roam/window.test.ts:178-271`.
- Correct: normal automatic non-climb roaming calls `fetchEnvironment(false)` at `windows/src/roam/engine.ts:117-120`; a normal non-climb drag release likewise calls it with `false` at `windows/src/roam/engine.ts:145-149`. Therefore these paths do not enumerate system windows.
- Important: `windows/src/roam/engine.ts:151-157` reloads configuration after the first environment read, but not after the conditional second `fetchEnvironment(true)`. Sequence: release begins non-climb, switches to climb during the first await, enters the second await, then switches back to non-climb before it resolves. The retained `cfg.mode === "climb"` then selects `applyFall` at line 157 although the latest configuration requires `applyThrow`. This is not strictly equivalent to the required original semantic of choosing release physics from the latest configuration after environment loading. Reload configuration once more after the second fetch (or perform a final configuration read immediately before the branch) and add a behavior-level regression test; `windows/src/performance-contract.test.ts:42-52` currently only asserts source fragments and does not exercise this second-await transition.
- Correct: `windows/src-tauri/src/lib.rs:55-66` updates `last_ignore` only after `set_ignore_cursor_events` succeeds. Both the unknown-visibility safety reset (`651-654`) and visible click-through transition (`669-697`) use it, so failures retain the old cache value and are retried on the next loop. Rust coverage at `windows/src-tauri/src/lib.rs:813-832` verifies failure then retry.
- Correct: known-hidden windows are excluded from cursor acquisition, `outer_position`, hit-rectangle evaluation, and persistence: `windows/src-tauri/src/lib.rs:644-660`, `669-698`, and `713-730` operate cursor/geometry/hit-test/persistence only on `visible_wins`; known-hidden windows are not inserted there at line 647. The hit-rect and ignore cache cleanup at `703-711` uses labels only, not hidden-window geometry or hit-testing.
- Note: No Critical or Minor findings in the requested files. The reported Important issue blocks a "ready" attestation until the final configuration read is added after the optional second environment fetch.

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "One concrete Important finding is documented at windows/src/roam/engine.ts:151-157 with a reproducible configuration-transition sequence; all inspected required properties include file-and-line evidence."
    }
  ],
  "changedFiles": [
    "windows/src/roam/window.ts",
    "windows/src/roam/engine.ts",
    "windows/src/roam/window.test.ts",
    "windows/src-tauri/src/lib.rs",
    "windows/src/performance-contract.test.ts"
  ],
  "testsAddedOrUpdated": [
    "windows/src/roam/window.test.ts",
    "windows/src/performance-contract.test.ts",
    "windows/src-tauri/src/lib.rs (Rust unit test module)"
  ],
  "commandsRun": [
    {
      "command": "cd windows && npm exec vitest run src/roam/window.test.ts src/performance-contract.test.ts",
      "result": "passed",
      "summary": "2 files, 26 tests passed."
    },
    {
      "command": "cd windows/src-tauri && cargo test retries_a_failed_ignore_cursor_transition",
      "result": "passed",
      "summary": "Rust retry unit test passed."
    },
    {
      "command": "git diff --check -- [scoped files]",
      "result": "passed",
      "summary": "No whitespace errors in the requested diff."
    },
    {
      "command": "git diff --cached --quiet",
      "result": "passed",
      "summary": "No staged files."
    }
  ],
  "validationOutput": [
    "Vitest target: 26/26 passed.",
    "Cargo retry unit: 1/1 passed.",
    "Confirmed API type: onMoved payload is PhysicalPosition."
  ],
  "residualRisks": [
    "Important: configuration can change from climb back to non-climb during the optional second environment fetch, causing stale cfg to select applyFall."
  ],
  "noStagedFiles": true,
  "diffSummary": "Scoped changes add drag-only logical-position caching, gate system-window enumeration by climb mode, and make native visibility/click-through persistence skip known-hidden windows with retry-safe ignore-state caching.",
  "reviewFindings": [
    "important: windows/src/roam/engine.ts:151-157 - after fetchEnvironment(true), cfg is stale; a second configuration change can select fall instead of the latest-mode throw.",
    "no critical findings",
    "no minor findings"
  ],
  "manualNotes": "Review-only task; no source edits were made. Not ready until the Important release-mode race is fixed and behaviorally tested."
}
```
