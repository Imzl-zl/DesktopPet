import { mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { PNG } from "pngjs";
import { describe, expect, it } from "vitest";
import { slice } from "../../windows/src/pet";
import { encodeSheet, sheets } from "./fixture-sheets";

const fixturesDir = resolve(dirname(fileURLToPath(import.meta.url)), "fixtures");
const realPetsDir = resolve(fixturesDir, "pets");

/** 跑真实宠物包（CDN spritesheet 转 PNG）的 slice()。 */
function slicePngFile(filePath: string) {
  const png = PNG.sync.read(readFileSync(filePath));
  const data = new Uint8ClampedArray(png.data);
  const img = { naturalWidth: png.width, naturalHeight: png.height } as unknown as HTMLImageElement;
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    value: {
      createElement: () => ({
        width: 0,
        height: 0,
        getContext: () => ({
          drawImage: () => {},
          getImageData: () => ({ data }),
        }),
      }),
    },
  });
  return slice(img);
}

/**
 * Runs the REAL windows/src/pet.ts slice() against the shared synthetic test
 * sheets and freezes the results into slice-expected.json + PNG files.
 * DesktopPet.Core.Tests/SpriteSlicerTests asserts the C# port against exactly
 * these artifacts, so both implementations are verified on the same images.
 */
describe("slice reference fixtures", () => {
  it("generates PNGs and the expected slice() output", () => {
    mkdirSync(fixturesDir, { recursive: true });

    const expected: Array<{ name: string; clips: Array<Array<{ x: number; y: number; w: number; h: number }>> }> = [];

    for (const spec of sheets) {
      const png = PNG.sync.read(encodeSheet(spec));
      const data = new Uint8ClampedArray(png.data);

      // Minimal HTMLImageElement shape slice() reads.
      const img = { naturalWidth: spec.width, naturalHeight: spec.height } as unknown as HTMLImageElement;
      // Minimal document.createElement("canvas") shape slice() uses.
      Object.defineProperty(globalThis, "document", {
        configurable: true,
        value: {
          createElement: () => ({
            width: 0,
            height: 0,
            getContext: () => ({
              drawImage: () => {},
              getImageData: () => ({ data }),
            }),
          }),
        },
      });

      const clips = slice(img);
      expected.push({ name: spec.name, clips });

      const pngPath = resolve(fixturesDir, spec.name);
      writeFileSync(pngPath, PNG.sync.write(png));
    }

    const expectedPath = resolve(fixturesDir, "slice-expected.json");
    writeFileSync(expectedPath, JSON.stringify(expected, null, 2) + "\n");

    // Sanity: the expected output is non-trivial for the covered behaviors.
    expect(expected[0].clips).toHaveLength(2); // grid-2x3: two row bands
    expect(expected[0].clips[0]).toHaveLength(3); // three frames per row
    expect(expected[4].clips).toHaveLength(0); // transparent: nothing
  });

  it("slices real CDN pet sheets with the reference implementation", () => {
    const pngFiles = readdirSync(realPetsDir).filter((f) => f.endsWith(".png"));
    expect(pngFiles.length).toBeGreaterThan(0);

    const expected = pngFiles.map((file) => ({
      name: `pets/${file}`,
      clips: slicePngFile(resolve(realPetsDir, file)),
    }));

    const expectedPath = resolve(fixturesDir, "pets-expected.json");
    writeFileSync(expectedPath, JSON.stringify(expected, null, 2) + "\n");

    // 真实图必须切出多行多帧（标准 openpets 网格 9 行 × 8 帧）
    for (const entry of expected) {
      expect(entry.clips.length).toBeGreaterThan(3);
    }
  });
});
