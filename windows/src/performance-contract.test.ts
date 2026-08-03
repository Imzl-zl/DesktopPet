import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const settingsScript = readFileSync(new URL("./settings.ts", import.meta.url), "utf8");
const petScript = readFileSync(new URL("./pet.ts", import.meta.url), "utf8");
const petWindowScript = readFileSync(new URL("./pet-window.ts", import.meta.url), "utf8");
const popoverScript = readFileSync(new URL("./popover.ts", import.meta.url), "utf8");
const environmentScript = readFileSync(new URL("./roam/environment.ts", import.meta.url), "utf8");
const engineScript = readFileSync(new URL("./roam/engine.ts", import.meta.url), "utf8");
const modesScript = readFileSync(new URL("./roam/modes.ts", import.meta.url), "utf8");
const styles = readFileSync(new URL("./styles.css", import.meta.url), "utf8");
const nativeWindowCode = readFileSync(new URL("../src-tauri/src/lib.rs", import.meta.url), "utf8");

describe("desktop pet performance contracts", () => {
  it("routes an instance configuration update only to its owning pet window", () => {
    expect(settingsScript).toContain('emitTo(`pet-${instanceId}`, "pet-instance-changed", { instanceId })');
    expect(petWindowScript).toMatch(/listen(?:<[^>]+>)?\("pet-instance-changed"/);
    expect(petWindowScript).not.toContain('listen("pets-changed"');
  });

  it("does not reconcile native windows for slider-driven instance configuration", () => {
    const updateSelected = settingsScript.match(
      /const updateSelected = \(patch: Partial<Omit<PetInstance, "id">>\) => \{.*?^  \};/ms,
    )?.[0] ?? "";
    expect(updateSelected).toContain("syncWindows: false");
  });

  it("keeps an open settings window synchronized with popover instance updates", () => {
    expect(popoverScript).toContain('emitTo("settings", "pet-instance-changed", { instanceId: instance.id })');
    expect(settingsScript).toMatch(/listen(?:<[^>]+>)?\("pet-instance-changed"/);
  });

  it("refreshes popover labels when the shared language changes", () => {
    expect(popoverScript).toMatch(
      /listen<Lang>\("lang-changed", \(event\) => \{\s*setLang\(event\.payload\);\s*applyStatic\(\);/s,
    );
    expect(popoverScript).toMatch(/listen\("popover-shown", \(\) => \{\s*applyStatic\(\);/s);
  });

  it("does not decode a spritesheet again when its URL has not changed", () => {
    expect(petScript).toContain("private loadedUrl: string | null = null;");
    expect(petScript).toContain("if (spritesheetUrl === this.loadedUrl) return;");
  });

  it("enumerates system windows only for climb roaming", () => {
    expect(environmentScript).toContain("fetchEnvironment(includeSystemWindows = false)");
    expect(environmentScript).toContain("includeSystemWindows ? await fetchSystemWindows(sf) : []");
    expect(engineScript).toContain('fetchEnvironment(mode === "climb")');
  });

  it("rechecks release mode after environment loading before choosing throw or fall", () => {
    const releaseHandler = engineScript.match(
      /async function handleDragRelease\([\s\S]*?\n\}/,
    )?.[0] ?? "";
    const releaseContext = engineScript.match(
      /export async function resolveReleaseContext\([\s\S]*?\n\}/,
    )?.[0] ?? "";

    expect(releaseHandler).toContain("const context = await resolveReleaseContext();");
    expect(releaseContext).toContain('const includedSystemWindows = config.mode === "climb";');
    expect(releaseContext).toContain("let environment = await getEnvironment(includedSystemWindows);");
    expect(releaseContext).toContain("config = getConfig();");
    expect(releaseContext).toContain('if (config.mode === "climb" && !includedSystemWindows)');
    expect(releaseContext).toContain("environment = await getEnvironment(true);");
    expect(releaseContext).toMatch(
      /environment = await getEnvironment\(true\);[\s\S]*?return \{ config: getConfig\(\), environment \};/,
    );
  });

  it("reads persistent instance state only when an instance update arrives", () => {
    expect(petWindowScript).toContain("let cachedInstance = readPetInstance();");
    expect(petWindowScript).toContain("cachedInstance = readPetInstance();");
  });

  it("does not wake every pet window to poll an unwritten bubble dirty flag", () => {
    expect(petWindowScript).not.toContain("setInterval(() => { if (bubble.dirty)");
  });

  it("skips cursor and geometry for known-hidden pet windows", () => {
    expect(nativeWindowCode).toMatch(
      /match window\.1\.is_visible\(\) \{[\s\S]*?Ok\(true\) => visible_wins\.push\(window\),[\s\S]*?Ok\(false\) => \{\}/,
    );
    expect(nativeWindowCode).toContain("for (label, win) in &visible_wins");
  });

  it("keeps visibility-query failures interactive", () => {
    expect(nativeWindowCode).toContain("Err(_) => unknown_visibility_wins.push(window)");
    expect(nativeWindowCode).toContain("for (label, win) in &unknown_visibility_wins");
    expect(nativeWindowCode).toContain("apply_ignore_state(&mut last_ignore, label, false");
  });

  it("retries failed visibility safety resets", () => {
    expect(nativeWindowCode).toContain("fn apply_ignore_state<E>(");
    expect(nativeWindowCode).toContain("set_ignore(ignore).is_err()");
    expect(nativeWindowCode).toContain("apply_ignore_state(&mut last_ignore, label, false");
  });

  it("retries failed visible click-through state transitions", () => {
    expect(nativeWindowCode).toContain("apply_ignore_state(&mut last_ignore, label, ignore");
  });

  it("does not persist geometry for known-hidden pet windows", () => {
    expect(nativeWindowCode).toMatch(
      /if tick % 17 == 0 \{[\s\S]*?for \(label, window\) in &visible_wins/,
    );
    expect(nativeWindowCode).not.toMatch(
      /if tick % 17 == 0 \{[\s\S]*?for \(label, window\) in &wins/,
    );
  });

  it("disables the Settings aura when motion reduction is enabled", () => {
    const reducedMotion = styles.match(/body\.reduce-motion[\s\S]*?\{[\s\S]*?animation: none !important;/)?.[0] ?? "";
    const osReducedMotion = styles.match(/@media \(prefers-reduced-motion: reduce\) \{[\s\S]*?\n\}/)?.[0] ?? "";

    expect(reducedMotion).toContain(".pp-stage .stage-aura");
    expect(reducedMotion).toContain(".page.sel");
    expect(reducedMotion).toContain("body.reduce-motion .modal,");
    expect(reducedMotion).toContain("body.reduce-motion .modal-backdrop");
    expect(osReducedMotion).toContain(".pp-stage .stage-aura");
  });

  it("does not wake resting pets with periodic diagnostic IPC", () => {
    expect(engineScript).not.toContain("roam-diag:");
    expect(modesScript).not.toContain("roam-mode:");
  });
});

