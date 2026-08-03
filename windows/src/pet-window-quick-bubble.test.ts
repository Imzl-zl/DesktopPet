import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const petWindowScript = readFileSync(new URL("./pet-window.ts", import.meta.url), "utf8");

describe("pet quick-bubble rendering", () => {
  it("uses the timed controller to force a normal render when a quick bubble expires", () => {
    expect(petWindowScript).toContain('from "./quick-bubble"');
    expect(petWindowScript).toContain("new QuickBubbleController(");
    expect(petWindowScript).toContain("quickBubble.current()");
    expect(petWindowScript).toMatch(/\(\) => \{\s*renderSig = "";\s*render\(\);\s*\}/s);
    expect(petWindowScript).toContain("let quickBubbleWasVisible = false;");
    expect(petWindowScript).toContain("if (quickBubbleWasVisible) {");
    expect(petWindowScript).not.toContain("quickBubbleUntil");
  });
});
