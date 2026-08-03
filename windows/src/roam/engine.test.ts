import { describe, expect, it, vi } from "vitest";
import type { Config, Environment } from "./types";

vi.mock("@tauri-apps/api/core", () => ({
  invoke: vi.fn(),
}));

vi.mock("@tauri-apps/api/window", () => ({
  getCurrentWindow: () => ({ label: "pet-test" }),
}));

vi.mock("./environment", () => ({
  fetchEnvironment: vi.fn(),
}));

vi.mock("./modes", () => ({
  runMode: vi.fn(),
}));

vi.mock("./physics", () => ({
  applyFall: vi.fn(),
  applyThrow: vi.fn(),
  cancelThrow: vi.fn(),
  clearSamples: vi.fn(),
  isThrowing: vi.fn(),
  recordSample: vi.fn(),
  releaseVelocity: vi.fn(),
}));

vi.mock("./window", () => ({
  currentLogicalPos: vi.fn(),
  setLogical: vi.fn(),
  setPhysical: vi.fn(),
  setDragPositionTracking: vi.fn(),
}));

function deferred<T>() {
  let resolve: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => { resolve = resolvePromise; });
  return { promise, resolve: resolve! };
}

function config(mode: Config["mode"]): Config {
  return {
    enabled: true,
    mode,
    speed: 5,
    wanderPauseMinMs: 1200,
    wanderPauseMaxMs: 3500,
  };
}

const environment: Environment = {
  workArea: { left: 0, top: 0, right: 1920, bottom: 1080 },
  windows: [],
};

describe("release environment resolution", () => {
  it("uses the final mode when it changes during the climb environment refresh", async () => {
    const firstEnvironment = deferred<Environment | null>();
    const secondEnvironment = deferred<Environment | null>();
    const loadCurrentConfig = vi.fn()
      .mockReturnValueOnce(config("wander"))
      .mockReturnValueOnce(config("climb"))
      .mockReturnValueOnce(config("wander"));
    const fetchCurrentEnvironment = vi.fn()
      .mockReturnValueOnce(firstEnvironment.promise)
      .mockReturnValueOnce(secondEnvironment.promise);
    const { resolveReleaseContext } = await import("./engine");

    const context = resolveReleaseContext(loadCurrentConfig, fetchCurrentEnvironment);
    expect(fetchCurrentEnvironment).toHaveBeenCalledWith(false);

    firstEnvironment.resolve(environment);
    await vi.waitFor(() => {
      expect(fetchCurrentEnvironment).toHaveBeenCalledWith(true);
    });
    secondEnvironment.resolve(environment);

    await expect(context).resolves.toMatchObject({ config: config("wander"), environment });
  });

  it("records start and successful manual positions before ending the drag", async () => {
    const windowApi = await import("./window");
    const physics = await import("./physics");
    vi.mocked(windowApi.currentLogicalPos).mockResolvedValue({ x: 100, y: 200 });
    vi.mocked(windowApi.setPhysical).mockResolvedValue({ x: 150, y: 250 });
    const { beginManualDrag, finishManualDrag, moveManualDrag } = await import("./engine");

    await beginManualDrag();
    await moveManualDrag({ x: 300, y: 500 });
    finishManualDrag();

    expect(physics.recordSample).toHaveBeenNthCalledWith(1, { x: 100, y: 200 });
    expect(windowApi.setPhysical).toHaveBeenCalledWith({ x: 300, y: 500 });
    expect(physics.recordSample).toHaveBeenNthCalledWith(2, { x: 150, y: 250 });
    expect(windowApi.setDragPositionTracking).toHaveBeenLastCalledWith(false);
  });

  it("does not record a late start position after the manual drag has ended", async () => {
    const initialPosition = deferred<{ x: number; y: number }>();
    const windowApi = await import("./window");
    const physics = await import("./physics");
    vi.mocked(windowApi.currentLogicalPos).mockReset().mockReturnValue(initialPosition.promise);
    vi.mocked(physics.recordSample).mockClear();
    const { beginManualDrag, finishManualDrag } = await import("./engine");

    const start = beginManualDrag();
    finishManualDrag();
    initialPosition.resolve({ x: 100, y: 200 });
    await start;

    expect(physics.recordSample).not.toHaveBeenCalled();
  });
});
