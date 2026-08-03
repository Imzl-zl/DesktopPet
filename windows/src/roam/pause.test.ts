import { describe, expect, it } from "vitest";

import {
  DEFAULT_WANDER_PAUSE_MAX_MS,
  DEFAULT_WANDER_PAUSE_MIN_MS,
  normalizeWanderPauseRange,
  sampleWanderPauseMs,
} from "./pause";

describe("wander pause range", () => {
  it("preserves the established defaults when persisted values are absent", () => {
    expect(normalizeWanderPauseRange(undefined, undefined)).toEqual({
      minMs: DEFAULT_WANDER_PAUSE_MIN_MS,
      maxMs: DEFAULT_WANDER_PAUSE_MAX_MS,
    });
  });

  it("orders a reversed range without imposing an arbitrary upper limit", () => {
    expect(normalizeWanderPauseRange(9000, 1200)).toEqual({ minMs: 1200, maxMs: 9000 });
    expect(normalizeWanderPauseRange(1000, 120_000)).toEqual({ minMs: 1000, maxMs: 120_000 });
  });
  it("samples inclusively from the normalized pause range", () => {
    const range = normalizeWanderPauseRange(1200, 3500);

    expect(sampleWanderPauseMs(range, () => 0)).toBe(1200);
    expect(sampleWanderPauseMs(range, () => 1)).toBe(3500);
  });
});
