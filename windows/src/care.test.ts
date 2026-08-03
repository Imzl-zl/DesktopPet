import { beforeEach, describe, expect, it } from "vitest";
import { migrateLegacyCareState, mutate, stateFor } from "./care";

const mockStorage: Record<string, string> = {};

Object.defineProperty(globalThis, "localStorage", {
  value: {
    getItem(key: string) {
      return mockStorage[key] ?? null;
    },
    setItem(key: string, value: string) {
      mockStorage[key] = String(value);
    },
    removeItem(key: string) {
      delete mockStorage[key];
    },
  },
  writable: true,
});

beforeEach(() => {
  for (const key of Object.keys(mockStorage)) delete mockStorage[key];
});

describe("care state migration", () => {
  it("moves legacy sprite-keyed care state to its migrated instance id", () => {
    mutate("cat", (state) => {
      state.xp = 75;
      state.totalMeals = 3;
    });

    migrateLegacyCareState("cat", "legacy-pet");

    expect(stateFor("legacy-pet")).toMatchObject({ xp: 75, totalMeals: 3 });
    expect(stateFor("cat")).toMatchObject({ xp: 0, totalMeals: 0 });
  });

  it("does not overwrite care already accumulated by the new instance", () => {
    mutate("cat", (state) => { state.xp = 75; });
    mutate("legacy-pet", (state) => { state.xp = 120; });

    migrateLegacyCareState("cat", "legacy-pet");

    expect(stateFor("legacy-pet").xp).toBe(120);
  });
});
