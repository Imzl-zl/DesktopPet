// Public API for the roam subsystem. Re-exports the engine lifecycle and
// config accessors so callers (main.ts, settings.ts) keep using the same
// imports as before, while the implementation is split across the roam/
// directory.

import type { Pet } from "../pet";
import {
  beginManualDrag,
  destroyEngine,
  finishManualDrag,
  initEngine,
  moveManualDrag,
  setDragging,
  setMood,
} from "./engine";
import {
  ConfigSource,
  ROAM_KEY,
  ROAM_MODE_KEY,
  ROAM_SPEED_KEY,
  VALID_MODES,
  loadConfig,
  setRoamConfigSource,
} from "./types";
import type { RoamMode } from "./types";

export { beginManualDrag, finishManualDrag, moveManualDrag, setDragging, setMood };
export { ROAM_KEY, ROAM_MODE_KEY, ROAM_SPEED_KEY };

export function initRoam(pet: Pet, configSource?: ConfigSource): void {
  setRoamConfigSource(configSource ?? null);
  initEngine(pet);
}

export function destroyRoam(): void {
  setRoamConfigSource(null);
  destroyEngine();
}

export function isRoamingEnabled(): boolean {
  return localStorage.getItem(ROAM_KEY) !== "0";
}

export function setRoamEnabled(enabled: boolean): void {
  localStorage.setItem(ROAM_KEY, enabled ? "1" : "0");
}

export function getRoamMode(): RoamMode {
  return loadConfig().mode;
}

export function setRoamMode(mode: RoamMode): void {
  if (VALID_MODES.includes(mode)) {
    localStorage.setItem(ROAM_MODE_KEY, mode);
  }
}

export function getRoamSpeed(): number {
  return loadConfig().speed;
}

export function setRoamSpeed(speed: number): void {
  localStorage.setItem(ROAM_SPEED_KEY, String(Math.max(1, Math.min(10, speed))));
}
