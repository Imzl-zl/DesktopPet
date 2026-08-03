import { describe, expect, it, vi } from "vitest";

type Point = { x: number; y: number };
type DragOutcome = {
  kind: "click" | "drag" | "cancel";
  completion: Promise<void> | null;
};
type DragController = {
  begin(pointerId: number, point: Point, grabOffset: Point, startedAt: number): boolean;
  move(pointerId: number, point: Point): boolean;
  finish(pointerId: number, point: Point, allowClick: boolean, now: number): DragOutcome | null;
};
type PointerOptions = {
  drag: DragController;
  isMenuOpen(): boolean;
  hideMenu(): void;
  showMenu(): void;
  now(): number;
  scale(): number;
  reportError(error: unknown): void;
};
type PointerModule = {
  attachFloatingBallPointerDrag(ball: HTMLElement, options: PointerOptions): void;
};

const pointerModulePath = "./floating-ball-pointer";

async function attach(ball: FakeBall, options: PointerOptions): Promise<void> {
  const module = await import(/* @vite-ignore */ pointerModulePath).catch(() => null);
  expect(module).not.toBeNull();
  (module as PointerModule).attachFloatingBallPointerDrag(ball as unknown as HTMLElement, options);
}

class FakeBall {
  readonly captured = new Set<number>();
  readonly classes = new Set<string>();
  private readonly listeners = new Map<string, (event: PointerEvent) => void>();

  readonly classList = {
    add: (...names: string[]) => names.forEach((name) => this.classes.add(name)),
    remove: (...names: string[]) => names.forEach((name) => this.classes.delete(name)),
  };

  addEventListener(type: string, listener: (event: PointerEvent) => void): void {
    this.listeners.set(type, listener);
  }

  setPointerCapture(pointerId: number): void {
    this.captured.add(pointerId);
  }

  hasPointerCapture(pointerId: number): boolean {
    return this.captured.has(pointerId);
  }

  releasePointerCapture(pointerId: number): void {
    this.captured.delete(pointerId);
  }

  dispatch(type: string, event: PointerEvent): void {
    this.listeners.get(type)?.(event);
  }
}

function pointerEvent(overrides: Partial<PointerEvent> = {}): PointerEvent {
  return {
    pointerId: 1,
    button: 0,
    screenX: 100,
    screenY: 200,
    clientX: 10,
    clientY: 20,
    preventDefault: vi.fn(),
    ...overrides,
  } as PointerEvent;
}

function options(drag: DragController, overrides: Partial<PointerOptions> = {}): PointerOptions {
  return {
    drag,
    isMenuOpen: vi.fn().mockReturnValue(false),
    hideMenu: vi.fn(),
    showMenu: vi.fn(),
    now: vi.fn().mockReturnValue(500),
    scale: vi.fn().mockReturnValue(1.5),
    reportError: vi.fn(),
    ...overrides,
  };
}

describe("floating ball pointer binding", () => {
  it("captures a pointer, starts dragging, then releases it without duplicate completion", async () => {
    const drag: DragController = {
      begin: vi.fn().mockReturnValue(true),
      move: vi.fn().mockReturnValue(true),
      finish: vi.fn().mockReturnValue({ kind: "click", completion: null }),
    };
    const ball = new FakeBall();
    const config = options(drag);
    await attach(ball, config);

    const down = pointerEvent();
    ball.dispatch("pointerdown", down);
    expect(down.preventDefault).toHaveBeenCalledTimes(1);
    expect(drag.begin).toHaveBeenCalledWith(1, { x: 100, y: 200 }, { x: 15, y: 30 }, 500);
    expect(ball.captured).toContain(1);
    expect(ball.classes).toContain("pressed");

    ball.dispatch("pointermove", pointerEvent({ screenX: 110 }));
    expect(drag.move).toHaveBeenCalledWith(1, { x: 110, y: 200 });
    expect(ball.classes).not.toContain("pressed");
    expect(ball.classes).toContain("dragging");

    ball.dispatch("pointerup", pointerEvent({ screenX: 120 }));
    expect(drag.finish).toHaveBeenCalledWith(1, { x: 120, y: 200 }, true, 500);
    expect(config.showMenu).toHaveBeenCalledTimes(1);
    expect(ball.captured).not.toContain(1);
    expect(ball.classes).not.toContain("dragging");

    ball.dispatch("lostpointercapture", pointerEvent({ screenX: 120 }));
    expect(drag.finish).toHaveBeenCalledTimes(1);
  });

  it("cancels a drag without opening the menu and reports asynchronous failures", async () => {
    const failure = new Error("persist failed");
    const completion = Promise.reject(failure);
    void completion.catch(() => {});
    const drag: DragController = {
      begin: vi.fn().mockReturnValue(true),
      move: vi.fn().mockReturnValue(false),
      finish: vi.fn().mockReturnValue({ kind: "drag", completion }),
    };
    const ball = new FakeBall();
    const config = options(drag);
    await attach(ball, config);

    ball.dispatch("pointerdown", pointerEvent({ pointerId: 2 }));
    ball.dispatch("pointercancel", pointerEvent({ pointerId: 2, screenX: 140 }));

    expect(drag.finish).toHaveBeenCalledWith(2, { x: 140, y: 200 }, false, 500);
    expect(config.showMenu).not.toHaveBeenCalled();
    expect(ball.captured).not.toContain(2);
    await vi.waitFor(() => expect(config.reportError).toHaveBeenCalledWith(failure));
  });

  it("closes an open menu instead of beginning a new drag", async () => {
    const drag: DragController = {
      begin: vi.fn().mockReturnValue(true),
      move: vi.fn().mockReturnValue(false),
      finish: vi.fn().mockReturnValue(null),
    };
    const ball = new FakeBall();
    const config = options(drag, { isMenuOpen: vi.fn().mockReturnValue(true) });
    await attach(ball, config);

    ball.dispatch("pointerdown", pointerEvent());

    expect(config.hideMenu).toHaveBeenCalledTimes(1);
    expect(drag.begin).not.toHaveBeenCalled();
    expect(ball.captured).toHaveLength(0);
  });
});
