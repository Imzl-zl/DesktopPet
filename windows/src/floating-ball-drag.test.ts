import { describe, expect, it, vi } from "vitest";

type Point = { x: number; y: number };
type DragHost = {
  cursorPosition(): Promise<Point>;
  setPosition(position: Point): Promise<void>;
  persistPosition(): Promise<void>;
};
type DragOutcome = {
  kind: "click" | "drag" | "cancel";
  completion: Promise<void> | null;
};
type DragController = {
  readonly isDragging: boolean;
  begin(pointerId: number, point: Point, grabOffset: Point, startedAt: number): boolean;
  move(pointerId: number, point: Point): boolean;
  finish(pointerId: number, point: Point, allowClick: boolean, now: number): DragOutcome | null;
};
type DragModule = {
  FloatingBallDragController: new (host: DragHost, thresholdPx?: number, clickMaxMs?: number) => DragController;
};

const dragModulePath = "./floating-ball-drag";

async function createController(host: DragHost): Promise<DragController> {
  const module = await import(/* @vite-ignore */ dragModulePath).catch(() => null);
  expect(module).not.toBeNull();
  return new (module as DragModule).FloatingBallDragController(host);
}

function deferred<T>() {
  let resolve: (value: T) => void;
  const promise = new Promise<T>((r) => { resolve = r; });
  return { promise, resolve: resolve! };
}

describe("floating ball pointer drag", () => {
  it("keeps a short stationary press as a menu click", async () => {
    const host: DragHost = {
      cursorPosition: vi.fn(),
      setPosition: vi.fn(),
      persistPosition: vi.fn(),
    };
    const drag = await createController(host);

    expect(drag.begin(1, { x: 40, y: 50 }, { x: 20, y: 30 }, 100)).toBe(true);
    const result = drag.finish(1, { x: 42, y: 53 }, true, 200);

    expect(result).toEqual({ kind: "click", completion: null });
    expect(host.cursorPosition).not.toHaveBeenCalled();
    expect(host.setPosition).not.toHaveBeenCalled();
    expect(host.persistPosition).not.toHaveBeenCalled();
  });

  it("uses physical cursor coordinates and persists only after the final move", async () => {
    const firstMove = deferred<void>();
    const finalMove = deferred<void>();
    const host: DragHost = {
      cursorPosition: vi.fn()
        .mockResolvedValueOnce({ x: 120, y: 130 })
        .mockResolvedValueOnce({ x: 2600, y: 480 }),
      setPosition: vi.fn()
        .mockImplementationOnce(() => firstMove.promise)
        .mockImplementationOnce(() => finalMove.promise),
      persistPosition: vi.fn().mockResolvedValue(undefined),
    };
    const drag = await createController(host);

    drag.begin(7, { x: 10, y: 20 }, { x: 20, y: 30 }, 0);
    expect(drag.move(7, { x: 20, y: 20 })).toBe(true);
    await vi.waitFor(() => expect(host.setPosition).toHaveBeenCalledWith({ x: 100, y: 100 }));

    const result = drag.finish(7, { x: 30, y: 20 }, true, 80);
    expect(result?.kind).toBe("drag");
    expect(host.persistPosition).not.toHaveBeenCalled();

    firstMove.resolve();
    await vi.waitFor(() => expect(host.setPosition).toHaveBeenCalledWith({ x: 2580, y: 450 }));
    expect(host.persistPosition).not.toHaveBeenCalled();

    finalMove.resolve();
    await result?.completion;
    expect(host.persistPosition).toHaveBeenCalledTimes(1);
  });

  it("surfaces an earlier move failure after a later final move persists", async () => {
    const moveFailure = new Error("native move failed");
    const host: DragHost = {
      cursorPosition: vi.fn()
        .mockResolvedValueOnce({ x: 120, y: 130 })
        .mockResolvedValue({ x: 260, y: 370 }),
      setPosition: vi.fn()
        .mockImplementationOnce(() => { throw moveFailure; })
        .mockResolvedValueOnce(undefined),
      persistPosition: vi.fn().mockResolvedValue(undefined),
    };
    const drag = await createController(host);

    drag.begin(5, { x: 0, y: 0 }, { x: 20, y: 30 }, 0);
    expect(drag.move(5, { x: 8, y: 0 })).toBe(true);
    await vi.waitFor(() => expect(host.setPosition).toHaveBeenCalledTimes(1));

    drag.move(5, { x: 12, y: 0 });
    const result = drag.finish(5, { x: 16, y: 0 }, true, 40);
    expect(result?.kind).toBe("drag");
    await expect(result!.completion).rejects.toBe(moveFailure);
    expect(host.persistPosition).toHaveBeenCalledTimes(1);
  });

  it("persists a cancelled drag without turning it into a click", async () => {
    const host: DragHost = {
      cursorPosition: vi.fn().mockResolvedValue({ x: 240, y: 360 }),
      setPosition: vi.fn().mockResolvedValue(undefined),
      persistPosition: vi.fn().mockResolvedValue(undefined),
    };
    const drag = await createController(host);

    drag.begin(3, { x: 0, y: 0 }, { x: 10, y: 10 }, 0);
    expect(drag.move(3, { x: 8, y: 0 })).toBe(true);
    const result = drag.finish(3, { x: 12, y: 0 }, false, 20);

    expect(result?.kind).toBe("drag");
    await result?.completion;
    expect(drag.isDragging).toBe(false);
    expect(host.setPosition).toHaveBeenLastCalledWith({ x: 230, y: 350 });
    expect(host.persistPosition).toHaveBeenCalledTimes(1);
  });
});
