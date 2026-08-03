import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("@tauri-apps/api/window", () => ({
  getCurrentWindow: () => ({ label: "pet-test" }),
}));

vi.stubGlobal("localStorage", {
  getItem: vi.fn(() => null),
});

afterEach(async () => {
  const { setRoamConfigSource } = await import("./types");
  setRoamConfigSource(null);
  vi.resetModules();
});

describe("roam configuration", () => {
  it("normalizes a per-instance wander pause range before the engine reads it", async () => {
    const { loadConfig, setRoamConfigSource } = await import("./types");
    setRoamConfigSource(() => ({
      enabled: true,
      mode: "wander",
      speed: 5,
      wanderPauseMinMs: 9000,
      wanderPauseMaxMs: 1200,
    }));

    expect(loadConfig()).toMatchObject({
      enabled: true,
      mode: "wander",
      speed: 5,
      wanderPauseMinMs: 1200,
      wanderPauseMaxMs: 9000,
    });
  });
});
