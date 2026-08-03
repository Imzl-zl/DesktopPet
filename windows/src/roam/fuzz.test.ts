// Regression fuzz: climb mode must never get permanently stuck, regardless of
// window layout. Historically the pet could freeze forever when a wander
// target landed 6–8px away: moveToward stops at ARRIVAL_DISTANCE (8) while
// advanceWander only picked a new target below 6px, so the pet stood still
// every tick with no way to re-target.
//
// Fully deterministic (mulberry32 for layout AND Math.random), so a failure
// here reproduces on every run.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { runMode } from "./modes";
import type { Environment, Point } from "./types";

vi.mock("@tauri-apps/api/window", () => ({
  cursorPosition: vi.fn(),
}));

let storedRoamMode = "climb";
vi.stubGlobal("localStorage", {
  getItem: vi.fn((key: string) => (key === "ap_roam_mode" ? storedRoamMode : null)),
});

const pet = { clearRow: vi.fn(), setRow: vi.fn() };

/// Deterministic PRNG so failures are reproducible.
function mulberry32(seed: number) {
  let a = seed >>> 0;
  return () => {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const WORK_AREA = { left: 0, top: 0, right: 1920, bottom: 1040 };

async function simulate(env: Environment, start: Point, ticks = 900): Promise<boolean> {
  let pos = start;
  let stallRun = 0;
  let finalStuck = false;
  const t0 = Date.now();
  await runMode("wander", { env, pos, pet: pet as never }); // reset module state
  const rng = mulberry32(7777); // deterministic Math.random for wander targets / direction flips
  const spy = vi.spyOn(Math, "random").mockImplementation(rng);
  try {
    for (let i = 0; i < ticks; i++) {
      vi.setSystemTime(t0 + i * 30);
      const next = await runMode("climb", { env, pos, pet: pet as never });
      const movedNow = next.x !== pos.x || next.y !== pos.y;
      stallRun = movedNow ? 0 : stallRun + 1;
      // Stuck only if completely still for the final 9s (resting pauses are <= 3.5s).
      if (i > ticks - 300 && stallRun > 280) finalStuck = true;
      pos = next;
    }
  } finally {
    spy.mockRestore();
  }
  return finalStuck;
}

function randomWindow(rnd: () => number, i: number): Environment["windows"][number] {
  const w = 300 + rnd() * 1400;
  const h = 200 + rnd() * 700;
  // Tops can be anywhere: negative (off-screen top), 0 (maximized), mid-screen.
  const top = Math.round((rnd() - 0.35) * 1600);
  const left = Math.round((rnd() - 0.2) * 2100);
  return { title: `W${i}`, rect: { left, top, right: left + w, bottom: top + h } };
}

describe("climb never gets permanently stuck", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2025, 0, 1));
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("random layouts x random starts (300 seeds)", async () => {
    const stuck: Array<{ seed: number; start: Point; windows: unknown }> = [];
    for (let seed = 0; seed < 300; seed++) {
      const rnd = mulberry32(seed);
      const nWin = 1 + Math.floor(rnd() * 5);
      const windows = Array.from({ length: nWin }, (_, i) => randomWindow(rnd, i));
      const env: Environment = { workArea: WORK_AREA, windows };
      const start: Point = {
        x: Math.round(rnd() * 1800),
        y: Math.round(rnd() * 900),
      };
      const stuckNow = await simulate(env, start, 900);
      if (stuckNow) {
        stuck.push({ seed, start, windows });
        if (stuck.length >= 5) break;
      }
    }
    expect(stuck).toEqual([]);
  }, 60_000);
});
