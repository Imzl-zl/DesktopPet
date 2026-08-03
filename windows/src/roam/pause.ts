export const MIN_WANDER_PAUSE_MS = 1000;
export const DEFAULT_WANDER_PAUSE_MIN_MS = 1200;
export const DEFAULT_WANDER_PAUSE_MAX_MS = 3500;

export type WanderPauseRange = {
  minMs: number;
  maxMs: number;
};

function normalizePauseMs(value: unknown, fallback: number): number {
  const milliseconds = Number(value);
  return Number.isFinite(milliseconds) && milliseconds >= MIN_WANDER_PAUSE_MS
    ? Math.round(milliseconds)
    : fallback;
}

export function normalizeWanderPauseRange(minValue: unknown, maxValue: unknown): WanderPauseRange {
  const minMs = normalizePauseMs(minValue, DEFAULT_WANDER_PAUSE_MIN_MS);
  const maxMs = normalizePauseMs(maxValue, DEFAULT_WANDER_PAUSE_MAX_MS);
  return {
    minMs: Math.min(minMs, maxMs),
    maxMs: Math.max(minMs, maxMs),
  };
}

export function sampleWanderPauseMs(
  range: WanderPauseRange,
  random: () => number = Math.random,
): number {
  const factor = Math.max(0, Math.min(1, random()));
  return range.minMs + (range.maxMs - range.minMs) * factor;
}
