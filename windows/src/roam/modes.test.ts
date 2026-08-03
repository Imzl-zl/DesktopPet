import { afterEach, describe, expect, it, vi } from "vitest";
import { runMode } from "./modes";
import { loadConfig, setRoamConfigSource } from "./types";
import type { Environment, Point } from "./types";

vi.mock("@tauri-apps/api/window", () => ({
  cursorPosition: vi.fn(),
}));

let storedRoamMode = "wander";

vi.stubGlobal("localStorage", {
  getItem: vi.fn((key: string) => (key === "ap_roam_mode" ? storedRoamMode : null)),
});

const environment: Environment = {
  workArea: { left: 0, top: 0, right: 1400, bottom: 1000 },
  windows: [
    {
      title: "Editor",
      rect: { left: 100, top: 400, right: 1000, bottom: 850 },
    },
  ],
};

const pet = {
  clearRow: vi.fn(),
  setRow: vi.fn(),
};

describe("climb roaming", () => {
  afterEach(() => {
    setRoamConfigSource(null);
    vi.useRealTimers();
  });
  it("lands on a target window instead of stopping inside its arrival margin", async () => {
    let pos: Point = { x: 900, y: 680 };

    for (let tick = 0; tick < 120; tick += 1) {
      pos = await runMode("climb", { env: environment, pos, pet: pet as never });
    }

    expect(pos.y).toBe(environment.windows[0].rect.top - 320);
  });

  it("keeps moving when no window has a usable top edge", async () => {
    const random = vi.spyOn(Math, "random").mockReturnValue(0.5);
    const start: Point = { x: 0, y: 0 };
    storedRoamMode = "climb";

    try {
      await runMode("stay", { env: environment, pos: start, pet: pet as never });
      const next = await runMode("climb", {
        env: { ...environment, windows: [] },
        pos: start,
        pet: pet as never,
      });

      expect(next).not.toEqual(start);
    } finally {
      storedRoamMode = "wander";
      random.mockRestore();
    }
  });

  it("uses the instance wander pause range after reaching a target", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2025, 0, 1));
    setRoamConfigSource(() => ({
      enabled: true,
      mode: "wander",
      speed: 5,
      wanderPauseMinMs: 9000,
      wanderPauseMaxMs: 9000,
    }));
    const random = vi.spyOn(Math, "random")
      .mockReturnValueOnce(0)
      .mockReturnValueOnce(0)
      .mockReturnValueOnce(0)
      .mockReturnValue(0.5);
    const start: Point = { x: 40, y: 40 };

    expect(loadConfig()).toMatchObject({ wanderPauseMinMs: 9000, wanderPauseMaxMs: 9000 });

    try {
      await runMode("stay", { env: environment, pos: start, pet: pet as never });
      await runMode("wander", { env: environment, pos: start, pet: pet as never });
      vi.setSystemTime(new Date(2025, 0, 1, 0, 0, 1, 201));

      await expect(runMode("wander", { env: environment, pos: start, pet: pet as never }))
        .resolves.toEqual(start);

      vi.setSystemTime(new Date(2025, 0, 1, 0, 0, 9, 1));
      await expect(runMode("wander", { env: environment, pos: start, pet: pet as never }))
        .resolves.not.toEqual(start);
    } finally {
      random.mockRestore();
    }
  });
});
