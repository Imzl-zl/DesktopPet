import { PNG } from "pngjs";

/**
 * Shared slice-test fixture layouts — THE single source of truth for the
 * "same batch of test images". The C# side (SpriteSlicerTests) only decodes
 * the PNG files produced here; it never re-specifies the layouts.
 *
 * Every sheet exercises a distinct alpha-gutter slice() behavior:
 *  - grid-2x3:      uniform 2 rows × 3 cols with padding + gutters (mirrors
 *                   the macOS SpriteSlicerTests synthesis sheet)
 *  - ragged:        rows with different frame counts + a fully transparent row
 *  - alpha-edge:    ALPHA_THRESHOLD=16 boundary (15/16 excluded, 17 included)
 *  - single-row:    one centered frame with padding
 *  - transparent:   fully transparent → empty result
 *  - touching:      adjacent frames without a gutter merge into one frame
 */

export interface SheetSpec {
  name: string;
  width: number;
  height: number;
  fill: (buf: Uint8Array, width: number, height: number) => void;
}

function rect(
  buf: Uint8Array,
  width: number,
  x: number,
  y: number,
  w: number,
  h: number,
  rgba: [number, number, number, number],
): void {
  for (let yy = y; yy < y + h; yy++) {
    for (let xx = x; xx < x + w; xx++) {
      const off = (yy * width + xx) * 4;
      buf[off] = rgba[0];
      buf[off + 1] = rgba[1];
      buf[off + 2] = rgba[2];
      buf[off + 3] = rgba[3];
    }
  }
}

export const sheets: SheetSpec[] = [
  {
    // pad 6 / gutter 8 / cell 20×24 / 2 rows × 3 cols — same parameters as the
    // macOS SpriteSlicerTests.makeSynthesisSheet()
    name: "grid-2x3.png",
    width: 6 + 20 + 8 + 20 + 8 + 20 + 6,
    height: 6 + 24 + 8 + 24 + 6,
    fill(buf, w) {
      const cellW = 20, cellH = 24, pad = 6, gutter = 8;
      const rowColors: Array<[number, number, number]> = [
        [255, 0, 0], [0, 255, 0], [0, 0, 255],
        [0, 255, 255], [255, 0, 255], [255, 255, 0],
      ];
      for (let row = 0; row < 2; row++) {
        for (let col = 0; col < 3; col++) {
          const [r, g, b] = rowColors[row * 3 + col];
          rect(buf, w, pad + col * (cellW + gutter), pad + row * (cellH + gutter), cellW, cellH, [r, g, b, 255]);
        }
      }
    },
  },
  {
    // Row 0: two frames; transparent row; row 1: one wider frame.
    name: "ragged.png",
    width: 60,
    height: 64,
    fill(buf, w) {
      rect(buf, w, 0, 0, 20, 24, [255, 0, 0, 255]);
      rect(buf, w, 30, 0, 20, 24, [0, 255, 0, 255]);
      // y 24..39 fully transparent
      rect(buf, w, 5, 40, 32, 24, [0, 0, 255, 255]);
    },
  },
  {
    // ALPHA_THRESHOLD = 16; only alpha > 16 counts. 15 and 16 are excluded,
    // 17 is included.
    name: "alpha-edge.png",
    width: 60,
    height: 10,
    fill(buf, w) {
      rect(buf, w, 0, 0, 10, 10, [255, 0, 0, 15]);
      rect(buf, w, 20, 0, 10, 10, [0, 255, 0, 16]);
      rect(buf, w, 40, 0, 10, 10, [0, 0, 255, 17]);
    },
  },
  {
    name: "single-row.png",
    width: 40,
    height: 50,
    fill(buf, w) {
      rect(buf, w, 5, 5, 30, 40, [255, 128, 0, 255]);
    },
  },
  {
    name: "transparent.png",
    width: 60,
    height: 40,
    fill() { /* all pixels stay alpha 0 */ },
  },
  {
    // No gutter between the two frames → they merge into one 40px frame.
    name: "touching.png",
    width: 40,
    height: 20,
    fill(buf, w) {
      rect(buf, w, 0, 0, 20, 20, [255, 0, 0, 255]);
      rect(buf, w, 20, 0, 20, 20, [0, 255, 0, 255]);
    },
  },
];

/** Encodes a sheet spec into a PNG buffer (RGBA). */
export function encodeSheet(spec: SheetSpec): Buffer {
  const buf = new Uint8Array(spec.width * spec.height * 4);
  spec.fill(buf, spec.width, spec.height);
  const png = new PNG({ width: spec.width, height: spec.height });
  png.data = Buffer.from(buf);
  return PNG.sync.write(png);
}
