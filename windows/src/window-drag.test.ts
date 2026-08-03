import { describe, expect, it, vi } from "vitest";

type Point = { x: number; y: number };
type DragHost = {
  cursorPosition(): Promise<Point>;
  setPosition(position: Point): Promise<void>;
  startDrag(): void | Promise<void>;
  finishDrag(): Promise<void>;
};
type DragOutcome = {
  kind: "click" | "drag" | "cancel";
  completion: Promise<void> | null;
};
type DragController = {
  begin(pointerId: number, point: Point, grabOffset: Point, startedAt: number): boolean;
  move(pointerId: number, point: Point): boolean;
  finish(pointerId: number, point: Point, allowClick: boolean, now: number): DragOutcome | null;
};
type DragModule = {
  WindowDragController: new (host: DragHost, thresholdPx?: number, clickMaxMs?: number) => DragController;
};

const dragModulePath = "./window-drag";

async function createController(host: DragHost): Promise<DragController> {
  const module = await import(/* @vite-ignore */ dragModulePath).catch(() => null);
  expect(module).not.toBeNull();
  return new (module as DragModule).WindowDragController(host);
}

function deferred<T>() {
  let resolve: (value: T) => void;
  const promise = new Promise<T>((r) => { resolve = r; });
  return { promise, resolve: resolve! };
}

describe("window pointer drag controller", () => {
  it("lets the system drag position the window and finishes without a pointerup", async () => {
    const systemDrag = deferred<void>();
    const host: DragHost = {
      cursorPosition: vi.fn().mockResolvedValue({ x: 120, y: 130 }),
      setPosition: vi.fn().mockResolvedValue(undefined),
      startDrag: vi.fn(() => systemDrag.promise),
      finishDrag: vi.fn().mockResolvedValue(undefined),
    };
    const drag = await createController(host);

    expect(drag.begin(7, { x: 10, y: 20 }, { x: 20, y: 30 }, 0)).toBe(true);
    expect(drag.move(7, { x: 20, y: 20 })).toBe(true);
    expect(host.startDrag).toHaveBeenCalledTimes(1);

    // While the system drag is active no manual moves happen.
    await vi.waitFor(() => expect(host.setPosition).not.toHaveBeenCalled());
    expect(host.finishDrag).not.toHaveBeenCalled();
    expect(drag.begin(8, { x: 40, y: 20 }, { x: 20, y: 30 }, 90)).toBe(false);

    // Mouse released: the system drag resolves and the controller finishes
    // on its own — no pointerup arrives in the webview.
    systemDrag.resolve();
    await vi.waitFor(() => expect(host.finishDrag).toHaveBeenCalledTimes(1));
    expect(host.setPosition).not.toHaveBeenCalled();

    // State was cleaned up: a new drag can begin.
    expect(drag.begin(8, { x: 40, y: 20 }, { x: 20, y: 30 }, 90)).toBe(true);
  });

  it("falls back to manual pointer-driven moves when the system drag is unavailable", async () => {
    const firstMove = deferred<void>();
    const finalMove = deferred<void>();
    const host: DragHost = {
      cursorPosition: vi.fn()
        .mockResolvedValueOnce({ x: 120, y: 130 })
        .mockResolvedValueOnce({ x: 2600, y: 480 }),
      setPosition: vi.fn()
        .mockImplementationOnce(() => firstMove.promise)
        .mockImplementationOnce(() => finalMove.promise),
      startDrag: vi.fn(() => Promise.reject(new Error("system drag unavailable"))),
      finishDrag: vi.fn().mockResolvedValue(undefined),
    };
    const drag = await createController(host);

    expect(drag.begin(7, { x: 10, y: 20 }, { x: 20, y: 30 }, 0)).toBe(true);
    expect(drag.move(7, { x: 20, y: 20 })).toBe(true);
    await vi.waitFor(() => expect(host.setPosition).toHaveBeenCalledWith({ x: 100, y: 100 }));
    expect(host.startDrag).toHaveBeenCalledTimes(1);

    const result = drag.finish(7, { x: 30, y: 20 }, true, 80);
    expect(result?.kind).toBe("drag");
    expect(drag.begin(8, { x: 40, y: 20 }, { x: 20, y: 30 }, 90)).toBe(false);
    expect(host.finishDrag).not.toHaveBeenCalled();

    firstMove.resolve();
    await vi.waitFor(() => expect(host.setPosition).toHaveBeenCalledWith({ x: 2580, y: 450 }));
    expect(host.finishDrag).not.toHaveBeenCalled();

    finalMove.resolve();
    await result?.completion;
    expect(host.finishDrag).toHaveBeenCalledTimes(1);
    expect(drag.begin(8, { x: 40, y: 20 }, { x: 20, y: 30 }, 90)).toBe(true);
  });

  it("does not move the window while the system drag is still active", async () => {
    const setup = deferred<void>();
    const host: DragHost = {
      cursorPosition: vi.fn().mockResolvedValue({ x: 120, y: 130 }),
      setPosition: vi.fn().mockResolvedValue(undefined),
      startDrag: vi.fn(() => setup.promise),
      finishDrag: vi.fn().mockResolvedValue(undefined),
    };
    const drag = await createController(host);

    drag.begin(1, { x: 0, y: 0 }, { x: 20, y: 30 }, 0);
    drag.move(1, { x: 8, y: 0 });
    await Promise.resolve();
    expect(host.setPosition).not.toHaveBeenCalled();

    setup.resolve();
    await vi.waitFor(() => expect(host.finishDrag).toHaveBeenCalledTimes(1));
    expect(host.setPosition).not.toHaveBeenCalled();
  });
});
