import { describe, expect, it } from "vitest";
import settingsHtml from "../settings.html?raw";
import settingsScript from "./settings.ts?raw";
import styles from "./styles.css?raw";

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

