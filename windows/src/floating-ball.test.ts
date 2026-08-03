import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const floatingBallScript = readFileSync(new URL("./floating-ball.ts", import.meta.url), "utf8");
const pointerBinding = readFileSync(new URL("./floating-ball-pointer.ts", import.meta.url), "utf8");
const nativeWindowCode = readFileSync(new URL("../src-tauri/src/lib.rs", import.meta.url), "utf8");
const styles = readFileSync(new URL("./styles.css", import.meta.url), "utf8");

describe("floating ball drag rendering", () => {
  it("uses pointer capture for click detection and the native drag loop for moving", () => {
    expect(floatingBallScript).toContain("win.startDragging()");
    expect(floatingBallScript).toContain("attachFloatingBallPointerDrag(ball");
    expect(floatingBallScript).toContain("new PhysicalPosition(position.x, position.y)");
    expect(pointerBinding).toContain("ball.setPointerCapture(e.pointerId)");
    expect(pointerBinding).toContain('ball.addEventListener("pointercancel"');
    expect(pointerBinding).toContain('ball.addEventListener("lostpointercapture"');
  });

  it("keeps the user's drop position without a second native window move", () => {
    expect(floatingBallScript).toContain("cursorPosition,");
    expect(floatingBallScript).toContain("new PhysicalPosition(position.x, position.y)");
    expect(floatingBallScript).toContain('persistPosition: () => invoke<void>("persist_floating_ball_position")');
    expect(floatingBallScript).not.toContain("snap_floating_ball");
    expect(nativeWindowCode).toContain("fn persist_floating_ball_position");
    expect(nativeWindowCode).not.toContain("fn snap_floating_ball");
    const persistCommand = nativeWindowCode.match(
      /fn persist_floating_ball_position\([^)]*\)\s*\{.*?^\}/ms,
    )?.[0] ?? "";
    expect(persistCommand).not.toContain("set_position");
  });

  it("removes external compositing effects during an OS drag", () => {
    expect(styles).toMatch(
      /#ball\.dragging,\s*\.floating-ball-body #ball\.dragging:hover\s*\{[^}]*transform:\s*none;[^}]*filter:\s*none;[^}]*transition:\s*none;[^}]*box-shadow:\s*none;/s,
    );
  });
});
