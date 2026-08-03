// Movement strategies. Each mode is a pure function: given the current
// position and environment, return the next position (and animate the pet).
// The engine drives the tick loop; modes stay stateless where possible.

import { cursorPosition } from "@tauri-apps/api/window";
import type { Pet } from "../pet";
import type { Environment, Point, Rect, RoamMode } from "./types";
import { sampleWanderPauseMs, type WanderPauseRange } from "./pause";
import {
  DT_SEC,
  IDLE_MS_MAX,
  IDLE_MS_MIN,
  MARGIN,
  WIN_H,
  WIN_W,
  clampToBounds,
  loadConfig,
  pxPerSec,
} from "./types";

const ROW_RIGHT = 1;
const ROW_LEFT = 2;
const ARRIVAL_DISTANCE = 8;

type HorizontalDirection = -1 | 1;

let climbDirection: HorizontalDirection | null = null;
let activeMode: RoamMode | null = null;

export interface ModeContext {
  env: Environment;
  pos: Point;
  pet: Pet | null;
}

/// Dispatches to the active mode and returns the next position (or the
/// unchanged position if the mode didn't move). Each mode handles its own
/// animation row; if no movement happens, the caller clears the row.
export async function runMode(mode: RoamMode, ctx: ModeContext): Promise<Point> {
  if (mode !== activeMode) {
    if (mode !== "climb") climbDirection = null;
    wanderTarget = null;
    restUntil = 0;
    activeMode = mode;
  }
  switch (mode) {
    case "stay": return ctx.pos;
    case "cursor": return followCursor(ctx);
    case "climb": return climb(ctx);
    case "wander":
    default: return wander(ctx);
  }
}

/// Chase the mouse cursor. Stops beside it so it doesn't cover the pointer.
async function followCursor(ctx: ModeContext): Promise<Point> {
  const { env, pos, pet } = ctx;
  try {
    const sf = (await import("@tauri-apps/api/window")).getCurrentWindow();
    const factor = await sf.scaleFactor();
    const cur = await cursorPosition();
    const target = {
      x: cur.x / factor - WIN_W / 2,
      y: cur.y / factor - WIN_H + 20,
    };
    return moveToward(target, pos, env.workArea, pet);
  } catch {
    return pos;
  }
}

/// Persistent wander target. The engine calls wander() once per tick (30ms),
/// so the target must survive across calls , otherwise the pet picks a new
/// random destination every tick and jitters in place instead of walking.
let wanderTarget: Point | null = null;

/// Shared "resting until" deadline (ms timestamp). Set by a mode when the pet
/// reaches a destination / edge and should pause. The engine loop keeps
/// ticking at 30ms while resting, so drag, mood changes, and mode switches
/// stay responsive , the modes just no-op until the deadline passes.
let restUntil = 0;

function inBounds(p: Point, bounds: Rect): boolean {
  return p.x >= bounds.left && p.x <= bounds.right - WIN_W
      && p.y >= bounds.top && p.y <= bounds.bottom - WIN_H;
}

/// Random walk within the work area. Walks to a target, idles, picks another.
/// The target persists across ticks so the pet actually reaches it. Idling is
/// done by setting `restUntil` (no blocking sleep) so the engine stays live.
function wander(ctx: ModeContext): Point {
  const config = loadConfig();
  if (config.mode !== "wander") {
    wanderTarget = null;
    restUntil = 0;
    return ctx.pos;
  }
  return advanceWander(ctx, {
    minMs: config.wanderPauseMinMs,
    maxMs: config.wanderPauseMaxMs,
  });
}

function fallbackWander(ctx: ModeContext): Point {
  restUntil = 0;
  return advanceWander(ctx, {
    minMs: IDLE_MS_MIN,
    maxMs: IDLE_MS_MAX,
  });
}

function advanceWander(ctx: ModeContext, pauseRange: WanderPauseRange): Point {
  const { env, pos, pet } = ctx;
  if (Date.now() < restUntil) return pos;
  if (!wanderTarget || !inBounds(wanderTarget, env.workArea)) {
    wanderTarget = randomTarget(env.workArea);
  }
  const target = wanderTarget;

  const dx = target.x - pos.x;
  const dy = target.y - pos.y;
  const dist = Math.hypot(dx, dy);

  // Arrival must match moveToward's stopping threshold (ARRIVAL_DISTANCE).
  // If this check used a smaller radius (e.g. 6), a target 6–8px away would
  // make moveToward stop without advanceWander picking a new target, and the
  // pet would stand still forever.
  if (dist < ARRIVAL_DISTANCE) {
    wanderTarget = null;
    restUntil = Date.now() + sampleWanderPauseMs(pauseRange);
    pet?.clearRow();
    return pos;
  }
  return moveToward(target, pos, env.workArea, pet);
}

/// Climb along the top edges of visible application windows, like Shimeji.
/// Moves to the nearest reachable window top before walking its edge, then
/// pauses and reverses direction at an edge.
async function climb(ctx: ModeContext): Promise<Point> {
  const { env, pos, pet } = ctx;
  if (env.windows.length === 0) {
    climbDirection = null;
    return fallbackWander(ctx);
  }
  if (Date.now() < restUntil) {
    return pos;
  }

  const surface = findSurfaceBelow(pos, env);
  if (!surface.isWindow || !isStandingOnSurface(pos, surface, env.workArea)) {
    climbDirection = null;
    const target = nearestClimbTarget(pos, env);
    return target ? moveToward(target, pos, env.workArea, pet) : fallbackWander(ctx);
  }

  const support = surfaceSupportRange(surface.rect, env.workArea);
  if (!support) {
    climbDirection = null;
    return fallbackWander(ctx);
  }
  const standY = climbTopY(surface.rect, env.workArea);

  const dir = climbDirection ?? (Math.random() < 0.5 ? -1 : 1);
  climbDirection = dir;
  const step = pxPerSec(loadConfig().speed) * DT_SEC;
  const nextX = pos.x + dir * step;
  const onEdge = nextX < support.left - 2 || nextX > support.right + 2;

  if (onEdge) {
    restUntil = Date.now() + IDLE_MS_MIN + Math.random() * (IDLE_MS_MAX - IDLE_MS_MIN);
    climbDirection = dir === 1 ? -1 : 1;
    pet?.clearRow();
    return {
      x: Math.max(support.left, Math.min(support.right, pos.x)),
      y: standY,
    };
  }

  const next = clampToBounds(
    { x: nextX, y: standY },
    env.workArea,
  );
  pet?.setRow(dir > 0 ? ROW_RIGHT : ROW_LEFT);
  return next;
}

function moveToward(target: Point, pos: Point, bounds: Rect, pet: Pet | null): Point {
  const dx = target.x - pos.x;
  const dy = target.y - pos.y;
  const dist = Math.hypot(dx, dy);
  if (dist < ARRIVAL_DISTANCE) { pet?.clearRow(); return pos; }
  const speed = pxPerSec(loadConfig().speed);
  const move = Math.min(dist, speed * DT_SEC);
  const next = clampToBounds(
    { x: pos.x + (dx / dist) * move, y: pos.y + (dy / dist) * move },
    bounds,
  );
  pet?.setRow(dx > 0 ? ROW_RIGHT : ROW_LEFT);
  return next;
}

function randomTarget(bounds: Rect): Point {
  const x = bounds.left + MARGIN + Math.random() * Math.max(1, bounds.right - bounds.left - WIN_W - MARGIN * 2);
  const y = bounds.top + MARGIN + Math.random() * Math.max(1, bounds.bottom - bounds.top - WIN_H - MARGIN * 2);
  return { x, y };
}

interface SurfaceInfo { rect: Rect; isWindow: boolean }

type SurfaceSupport = { left: number; right: number };

function surfaceSupportRange(rect: Rect, bounds: Rect): SurfaceSupport | null {
  const left = Math.max(bounds.left, rect.left - WIN_W / 2);
  const right = Math.min(bounds.right - WIN_W, rect.right - WIN_W / 2);
  return left <= right ? { left, right } : null;
}

/// Logical y for the pet window's TOP edge when standing on this surface.
/// A window whose top edge sits too high for the pet to stand on (standing on
/// it would push the pet off the top of the screen) is treated as reachable at
/// the screen top: the pet is pinned to the top edge instead of falling back
/// to random roaming on maximized/fullscreen desktops.
function climbTopY(rect: Rect, workArea: Rect): number {
  return Math.max(rect.top - WIN_H, workArea.top);
}

function isStandingOnSurface(pos: Point, surface: SurfaceInfo, workArea: Rect): boolean {
  return Math.abs(pos.y - climbTopY(surface.rect, workArea)) < ARRIVAL_DISTANCE;
}

function nearestClimbTarget(pos: Point, env: Environment): Point | null {
  let nearest: Point | null = null;
  let nearestDistance = Infinity;

  for (const window of env.windows) {
    const support = surfaceSupportRange(window.rect, env.workArea);
    // climbTopY clamps to the screen top, so only window tops below the
    // reachable band are skipped (they would push the pet off the bottom).
    const y = climbTopY(window.rect, env.workArea);
    if (!support || y > env.workArea.bottom - WIN_H) continue;

    const target = { x: Math.max(support.left, Math.min(support.right, pos.x)), y };
    const distance = Math.hypot(target.x - pos.x, target.y - pos.y);
    if (distance < nearestDistance) {
      nearest = target;
      nearestDistance = distance;
    }
  }

  return nearest;
}

function findSurfaceBelow(pos: Point, env: Environment): SurfaceInfo {
  // Standing on a window means the pet's top sits exactly at that window's
  // climbTopY (within the arrival margin), matching isStandingOnSurface so
  // the two can never disagree.
  for (const w of env.windows) {
    const support = surfaceSupportRange(w.rect, env.workArea);
    if (!support || pos.x < support.left - ARRIVAL_DISTANCE || pos.x > support.right + ARRIVAL_DISTANCE) continue;
    if (Math.abs(pos.y - climbTopY(w.rect, env.workArea)) < ARRIVAL_DISTANCE) {
      return { rect: w.rect, isWindow: true };
    }
  }
  return { rect: env.workArea, isWindow: false };
}
