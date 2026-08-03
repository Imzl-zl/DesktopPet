import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const petWindowScript = readFileSync(new URL("./pet-window.ts", import.meta.url), "utf8");
const nativeWindowCode = readFileSync(new URL("../src-tauri/src/lib.rs", import.meta.url), "utf8");
const engineScript = readFileSync(new URL("./roam/engine.ts", import.meta.url), "utf8");
const capabilityConfig = readFileSync(new URL("../src-tauri/capabilities/default.json", import.meta.url), "utf8");

describe("pet manual drag integration", () => {
  it("uses pointer capture, an interaction lease, and explicit final persistence", () => {
    expect(petWindowScript).not.toContain(".startDragging()");
    expect(petWindowScript).toContain('from "./window-drag"');
    expect(petWindowScript).toContain('from "./pet-pointer-drag"');
    expect(petWindowScript).toContain("new WindowDragController(");
    expect(petWindowScript).toContain("attachPetPointerDrag(canvas");
    expect(petWindowScript).toContain("attachPetPointerDrag(bubbleEl");
    expect(petWindowScript).toContain("beginManualDrag");
    expect(petWindowScript).toContain("moveManualDrag");
    expect(petWindowScript).toContain("finishManualDrag");
    expect(petWindowScript).toContain("startPetInteractionLease");
    expect(petWindowScript).toContain("onBegin: beginPetInteraction");
    expect(petWindowScript).toContain("finishCapture: finishPetInteractionLease");
    expect(petWindowScript).toContain('"set_pet_dragging"');
    expect(petWindowScript).toContain('"persist_pet_position"');

    expect(nativeWindowCode).toContain("fn set_pet_dragging");
    expect(nativeWindowCode).toContain("fn persist_pet_position");
    expect(nativeWindowCode).toContain("should_ignore_cursor_events(active_drags.contains(label), inside)");
    expect(engineScript).toContain("await persistPetPosition();");
    expect(capabilityConfig).not.toContain("core:window:allow-start-dragging");
  });
});
