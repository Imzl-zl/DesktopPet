import { describe, expect, it, vi } from "vitest";

type Timer = { callback: () => void; delay: number; cancelled: boolean };
type TimerHost = {
  now(): number;
  schedule(callback: () => void, delay: number): Timer;
  cancel(timer: Timer): void;
};
type QuickBubble = {
  show(text: string, durationMs: number): void;
  current(): string | null;
};
type QuickBubbleModule = {
  QuickBubbleController: new (timer: TimerHost, onExpire: () => void) => QuickBubble;
  normalizeQuickBubbleDurationSeconds(value: unknown): number;
};

const quickBubbleModulePath = "./quick-bubble";

async function loadModule(): Promise<QuickBubbleModule> {
  const module = await import(/* @vite-ignore */ quickBubbleModulePath).catch(() => null);
  expect(module).not.toBeNull();
  return module as QuickBubbleModule;
}

function timerHost() {
  let now = 0;
  const timers: Timer[] = [];
  const host: TimerHost = {
    now: () => now,
    schedule: (callback, delay) => {
      const timer = { callback, delay, cancelled: false };
      timers.push(timer);
      return timer;
    },
    cancel: (timer) => { timer.cancelled = true; },
  };
  return {
    host,
    timers,
    setNow(value: number) { now = value; },
  };
}

describe("quick bubble lifetime", () => {
  it("expires only the newest message and requests a normal render once", async () => {
    const module = await loadModule();
    const clock = timerHost();
    const onExpire = vi.fn();
    const bubble = new module.QuickBubbleController(clock.host, onExpire);

    bubble.show("first", 4000);
    const firstTimer = clock.timers[0];
    bubble.show("second", 6000);
    const secondTimer = clock.timers[1];

    expect(firstTimer.cancelled).toBe(true);
    clock.setNow(4000);
    firstTimer.callback();
    expect(bubble.current()).toBe("second");
    expect(onExpire).not.toHaveBeenCalled();

    clock.setNow(6000);
    secondTimer.callback();
    expect(bubble.current()).toBeNull();
    expect(onExpire).toHaveBeenCalledTimes(1);
  });

  it("normalizes persisted display duration into the supported range", async () => {
    const module = await loadModule();

    expect(module.normalizeQuickBubbleDurationSeconds(undefined)).toBe(4);
    expect(module.normalizeQuickBubbleDurationSeconds(0)).toBe(4);
    expect(module.normalizeQuickBubbleDurationSeconds(4.4)).toBe(4.4);
    expect(module.normalizeQuickBubbleDurationSeconds(99)).toBe(99);
  });
});
