import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import settingsHtml from "../settings.html?raw";
import settingsScript from "./settings.ts?raw";

const styles = readFileSync(new URL("./styles.css", import.meta.url), "utf8");

describe("settings page structure", () => {
  it("keeps desktop instances and library materials in one explicit surface each", () => {
    expect(settingsHtml).toContain('id="desktop-display-controls"');
    expect(settingsHtml).toContain('id="desktop-instance-list"');
    expect(settingsHtml).toContain('id="pet-library"');
    expect(settingsHtml).not.toContain('id="extra-grid"');
    expect(settingsHtml).not.toContain('href="/src/styles.css"');
  });

  it("places reusable library materials before desktop instances", () => {
    expect(settingsHtml.indexOf('id="pet-library"')).toBeLessThan(
      settingsHtml.indexOf('id="desktop-pets"'),
    );
  });

  it("does not couple library cards to the edited desktop pet", () => {
    expect(settingsScript).not.toContain("current-material");
    expect(settingsScript).not.toContain("Replace selected desktop pet");
  });


  it("loads the living sidebar preview whenever the current pet is refreshed", () => {
    const showCurrent = settingsScript.split("/// Removes the selected desktop instance.")[0];
    expect(showCurrent).toContain("livingPets.loadPet(sel?.url ?? null);");
  });

  it("does not request a sprite when no pet has been selected yet", () => {
    expect(settingsScript).not.toContain('stagePet?.load(url ?? "");');
  });

  it("does not force every settings page into an implicit two-column grid", () => {
    expect(styles).not.toMatch(/\.page\.sel\s*\{[^}]*grid-template-columns:\s*1fr\s+1fr/s);
  });

  it("gives image generation a dedicated controls-and-canvas workspace", () => {
    const imagePage = settingsHtml.split('data-page="image"')[1];

    expect(imagePage).toContain('class="ig-workspace"');
    expect(imagePage).toContain('class="ig-controls"');
    expect(imagePage).toContain('class="ig-canvas"');
    expect(imagePage.indexOf('class="ig-controls"')).toBeLessThan(
      imagePage.indexOf('class="ig-canvas"'),
    );
  });

  it("keeps connection settings secondary and result states on the canvas", () => {
    const imagePage = settingsHtml.split('data-page="image"')[1];

    expect(imagePage).toContain('class="ig-service-panel"');
    expect(imagePage).toContain('id="ig-empty"');
    expect(imagePage).toContain('id="ig-progress"');
    expect(imagePage.indexOf('id="ig-empty"')).toBeGreaterThan(
      imagePage.indexOf('class="ig-canvas"'),
    );
    expect(imagePage.indexOf('id="ig-result"')).toBeGreaterThan(
      imagePage.indexOf('class="ig-canvas"'),
    );
  });

  it("expands the settings shell into image-workspace mode", () => {
    expect(settingsScript).toContain('classList.toggle("image-workspace-active"');
    expect(styles).toMatch(/\.image-workspace-active\s+\.pet-panel\s*\{/);
    expect(styles).toMatch(/\.ig-workspace\s*\{[^}]*grid-template-columns:/s);
  });

  it("preserves a saved custom model when the live model list refreshes", () => {
    expect(settingsScript).toContain("resolveImageModelSelection(prev, all)");
    expect(settingsScript).toContain("modelSel.value = resolveImageModelSelection");
  });

  it("ignores stale model-list responses after a newer request or credential change", () => {
    expect(settingsScript).toContain("isCurrentImageModelRequest(");
    expect(settingsScript).toContain("modelLoadRequest += 1");
    expect(settingsScript).toContain("if (!isCurrentImageModelRequest(");
  });

  it("keeps wander pause per pet and quick-bubble duration global", () => {
    expect(settingsHtml).toContain('id="wander-pause-min"');
    expect(settingsHtml).toContain('id="wander-pause-max"');
    expect(settingsHtml).toContain('id="quick-bubble-duration"');
    expect(settingsHtml.indexOf('id="wander-pause-min"')).toBeLessThan(
      settingsHtml.indexOf('data-page="bubble"'),
    );
    expect(settingsHtml.indexOf('id="quick-bubble-duration"')).toBeGreaterThan(
      settingsHtml.indexOf('data-page="bubble"'),
    );
    expect(settingsScript).toContain("wanderPauseMinMs");
    expect(settingsScript).toContain("QUICK_BUBBLE_DURATION_KEY");
  });
});

