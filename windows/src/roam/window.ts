// Shared window-position helpers. Both the engine (tick loop) and the physics
// module (throw/fall) need to read and write the pet window's logical position;
// centralizing it keeps the DPI conversion in one place and avoids two copies
// of the same `outerPosition / scaleFactor` dance drifting apart.

import { getCurrentWindow } from "@tauri-apps/api/window";
import { LogicalPosition, PhysicalPosition } from "@tauri-apps/api/dpi";
import type { Point } from "./types";

const win = getCurrentWindow();

let cachedLogicalPos: Point | null = null;
let cacheGeneration = 0;
let pendingPositionRead: Promise<Point | null> | null = null;
let pendingDragPositionMove: Promise<void> | null = null;
let dragPositionTrackingActive = false;
let trackingStarted = false;
let pendingTracking: Promise<void> | null = null;

function copyPoint(pos: Point): Point {
  return { x: pos.x, y: pos.y };
}

function invalidatePosition(): number {
  cacheGeneration += 1;
  cachedLogicalPos = null;
  return cacheGeneration;
}

function refreshDragPosition(physical: Point): void {
  const generationAtMove = invalidatePosition();
  let update: Promise<void>;
  update = win.scaleFactor()
    .then((scaleFactor) => {
      if (!dragPositionTrackingActive || generationAtMove !== cacheGeneration) return;
      cachedLogicalPos = {
        x: physical.x / scaleFactor,
        y: physical.y / scaleFactor,
      };
    })
    .catch(() => {
      // Keep the cache invalid; the regular reader retries when it is needed.
    })
    .finally(() => {
      if (pendingDragPositionMove === update) pendingDragPositionMove = null;
    });
  pendingDragPositionMove = update;
}

function startPositionTracking(): void {
  if (trackingStarted || pendingTracking) return;
  const movedRegistration = Promise.resolve().then(() =>
    win.onMoved(({ payload }) => {
      if (dragPositionTrackingActive) refreshDragPosition(payload);
    }),
  );
  const scaleRegistration = Promise.resolve().then(() =>
    win.onScaleChanged(() => {
      invalidatePosition();
    }),
  );
  pendingTracking = Promise.allSettled([movedRegistration, scaleRegistration])
    .then(([moved, scale]) => {
      if (moved.status === "fulfilled" && scale.status === "fulfilled") {
        trackingStarted = true;
        return;
      }
      if (moved.status === "fulfilled") moved.value();
      if (scale.status === "fulfilled") scale.value();
    })
    .finally(() => {
      pendingTracking = null;
    });
}

export function setDragPositionTracking(active: boolean): void {
  if (dragPositionTrackingActive === active) return;
  dragPositionTrackingActive = active;
  invalidatePosition();
}

/// Current window position in LOGICAL pixels (DPI-divided). Returns null if the
/// window or scaleFactor can't be read (e.g. window closing).
export async function currentLogicalPos(): Promise<Point | null> {
  startPositionTracking();
  if (cachedLogicalPos) return copyPoint(cachedLogicalPos);
  while (pendingDragPositionMove) {
    const update = pendingDragPositionMove;
    await update;
    if (cachedLogicalPos) return copyPoint(cachedLogicalPos);
    if (pendingDragPositionMove === update) break;
  }
  if (!pendingPositionRead) {
    const generationAtReadStart = cacheGeneration;
    pendingPositionRead = (async () => {
      try {
        const sf = await win.scaleFactor();
        const p = await win.outerPosition();
        if (generationAtReadStart !== cacheGeneration) {
          return cachedLogicalPos ? copyPoint(cachedLogicalPos) : null;
        }
        cachedLogicalPos = { x: p.x / sf, y: p.y / sf };
        return copyPoint(cachedLogicalPos);
      } catch {
        return null;
      } finally {
        pendingPositionRead = null;
      }
    })();
  }
  const pos = await pendingPositionRead;
  return pos ? copyPoint(pos) : null;
}

/// Move the window to a physical-pixel position and return its corresponding
/// logical position for roam sampling.
export async function setPhysical(pos: Point): Promise<Point> {
  const generationAtSetStart = invalidatePosition();
  await win.setPosition(new PhysicalPosition(pos.x, pos.y));
  const scaleFactor = await win.scaleFactor();
  const logical = { x: pos.x / scaleFactor, y: pos.y / scaleFactor };
  if (generationAtSetStart === cacheGeneration) cachedLogicalPos = copyPoint(logical);
  return logical;
}

/// Move the window to a logical-pixel position. Caller is responsible for
/// clamping to the work area first.
export async function setLogical(pos: Point): Promise<void> {
  const generationAtSetStart = invalidatePosition();
  await win.setPosition(new LogicalPosition(pos.x, pos.y));
  if (generationAtSetStart === cacheGeneration && !dragPositionTrackingActive) {
    cachedLogicalPos = copyPoint(pos);
  }
}
