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
  canBegin(event: PointerEvent): boolean;
  onBegin(): void;
  finishCapture(): Promise<void>;
  onClick(): void;
  now(): number;
  scale(): number;
  reportError(error: unknown): void;
};
type PointerModule = {
  attachPetPointerDrag(element: HTMLElement, options: PointerOptions): void;
};

const pointerModulePath = "./pet-pointer-drag";

async function attach(element: FakeElement, options: PointerOptions): Promise<void> {
  const module = await import(/* @vite-ignore */ pointerModulePath).catch(() => null);
  expect(module).not.toBeNull();
  (module as PointerModule).attachPetPointerDrag(element as unknown as HTMLElement, options);
}

class FakeElement {
  readonly captured = new Set<number>();
  private readonly listeners = new Map<string, (event: PointerEvent) => void>();

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
    canBegin: vi.fn().mockReturnValue(true),
    onBegin: vi.fn(),
    finishCapture: vi.fn().mockResolvedValue(undefined),
    onClick: vi.fn(),
    now: vi.fn().mockReturnValue(500),
    scale: vi.fn().mockReturnValue(1.5),
    reportError: vi.fn(),
    ...overrides,
  };
}

describe("pet pointer drag binding", () => {
  it("captures the gesture and preserves a stationary left-click", async () => {
    const drag: DragController = {
      begin: vi.fn().mockReturnValue(true),
      move: vi.fn().mockReturnValue(false),
      finish: vi.fn().mockReturnValue({ kind: "click", completion: null }),
    };
    const element = new FakeElement();
    const config = options(drag);
    await attach(element, config);

    const down = pointerEvent();
    element.dispatch("pointerdown", down);
    expect(down.preventDefault).toHaveBeenCalledTimes(1);
    expect(config.onBegin).toHaveBeenCalledTimes(1);
    expect(drag.begin).toHaveBeenCalledWith(1, { x: 100, y: 200 }, { x: 15, y: 30 }, 500);
    expect(element.captured).toContain(1);

    element.dispatch("pointerup", pointerEvent({ screenX: 102 }));
    expect(drag.finish).toHaveBeenCalledWith(1, { x: 102, y: 200 }, true, 500);
    await vi.waitFor(() => expect(config.finishCapture).toHaveBeenCalledTimes(1));
    expect(config.onClick).toHaveBeenCalledTimes(1);
    expect(element.captured).not.toContain(1);

    element.dispatch("lostpointercapture", pointerEvent({ screenX: 102 }));
    expect(drag.finish).toHaveBeenCalledTimes(1);
  });

  it("cancels a drag without converting it into a click", async () => {
    const completion = Promise.resolve();
    const drag: DragController = {
      begin: vi.fn().mockReturnValue(true),
      move: vi.fn().mockReturnValue(true),
      finish: vi.fn().mockReturnValue({ kind: "drag", completion }),
    };
    const element = new FakeElement();
    const config = options(drag);
    await attach(element, config);

    element.dispatch("pointerdown", pointerEvent({ pointerId: 2 }));
    element.dispatch("pointercancel", pointerEvent({ pointerId: 2, screenX: 140 }));

    expect(drag.finish).toHaveBeenCalledWith(2, { x: 140, y: 200 }, false, 500);
    expect(config.finishCapture).not.toHaveBeenCalled();
    expect(config.onClick).not.toHaveBeenCalled();
    expect(element.captured).not.toContain(2);
  });

  it("leaves transparent canvas pixels outside the pet interaction untouched", async () => {
    const drag: DragController = {
      begin: vi.fn().mockReturnValue(true),
      move: vi.fn().mockReturnValue(false),
      finish: vi.fn().mockReturnValue(null),
    };
    const element = new FakeElement();
    const config = options(drag, { canBegin: vi.fn().mockReturnValue(false) });
    await attach(element, config);

    const down = pointerEvent();
    element.dispatch("pointerdown", down);

    expect(down.preventDefault).not.toHaveBeenCalled();
    expect(config.onBegin).not.toHaveBeenCalled();
    expect(drag.begin).not.toHaveBeenCalled();
    expect(element.captured).toHaveLength(0);
  });

  it("does not run begin side effects when another element owns the controller", async () => {
    const drag: DragController = {
      begin: vi.fn().mockReturnValue(false),
      move: vi.fn().mockReturnValue(false),
      finish: vi.fn().mockReturnValue(null),
    };
    const element = new FakeElement();
    const config = options(drag);
    await attach(element, config);

    element.dispatch("pointerdown", pointerEvent());

    expect(config.onBegin).not.toHaveBeenCalled();
    expect(element.captured).toHaveLength(0);
  });

  it("releases the interaction lease when an unstarted gesture is cancelled", async () => {
    const drag: DragController = {
      begin: vi.fn().mockReturnValue(true),
      move: vi.fn().mockReturnValue(false),
      finish: vi.fn().mockReturnValue({ kind: "cancel", completion: null }),
    };
    const element = new FakeElement();
    const config = options(drag);
    await attach(element, config);

    element.dispatch("pointerdown", pointerEvent({ pointerId: 3 }));
    element.dispatch("pointercancel", pointerEvent({ pointerId: 3 }));

    await vi.waitFor(() => expect(config.finishCapture).toHaveBeenCalledTimes(1));
    expect(config.onClick).not.toHaveBeenCalled();
  });
});
