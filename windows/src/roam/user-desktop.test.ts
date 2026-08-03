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

// User's real desktop: maximized windows (top=0) + edge-touching windows (top<0).
const USER_DESKTOP: Environment = {
  workArea: { left: 0, top: 0, right: 1707, bottom: 1067 },
  windows: [
    { title: "W", rect: { left: 0, top: 0, right: 1707, bottom: 1067 } },
    { title: "P", rect: { left: 0, top: 0, right: 1707, bottom: 1067 } },
    { title: "[", rect: { left: -7, top: -7, right: 1714, bottom: 1026 } },
    { title: "L", rect: { left: -7, top: -7, right: 1714, bottom: 1026 } },
  ],
};

describe("climb on maximized desktop", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2025, 0, 1));
    pet.clearRow.mockClear();
    pet.setRow.mockClear();
  });
  afterEach(() => vi.useRealTimers());

  it("walks to the screen-top edge of a maximized window and paces it", async () => {
    let pos: Point = { x: 1200, y: 800 };
    const t0 = Date.now();
    await runMode("wander", { env: USER_DESKTOP, pos, pet: pet as never });

    // Walk to the window edge: y must reach the screen top (climbTopY=0).
    for (let i = 0; i < 300; i++) {
      vi.setSystemTime(t0 + i * 30);
      pos = await runMode("climb", { env: USER_DESKTOP, pos, pet: pet as never });
      if (pos.y < 10) break;
    }
    expect(pos.y).toBeLessThan(10);
    expect(pos.y).toBeGreaterThanOrEqual(0);

    // Pacing: x must keep changing along the top edge (with rest pauses).
    const xs = new Set<number>();
    for (let i = 0; i < 600; i++) {
      vi.setSystemTime(t0 + (300 + i) * 30);
      pos = await runMode("climb", { env: USER_DESKTOP, pos, pet: pet as never });
      if (pos.y > 10) break; // must never leave the edge
      xs.add(Math.round(pos.x));
      if (xs.size > 20) break;
    }
    expect(pos.y).toBeLessThanOrEqual(10);
    expect(xs.size).toBeGreaterThan(3); // actually pacing along the edge
  });
});
