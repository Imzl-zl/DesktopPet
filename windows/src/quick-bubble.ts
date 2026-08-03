export const QUICK_BUBBLE_DURATION_KEY = "ap_quick_bubble_duration";
export const DEFAULT_QUICK_BUBBLE_DURATION_SECONDS = 4;

export type QuickBubbleTimer = {
  now(): number;
  schedule(callback: () => void, delay: number): unknown;
  cancel(timer: unknown): void;
};

type StorageReader = Pick<Storage, "getItem">;

export function normalizeQuickBubbleDurationSeconds(value: unknown): number {
  const seconds = Number(value);
  return Number.isFinite(seconds) && seconds >= 1
    ? seconds
    : DEFAULT_QUICK_BUBBLE_DURATION_SECONDS;
}

export function readQuickBubbleDurationMs(storage: StorageReader = localStorage): number {
  return Math.round(normalizeQuickBubbleDurationSeconds(storage.getItem(QUICK_BUBBLE_DURATION_KEY)) * 1000);
}

export class QuickBubbleController {
  private text: string | null = null;
  private expiresAt = 0;
  private timer: unknown | null = null;
  private generation = 0;

  constructor(
    private readonly clock: QuickBubbleTimer,
    private readonly onExpire: () => void,
  ) {}

  show(text: string, durationMs: number): void {
    this.cancelTimer();
    this.text = text;
    this.expiresAt = this.clock.now() + Math.max(0, durationMs);
    this.generation += 1;
    this.scheduleExpiry(this.generation);
  }

  current(): string | null {
    return this.text !== null && this.clock.now() < this.expiresAt ? this.text : null;
  }

  private scheduleExpiry(generation: number): void {
    const delay = Math.max(0, this.expiresAt - this.clock.now());
    this.timer = this.clock.schedule(() => this.expire(generation), delay);
  }

  private expire(generation: number): void {
    if (generation !== this.generation || this.text === null) return;

    const remaining = this.expiresAt - this.clock.now();
    if (remaining > 0) {
      this.scheduleExpiry(generation);
      return;
    }

    this.timer = null;
    this.text = null;
    this.expiresAt = 0;
    this.onExpire();
  }

  private cancelTimer(): void {
    if (this.timer !== null) this.clock.cancel(this.timer);
    this.timer = null;
  }
}
