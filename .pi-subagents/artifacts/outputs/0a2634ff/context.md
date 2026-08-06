# Code Context

## Files Retrieved
1. `windows-native/src/DesktopPet.App/Windows/PetWindow.cs` (lines 84-97, 531-539, 573-680) - raw Win32 drag state, hook registration, threshold/capture/movement/release and latency sampling.
2. `windows-native/src/DesktopPet.App/Windows/DanmakuWindow.cs` (lines 23-140) - full-screen transparent Win2D host, engine wiring, pause/wake behavior, lifecycle and cross-thread entry point.
3. `windows-native/src/DesktopPet.Core/Danmaku/DanmakuEngine.cs` (lines 1-145) - track allocation, active list/pool, locking, movement/recycling.
4. `windows-native/tests/DesktopPet.Core.Tests/DanmakuEngineTests.cs` (lines 9-114) - current engine tests and their coverage gaps.
5. `windows-native/src/DesktopPet.App/Ai/ModeService.cs` (lines 34-90) - lifecycle caller: creates/shows danmaku window and routes output (inspected by search).

## Key Code

### I8: drag path
`PetWindow` installs `WndProcHook` only from `OnLoaded` (531-539). The hook handles `WM_LBUTTONDOWN`, `WM_MOUSEMOVE` only while `_pressed && MK_LBUTTON`, and `WM_LBUTTONUP` (573-596). Down validates alpha hit (`HitTestSprite`), stores client press point and cursor/window grab offset, calls `CaptureMouse` and `RoamEngine.BeginManualDrag` (604-620). Move crosses a fixed `DragThresholdPx = 4`, then calls `MoveManualDrag` with the global cursor minus grab offset (622-644). Up releases capture, clears drag row, finishes roam, and persists through `_onDragFinished` (646-668).

Concrete risks/root causes:
- **High, state-integrity:** there is no `WM_CAPTURECHANGED`/`WM_CANCELMODE` recovery. If capture is lost (alt-tab, another capture, window/system cancellation), `_pressed` can remain true and `_dragging` can remain true; later mouse-up may not arrive, leaving roam in manual-drag state and drag animation active. This is a structural state-machine gap, not a rendering issue.
- **Medium, DPI semantics:** threshold is a raw constant (29) while coordinates/messages and grab offsets are physical pixels (comments 602, 671+). At non-100% DPI, 4 physical px is not a stable logical gesture threshold. Make threshold explicit in physical pixels (`4 * dpiScale`, or document physical semantics and test it) and use one coordinate-space conversion at the boundary.
- **Medium, input loss:** move processing is gated on `MK_LBUTTON` (586). During capture, Windows normally reports it, but a button-state inconsistency can suppress final movement; a robust drag state machine should rely on capture plus cancellation/release handling and always terminate on button-up/capture loss.
- **Test gap:** no App/UI tests exercise alpha miss, threshold click-vs-drag, capture loss, grab offset, persistence callback, or DPI behavior. Existing Core roam tests do not cover this WPF/Win32 boundary.

Structural solution: isolate a platform-independent `DragSession`/controller with states Idle/Pressed/Dragging and explicit `Begin`, `Move`, `Complete`, `Cancel`; make all terminal paths (`WM_LBUTTONUP`, `WM_CAPTURECHANGED`, `WM_CANCELMODE`, `OnClosed`) call one idempotent cancel/finish routine. Keep Win32 hook as adapter only, normalize coordinates/threshold once, and have the controller return a single completed physical position so persistence and `FinishManualDrag` cannot be skipped or duplicated. Add WPF/App integration tests with a fake input adapter or extract the message reducer so cancellation and DPI cases are deterministic.

### I9: danmaku path
`DanmakuWindow` constructs `DanmakuEngine(width, trackCount, 220, 420, 220)` (42-55). Win2D Update ticks the engine and Draw enumerates `Active` (96-122). `ShowDanmaku` enqueues and unpauses only when enqueue succeeds and `_canvas != null` (129-136). `DanmakuEngine` chooses the track whose `_trackTailX` is smallest, rejects when `_trackTailX[best] + _minGap > 0`, stores `item.X` into `_trackTailX`, and never changes that array in `Tick` (74-105, 122-143).

- **High/blocker:** `_trackTailX` is a historical enqueue position, not the current tail. It is initialized to `-Infinity`, then each used track is set to `-_width * .3` and never advanced/reset on movement or recycling. Consequently, after all tracks have received one item, every future enqueue continues to see `-width*.3 + minGap <= 0` and is accepted forever, even while entries overlap; conversely the documented “least busy/current tail” behavior is not implemented. With defaults width 1920, minGap 220, this becomes a permanent no-rejection policy after initial track fill.
- **High:** allocation does not account for text width at all. `DanmakuItem` has only `Text`, `X`, `Track`, `Speed`; `DanmakuWindow` draws text via CanvasTextFormat but engine’s `minGap` is a fixed pixel gap from the left edge. Long text can overlap a newly spawned item even if starts are separated by 220 px. Width must be measured (or conservatively estimated) and included in scheduling.
- **Medium:** `Active` returns an array snapshot under lock, but returns mutable `DanmakuItem` references. Render reads item fields after lock release while Tick mutates them under lock. This avoids collection corruption but still permits torn/inconsistent per-item frame reads. Prefer immutable render snapshots (`DanmakuRenderItem` records) or hold a snapshot of values, especially if Win2D callbacks are not guaranteed on the same dispatcher thread.
- **Medium:** `ShowDanmaku` can be called before Loaded/Canvas creation; enqueue succeeds but `_canvas` is null, so the canvas stays paused and the item only becomes visible if a later enqueue wakes it. The window lifecycle should either queue wake state and unpause in Loaded, or reject/queue before enqueue. Also, after last item exits, CanvasAnimatedControl is never paused again, contradicting the “no danmaku = CPU zero” comment.
- **Concurrency contract:** engine locks Enqueue/Tick/Active, but `DanmakuWindow`’s `_canvas.Paused` is accessed from the caller thread and Win2D callback thread without dispatcher marshaling. `ModeService` likely routes on UI thread, but this is an implicit contract that should be asserted or marshaled.

Structural solution: replace `_trackTailX` with per-track scheduling state containing the current/last item’s right edge and speed (or next safe spawn time), update/recompute it as items move and on recycle, and measure text width at enqueue/render boundary. Better: have Core accept a measured width via `Enqueue(text, width, now)` and compute per-track `nextAvailableAt`/safe distance; keep Canvas-specific text measurement in App. Return immutable value snapshots to rendering. Centralize wake/pause transitions: enqueue before Loaded sets a pending-wake flag; Draw/Update pauses when Active becomes empty; marshal canvas state changes to the window dispatcher.

## Architecture
`ModeService` owns danmaku window lifecycle and calls `ShowDanmaku`. `DanmakuWindow` is the WPF/Win2D adapter: it owns canvas, frame loop, and drawing, while `DanmakuEngine` owns pure scheduling/movement/pooling. `PetWindow` is a separate WPF/Win32 adapter around `RoamEngine`; it maps raw messages into drag operations and calls manager persistence only on successful drag release. Core tests currently validate engine movement/pooling/initial track selection, but not the App boundaries.

## Start Here
Open `windows-native/src/DesktopPet.Core/Danmaku/DanmakuEngine.cs` first: the `_trackTailX` invariant is the direct I9 correctness defect. Then inspect `PetWindow.cs` hook terminal paths for I8 cancellation handling.

## Tests / Recommended Additions
- Danmaku: after all tracks fill, advance only one track/item enough to become safe and assert a subsequent enqueue is accepted on that track; assert unsafe enqueue is rejected; assert long text width affects scheduling; assert `Active` snapshots are value-stable; assert empty transition causes pause and pre-Loaded enqueue wakes after Loaded.
- Drag: reducer tests for alpha miss, sub-threshold click, threshold drag, grab offset, normal release, capture-loss cancellation, cancel/close idempotence, and scaled DPI threshold. App smoke test should verify callback coordinates and no stuck manual drag.
- Existing `DanmakuEngineTests.cs` has no concurrency, text-width, stale-tail, lifecycle, or invalid-input tests. The `Enqueue_PrefersLeastBusyTrack` test comments contradict implementation (“least busy” says largest/current tail, algorithm selects smallest historical x) and does not assert track choice, so it would not catch I9.

## Acceptance Evidence

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "Concrete severity-ranked I8/I9 findings with exact file paths/line ranges, root causes, structural fixes, and focused test recommendations are documented above."
    }
  ],
  "changedFiles": [],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "targeted source inspection (grep/read)",
      "result": "passed",
      "summary": "Retrieved PetWindow, DanmakuWindow, DanmakuEngine, tests, and ModeService references."
    },
    {
      "command": "dotnet test windows-native/tests/DesktopPet.Core.Tests",
      "result": "not-run",
      "summary": "Read-only scouting task; no test execution requested or needed for findings."
    }
  ],
  "validationOutput": [
    "No files modified; findings are based on source inspection.",
    "Residual risk remains at WPF/Win32 and Win2D runtime boundaries because no UI smoke test was run."
  ],
  "residualRisks": [
    "Exact production impact of capture loss and cross-thread Canvas access requires Windows runtime reproduction.",
    "Text width measurement and scheduling policy need product-defined semantics for very long messages."
  ],
  "noStagedFiles": true,
  "diffSummary": "No code changes; read-only analysis only.",
  "reviewFindings": [
    "high: windows-native/src/DesktopPet.Core/Danmaku/DanmakuEngine.cs:74-105,122-143 - track tail is never advanced/reset, so track safety invariant is stale and post-fill enqueue scheduling is incorrect.",
    "high: windows-native/src/DesktopPet.App/Windows/PetWindow.cs:573-668 - no capture-loss/cancel terminal path can leave drag state and RoamEngine manual-drag state stuck.",
    "high: windows-native/src/DesktopPet.Core/Danmaku/DanmakuEngine.cs:24-45 and DanmakuWindow.cs:113-119 - fixed gap ignores rendered text width, allowing visible overlap.",
    "medium: windows-native/src/DesktopPet.App/Windows/DanmakuWindow.cs:96-136 - canvas wake/pause lifecycle has pre-Loaded enqueue and never-repause gaps.",
    "medium: windows-native/src/DesktopPet.Core.Tests/DanmakuEngineTests.cs:49-66 - least-busy test has contradictory comments and no track-selection assertion."
  ],
  "manualNotes": "I8 is the drag/session boundary; I9 is the Danmaku scheduling/render lifecycle. No edits were made."
}
```
