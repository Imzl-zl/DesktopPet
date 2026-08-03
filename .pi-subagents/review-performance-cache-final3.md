## Review
- Correct: `windows/src/roam/window.ts:29-46` uses the native `scaleFactor()` for each `onMoved` physical payload, rather than the `onScaleChanged` payload. `windows/src/roam/window.ts:61-72` uses `Promise.allSettled`, unregisters either successfully registered listener on failure, clears the pending registration state, and therefore permits retry. These behaviors are covered by `windows/src/roam/window.test.ts:158-170` and `windows/src/roam/window.test.ts:213-250`.
- Correct: Known-hidden windows are excluded before cursor sampling (`windows/src-tauri/src/lib.rs:631-648`), geometry/hit testing (`windows/src-tauri/src/lib.rs:657-686`), and persistence (`windows/src-tauri/src/lib.rs:701-710`). The scoped changes do not modify roam speed, physics constants, climbing rules, or user-setting values.
- Critical: `windows/src/roam/window.ts:29-46` lets a delayed, obsolete `onMoved` event overwrite a newer `setLogical` result. Example: after `setLogical(A)` and then `setLogical(B)` complete, a queued native moved event for A calls `invalidatePosition()` at line 30 and later commits A at lines 35-38. The generation is created by the event itself, so the latest logical write at `windows/src/roam/window.ts:110-115` cannot reject it. This violates the required arbitrary ordering guarantee that an old position never overwrites the latest logical position. `windows/src/roam/window.test.ts:116-132` tests out-of-order `setPosition` completion only; it does not interleave a stale moved event with those writes.
- Critical: `windows/src/roam/window.ts:57-59` invalidates an already correct moved cache whenever the paired scale event arrives after the asynchronous `scaleFactor()` request has settled and `pendingMovedPosition` has been cleared at line 44. The next `currentLogicalPos()` then reaches `outerPosition()` at lines 86-102. Thus a valid moved-before-scale cross-DPI delivery can add an outer-position read, contrary to the no-extra-read contract. `windows/src/roam/window.test.ts:134-156` only keeps the scale-factor Promise unresolved until after the scale event, so it does not cover the settled-before-scale ordering.
- Important: On an `is_visible()` failure, `windows/src-tauri/src/lib.rs:638-642` conditionally calls `set_ignore_cursor_events(false)` and records `false` even if that setter fails. A subsequent visibility failure skips the setter because the local cache says `false`; this is not an unconditional forced reset and cannot retry a failed reset. It does preserve the no-cursor/no-geometry behavior, but does not fully establish the stated fail-safe interaction contract.
- Note: `windows/src/performance-contract.test.ts:51-70` is textual coverage only. It establishes source shape for hidden-window handling but cannot demonstrate actual call suppression or retry behavior under Tauri API errors.

Not ready: the two Critical cache-ordering defects violate acceptance criteria 1 and 2.

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "not-satisfied",
      "evidence": "Critical findings at windows/src/roam/window.ts:29-46 and :57-59 demonstrate stale moved-event overwrite and moved-before-scale extra outerPosition read."
    }
  ],
  "changedFiles": [
    "windows/src/roam/window.ts",
    "windows/src/roam/window.test.ts",
    "windows/src-tauri/src/lib.rs",
    "windows/src/performance-contract.test.ts"
  ],
  "testsAddedOrUpdated": [
    "windows/src/roam/window.test.ts",
    "windows/src/performance-contract.test.ts"
  ],
  "commandsRun": [
    {
      "command": "npm --prefix windows test",
      "result": "passed",
      "summary": "8 test files and 45 tests passed."
    },
    {
      "command": "npm --prefix windows run build",
      "result": "passed",
      "summary": "tsc --noEmit and Vite production build passed."
    },
    {
      "command": "cargo check --manifest-path windows/src-tauri/Cargo.toml",
      "result": "passed",
      "summary": "Cargo dev-profile check passed."
    },
    {
      "command": "cargo test --manifest-path windows/src-tauri/Cargo.toml",
      "result": "passed",
      "summary": "Cargo unit and doc tests passed; the crate currently has 0 Rust tests."
    },
    {
      "command": "git diff --check",
      "result": "passed",
      "summary": "No whitespace errors."
    },
    {
      "command": "git diff --cached --name-only",
      "result": "passed",
      "summary": "No staged files."
    }
  ],
  "validationOutput": [
    "Scoped source review completed for the requested window cache, listener cleanup, visibility-loop, and performance-contract paths.",
    "All requested automated commands passed, but they do not cover the two critical event-order interleavings."
  ],
  "residualRisks": [
    "A late moved event from an older programmatic set can return stale logical coordinates until another event or read corrects the cache.",
    "A scale event delivered after the moved scale-factor Promise settles causes an avoidable outerPosition IPC read.",
    "Visibility-query failure may leave a window click-through if the first forced reset fails, because later failures are suppressed by last_ignore."
  ],
  "noStagedFiles": true,
  "diffSummary": "The scoped change adds logical-position caching and Tauri move/scale listener tracking, plus hidden-window work suppression in the Rust loop and textual performance contracts.",
  "reviewFindings": [
    "critical: windows/src/roam/window.ts:29-46 - delayed old moved event can overwrite newer setLogical cache.",
    "critical: windows/src/roam/window.ts:57-59 - moved-before-scale delivery can invalidate a correct cache and force outerPosition.",
    "important: windows/src-tauri/src/lib.rs:638-642 - failed visibility fail-safe reset is marked complete and not retried."
  ],
  "manualNotes": "Read-only review; no source or test files were edited."
}
```