import { beforeEach, describe, expect, it, vi } from "vitest";

const nativeWindow = vi.hoisted(() => ({
  scaleFactor: vi.fn(),
  outerPosition: vi.fn(),
  setPosition: vi.fn(),
  onMoved: vi.fn(),
  onScaleChanged: vi.fn(),
}));

vi.mock("@tauri-apps/api/window", () => ({
  getCurrentWindow: () => nativeWindow,
}));

vi.mock("@tauri-apps/api/dpi", () => ({
  LogicalPosition: class {
    constructor(readonly x: number, readonly y: number) {}
  },
  PhysicalPosition: class {
    constructor(readonly x: number, readonly y: number) {}
  },
}));

function deferred<T>() {
  let resolve: (value: T) => void;
  const promise = new Promise<T>((r) => { resolve = r; });
  return { promise, resolve: resolve! };
}

beforeEach(() => {
  vi.resetModules();
  nativeWindow.scaleFactor.mockReset().mockResolvedValue(2);
  nativeWindow.outerPosition.mockReset().mockResolvedValue({ x: 240, y: 480 });
  nativeWindow.setPosition.mockReset().mockResolvedValue(undefined);
  nativeWindow.onMoved.mockReset().mockResolvedValue(() => {});
  nativeWindow.onScaleChanged.mockReset().mockResolvedValue(() => {});
});

describe("roam window position", () => {
  it("reuses the last native position between movement ticks", async () => {
    const { currentLogicalPos } = await import("./window");

    await expect(currentLogicalPos()).resolves.toEqual({ x: 120, y: 240 });
    await expect(currentLogicalPos()).resolves.toEqual({ x: 120, y: 240 });

    expect(nativeWindow.scaleFactor).toHaveBeenCalledTimes(1);
    expect(nativeWindow.outerPosition).toHaveBeenCalledTimes(1);
  });

  it("updates the cache from native drag move events", async () => {
    let movedHandler: ((event: { payload: { x: number; y: number } }) => void) | undefined;
    nativeWindow.onMoved.mockImplementation(async (handler) => {
      movedHandler = handler;
      return () => {};
    });
    const { currentLogicalPos, setDragPositionTracking } = await import("./window");

    await currentLogicalPos();
    setDragPositionTracking(true);
    expect(nativeWindow.onMoved).toHaveBeenCalledTimes(1);

    movedHandler?.({ payload: { x: 600, y: 360 } });
    await expect(currentLogicalPos()).resolves.toEqual({ x: 300, y: 180 });
    expect(nativeWindow.outerPosition).toHaveBeenCalledTimes(1);
  });

  it("refreshes the native position after a scale change", async () => {
    let scaleHandler: ((event: { payload: { scaleFactor: number } }) => void) | undefined;
    nativeWindow.onScaleChanged.mockImplementation(async (handler) => {
      scaleHandler = handler;
      return () => {};
    });
    const { currentLogicalPos } = await import("./window");

    await currentLogicalPos();
    expect(nativeWindow.onScaleChanged).toHaveBeenCalledTimes(1);

    nativeWindow.scaleFactor.mockResolvedValue(1.5);
    nativeWindow.outerPosition.mockResolvedValue({ x: 300, y: 450 });
    scaleHandler?.({ payload: { scaleFactor: 1.5 } });

    await expect(currentLogicalPos()).resolves.toEqual({ x: 200, y: 300 });
    expect(nativeWindow.outerPosition).toHaveBeenCalledTimes(2);
  });

  it("uses the native scale factor for a native drag move", async () => {
    let movedHandler: ((event: { payload: { x: number; y: number } }) => void) | undefined;
    nativeWindow.onMoved.mockImplementation(async (handler) => {
      movedHandler = handler;
      return () => {};
    });
    const { currentLogicalPos, setDragPositionTracking } = await import("./window");

    await currentLogicalPos();
    setDragPositionTracking(true);
    nativeWindow.scaleFactor.mockResolvedValue(1.5);
    movedHandler?.({ payload: { x: 300, y: 450 } });

    await expect(currentLogicalPos()).resolves.toEqual({ x: 200, y: 300 });
    expect(nativeWindow.outerPosition).toHaveBeenCalledTimes(1);
  });

  it("does not let a late automatic move event overwrite a newer logical target", async () => {
    let movedHandler: ((event: { payload: { x: number; y: number } }) => void) | undefined;
    nativeWindow.onMoved.mockImplementation(async (handler) => {
      movedHandler = handler;
      return () => {};
    });
    const { currentLogicalPos, setLogical } = await import("./window");

    await currentLogicalPos();
    await setLogical({ x: 150, y: 250 });
    movedHandler?.({ payload: { x: 150, y: 250 } });

    await expect(currentLogicalPos()).resolves.toEqual({ x: 150, y: 250 });
  });

  it("does not accept a stale read while a programmatic move is pending", async () => {
    const initialPosition = deferred<{ x: number; y: number }>();
    const setComplete = deferred<void>();
    nativeWindow.outerPosition.mockImplementationOnce(() => initialPosition.promise);
    nativeWindow.setPosition.mockImplementationOnce(() => setComplete.promise);
    const { currentLogicalPos, setLogical } = await import("./window");

    const initialRead = currentLogicalPos();
    await vi.waitFor(() => expect(nativeWindow.outerPosition).toHaveBeenCalledTimes(1));
    const move = setLogical({ x: 75, y: 125 });
    initialPosition.resolve({ x: 240, y: 480 });

    await expect(initialRead).resolves.toBeNull();
    setComplete.resolve();
    await move;
    await expect(currentLogicalPos()).resolves.toEqual({ x: 75, y: 125 });
  });

  it("keeps the latest programmatic position when updates complete out of order", async () => {
    const firstComplete = deferred<void>();
    const secondComplete = deferred<void>();
    nativeWindow.setPosition
      .mockImplementationOnce(() => firstComplete.promise)
      .mockImplementationOnce(() => secondComplete.promise);
    const { currentLogicalPos, setLogical } = await import("./window");

    const firstMove = setLogical({ x: 75, y: 125 });
    const secondMove = setLogical({ x: 150, y: 250 });
    secondComplete.resolve();
    await secondMove;
    firstComplete.resolve();
    await firstMove;

    await expect(currentLogicalPos()).resolves.toEqual({ x: 150, y: 250 });
  });

  it("refreshes after scale invalidates a pending native drag update", async () => {
    const movedScale = deferred<number>();
    let movedHandler: ((event: { payload: { x: number; y: number } }) => void) | undefined;
    let scaleHandler: (() => void) | undefined;
    nativeWindow.onMoved.mockImplementation(async (handler) => {
      movedHandler = handler;
      return () => {};
    });
    nativeWindow.onScaleChanged.mockImplementation(async (handler) => {
      scaleHandler = handler;
      return () => {};
    });
    const { currentLogicalPos, setDragPositionTracking } = await import("./window");

    await currentLogicalPos();
    setDragPositionTracking(true);
    nativeWindow.scaleFactor.mockImplementationOnce(() => movedScale.promise);
    movedHandler?.({ payload: { x: 300, y: 450 } });
    scaleHandler?.();
    nativeWindow.scaleFactor.mockResolvedValue(1.5);
    nativeWindow.outerPosition.mockResolvedValue({ x: 300, y: 450 });
    movedScale.resolve(1.5);

    await expect(currentLogicalPos()).resolves.toEqual({ x: 200, y: 300 });
    expect(nativeWindow.outerPosition).toHaveBeenCalledTimes(2);
  });

  it("retries tracking after a synchronous registration failure", async () => {
    nativeWindow.onMoved
      .mockReset()
      .mockImplementationOnce(() => { throw new Error("listener unavailable"); })
      .mockResolvedValueOnce(() => {});
    nativeWindow.onScaleChanged.mockReset().mockResolvedValue(() => {});
    const { currentLogicalPos } = await import("./window");

    await expect(currentLogicalPos()).resolves.toEqual({ x: 120, y: 240 });
    await Promise.resolve();
    await currentLogicalPos();

    expect(nativeWindow.onMoved).toHaveBeenCalledTimes(2);
  });

  it("updates the cache after setting a logical position", async () => {
    const { currentLogicalPos, setLogical } = await import("./window");

    await currentLogicalPos();
    await setLogical({ x: 75, y: 125 });

    await expect(currentLogicalPos()).resolves.toEqual({ x: 75, y: 125 });
    expect(nativeWindow.outerPosition).toHaveBeenCalledTimes(1);
  });

  it("does not let a stale read overwrite a newer native drag move", async () => {
    const scale = deferred<number>();
    const position = deferred<{ x: number; y: number }>();
    let movedHandler: ((event: { payload: { x: number; y: number } }) => void) | undefined;
    let scaleHandler: ((event: { payload: { scaleFactor: number } }) => void) | undefined;
    nativeWindow.scaleFactor.mockImplementationOnce(() => scale.promise);
    nativeWindow.outerPosition.mockImplementationOnce(() => position.promise);
    nativeWindow.onMoved.mockImplementation(async (handler) => {
      movedHandler = handler;
      return () => {};
    });
    nativeWindow.onScaleChanged.mockImplementation(async (handler) => {
      scaleHandler = handler;
      return () => {};
    });
    const { currentLogicalPos, setDragPositionTracking } = await import("./window");

    const firstRead = currentLogicalPos();
    scale.resolve(1);
    await vi.waitFor(() => expect(nativeWindow.outerPosition).toHaveBeenCalledTimes(1));
    setDragPositionTracking(true);
    nativeWindow.scaleFactor.mockResolvedValue(1.5);

    scaleHandler?.({ payload: { scaleFactor: 1.5 } });
    movedHandler?.({ payload: { x: 300, y: 450 } });
    position.resolve({ x: 300, y: 450 });

    await expect(firstRead).resolves.toEqual({ x: 200, y: 300 });
    await expect(currentLogicalPos()).resolves.toEqual({ x: 200, y: 300 });
  });

  it("retries event tracking after a failed registration", async () => {
    const failedRegistration = Promise.reject<() => void>(new Error("listener unavailable"));
    void failedRegistration.catch(() => {});
    nativeWindow.onMoved
      .mockReset()
      .mockImplementationOnce(() => failedRegistration)
      .mockResolvedValueOnce(() => {});
    nativeWindow.onScaleChanged.mockReset().mockResolvedValue(() => {});
    const { currentLogicalPos } = await import("./window");

    await currentLogicalPos();
    await vi.waitFor(async () => {
      await currentLogicalPos();
      expect(nativeWindow.onMoved).toHaveBeenCalledTimes(2);
    });
  });

  it("cleans up a partial registration when scale tracking fails", async () => {
    const movedUnlisten = vi.fn();
    const failedRegistration = Promise.reject<() => void>(new Error("listener unavailable"));
    void failedRegistration.catch(() => {});
    nativeWindow.onMoved
      .mockReset()
      .mockResolvedValueOnce(movedUnlisten)
      .mockResolvedValueOnce(() => {});
    nativeWindow.onScaleChanged
      .mockReset()
      .mockImplementationOnce(() => failedRegistration)
      .mockResolvedValueOnce(() => {});
    const { currentLogicalPos } = await import("./window");

    await currentLogicalPos();
    await vi.waitFor(async () => {
      await currentLogicalPos();
      expect(movedUnlisten).toHaveBeenCalledTimes(1);
      expect(nativeWindow.onMoved).toHaveBeenCalledTimes(2);
      expect(nativeWindow.onScaleChanged).toHaveBeenCalledTimes(2);
    });
  });

  it("updates the cache from a manual physical position write", async () => {
    nativeWindow.scaleFactor.mockResolvedValue(1.5);
    const { currentLogicalPos, setPhysical } = await import("./window");

    await setPhysical({ x: 300, y: 450 });

    expect(nativeWindow.setPosition).toHaveBeenCalledWith(expect.objectContaining({ x: 300, y: 450 }));
    await expect(currentLogicalPos()).resolves.toEqual({ x: 200, y: 300 });
    expect(nativeWindow.outerPosition).not.toHaveBeenCalled();
  });
});

