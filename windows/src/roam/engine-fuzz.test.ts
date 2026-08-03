// Engine-level regression fuzz: the real tick loop clamps positions in
// stepMode and treats sub-0.5px movement as "not moving" (switching to the
// 200ms idle tick). This simulates the full loop (runMode + clamp + arrival
// margin + slow ticks) to make sure climb can never freeze permanently.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { runMode } from "./modes";
import type { Environment, Point } from "./types";
import { clampToBounds } from "./types";

vi.mock("@tauri-apps/api/window", () => ({
  cursorPosition: vi.fn(),
}));

let storedRoamMode = "climb";
vi.stubGlobal("localStorage", {
  getItem: vi.fn((key: string) => (key === "ap_roam_mode" ? storedRoamMode : null)),
});

const pet = { clearRow: vi.fn(), setRow: vi.fn() };

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

/// Mirrors engine.stepMode: clamp, then treat <0.5px movement as stationary.
async function stepMode(env: Environment, pos: Point): Promise<{ next: Point; moved: boolean }> {
  const next = await runMode("climb", { env, pos, pet: pet as never });
  const clamped = clampToBounds(next, env.workArea);
  const moved = Math.abs(clamped.x - pos.x) >= 0.5 || Math.abs(clamped.y - pos.y) >= 0.5;
  return { next: clamped, moved };
}

async function simulateEngine(env: Environment, start: Point, ticks = 900): Promise<boolean> {
  let pos = start;
  let stallRun = 0;
  let finalStuck = false;
  const t0 = Date.now();
  await runMode("wander", { env, pos, pet: pet as never }); // reset module state
  const rng = mulberry32(7777);
  const spy = vi.spyOn(Math, "random").mockImplementation(rng);
  try {
    for (let i = 0; i < ticks; i++) {
      // real loop sleeps 30ms when moving, 200ms when stationary
      vi.setSystemTime(t0 + i * (stallRun > 0 ? 200 : 30));
      const { next, moved } = await stepMode(env, pos);
      stallRun = moved ? 0 : stallRun + 1;
      // stationary for the final ~9s (resting pauses are <= 3.5s) => stuck
      if (i > ticks - 300 && stallRun > 60) finalStuck = true;
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
  const top = Math.round((rnd() - 0.35) * 1600);
  const left = Math.round((rnd() - 0.2) * 2100);
  return { title: `W${i}`, rect: { left, top, right: left + w, bottom: top + h } };
}

describe("engine-level climb never gets permanently stuck", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2025, 0, 1));
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("random layouts x random starts (300 seeds, full tick loop)", async () => {
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
      const stuckNow = await simulateEngine(env, start, 900);
      if (stuckNow) {
        stuck.push({ seed, start, windows });
        if (stuck.length >= 5) break;
      }
    }
    expect(stuck).toEqual([]);
  }, 60_000);
});
