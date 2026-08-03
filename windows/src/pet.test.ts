import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Pet } from "./pet";

class FakeImage {
  static instances: FakeImage[] = [];

  crossOrigin: string | null = null;
  naturalWidth = 80;
  naturalHeight = 90;
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  src = "";

  constructor() {
    FakeImage.instances.push(this);
  }
}

const imageDescriptor = Object.getOwnPropertyDescriptor(globalThis, "Image");
const documentDescriptor = Object.getOwnPropertyDescriptor(globalThis, "document");

function restoreGlobal(name: "Image" | "document", descriptor: PropertyDescriptor | undefined): void {
  if (descriptor) Object.defineProperty(globalThis, name, descriptor);
  else Reflect.deleteProperty(globalThis, name);
}

beforeEach(() => {
  FakeImage.instances = [];
  vi.useFakeTimers();
  Object.defineProperty(globalThis, "Image", { configurable: true, value: FakeImage });
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    value: {
      createElement: () => ({
        width: 0,
        height: 0,
        getContext: () => ({
          drawImage: vi.fn(),
          getImageData: () => ({ data: new Uint8ClampedArray() }),
        }),
      }),
    },
  });
});

afterEach(() => {
  vi.useRealTimers();
  restoreGlobal("Image", imageDescriptor);
  restoreGlobal("document", documentDescriptor);
});

describe("Pet sprite loading", () => {
  it("keeps the most recently requested sprite when an older request completes late", () => {
    const canvas = {
      width: 160,
      height: 180,
      clientWidth: 160,
      clientHeight: 180,
      getContext: () => ({ imageSmoothingEnabled: false }),
    } as unknown as HTMLCanvasElement;
    const pet = new Pet(canvas);

    pet.load("https://example.test/a.png");
    pet.load("https://example.test/b.png");

    const requestA = FakeImage.instances[1];
    const requestB = FakeImage.instances[2];
    requestB.onload?.();
    requestA.onload?.();

    expect((pet as unknown as { img: FakeImage }).img).toBe(requestB);
  });

  it("does not start a fallback request for an older sprite request", () => {
    const canvas = {
      width: 160,
      height: 180,
      clientWidth: 160,
      clientHeight: 180,
      getContext: () => ({ imageSmoothingEnabled: false }),
    } as unknown as HTMLCanvasElement;
    const pet = new Pet(canvas);

    pet.load("https://example.test/a.png");
    pet.load("https://example.test/b.png");
    const requestA = FakeImage.instances[1];
    requestA.onerror?.();

    expect(FakeImage.instances).toHaveLength(3);
  });

  it("retries the current sprite after both image requests fail", () => {
    const canvas = {
      width: 160,
      height: 180,
      clientWidth: 160,
      clientHeight: 180,
      getContext: () => ({ imageSmoothingEnabled: false }),
    } as unknown as HTMLCanvasElement;
    const pet = new Pet(canvas);

    pet.load("https://example.test/a.png");
    FakeImage.instances[1].onerror?.();
    FakeImage.instances[2].onerror?.();
    pet.load("https://example.test/a.png");

    expect(FakeImage.instances).toHaveLength(4);
  });
});
