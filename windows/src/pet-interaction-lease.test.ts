import { describe, expect, it, vi } from "vitest";

type LeaseHost = {
  activate(): Promise<void>;
  deactivate(): Promise<void>;
  reportError(error: unknown): void;
};
type Lease = {
  begin(): void;
  wait(): Promise<void>;
  finish(): Promise<void>;
};
type LeaseModule = {
  PetInteractionLease: new (host: LeaseHost) => Lease;
};

const leaseModulePath = "./pet-interaction-lease";

async function createLease(host: LeaseHost): Promise<Lease> {
  const module = await import(/* @vite-ignore */ leaseModulePath).catch(() => null);
  expect(module).not.toBeNull();
  return new (module as LeaseModule).PetInteractionLease(host);
}

function deferred<T>() {
  let resolve: (value: T) => void;
  const promise = new Promise<T>((r) => { resolve = r; });
  return { promise, resolve: resolve! };
}

describe("pet interaction lease", () => {
  it("does not let a late first release deactivate a newer gesture", async () => {
    const firstActivation = deferred<void>();
    const secondActivation = deferred<void>();
    const host: LeaseHost = {
      activate: vi.fn()
        .mockReturnValueOnce(firstActivation.promise)
        .mockReturnValueOnce(secondActivation.promise),
      deactivate: vi.fn().mockResolvedValue(undefined),
      reportError: vi.fn(),
    };
    const lease = await createLease(host);

    lease.begin();
    expect(host.activate).toHaveBeenCalledTimes(1);
    const firstFinish = lease.finish();
    lease.begin();
    firstActivation.resolve();
    await firstFinish;

    expect(host.deactivate).not.toHaveBeenCalled();

    secondActivation.resolve();
    await lease.finish();
    expect(host.deactivate).toHaveBeenCalledTimes(1);
  });

  it("releases a failed acquisition so the native interaction state cannot remain stale", async () => {
    const failure = new Error("native lease failed");
    const host: LeaseHost = {
      activate: vi.fn().mockRejectedValue(failure),
      deactivate: vi.fn().mockResolvedValue(undefined),
      reportError: vi.fn(),
    };
    const lease = await createLease(host);

    lease.begin();
    await lease.finish();

    expect(host.reportError).toHaveBeenCalledWith(failure);
    expect(host.deactivate).toHaveBeenCalledTimes(1);
  });
});
