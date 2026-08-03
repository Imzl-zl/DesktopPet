import { listen, emit } from "@tauri-apps/api/event";
import "./styles.css";

import { invoke } from "@tauri-apps/api/core";
import { cursorPosition, getCurrentWindow } from "@tauri-apps/api/window";
import { Pet } from "./pet";
import { ActivityStore, type ActivityEventPayload } from "./state";
import { BubbleRenderer, invalidateBubbleConfig } from "./bubble";
import { libraryUrlForSlug } from "./catalog";
import { t, setLang, type Lang } from "./i18n";
import { bubbleLines, PET_CHAT } from "./activity";
import * as care from "./care";
import { loadPetStore, petInstanceById, type PetInstance } from "./pets";
import { QuickBubbleController, readQuickBubbleDurationMs } from "./quick-bubble";
import { attachPetPointerDrag } from "./pet-pointer-drag";
import { PetInteractionLease } from "./pet-interaction-lease";
import {
  beginManualDrag,
  finishManualDrag,
  initRoam,
  moveManualDrag,
  setMood,
} from "./roam";
import { DEFAULT_WANDER_PAUSE_MAX_MS, DEFAULT_WANDER_PAUSE_MIN_MS } from "./roam/pause";
import { WindowDragController } from "./window-drag";

const MY_INSTANCE_ID = new URLSearchParams(location.search).get("pet");
const MY_LABEL = getCurrentWindow().label;

function readPetInstance(): PetInstance | null {
  if (!MY_INSTANCE_ID) return null;
  const store = loadPetStore();
  return store ? petInstanceById(store, MY_INSTANCE_ID) : null;
}

let cachedInstance = readPetInstance();

function myPetInstance(): PetInstance | null {
  return cachedInstance;
}

function refreshPetInstance(): void {
  cachedInstance = readPetInstance();
}

const canvas = document.getElementById("pet") as HTMLCanvasElement;
const bubbleEl = document.getElementById("bubble") as HTMLDivElement;
const pet = new Pet(canvas);
const store = new ActivityStore();
const bubble = new BubbleRenderer(bubbleEl);
initRoam(pet, () => {
  const instance = myPetInstance();
  return instance
    ? {
      enabled: instance.roamEnabled,
      mode: instance.roamMode,
      speed: instance.roamSpeed,
      wanderPauseMinMs: instance.wanderPauseMinMs,
      wanderPauseMaxMs: instance.wanderPauseMaxMs,
    }
    : {
      enabled: false,
      mode: "stay",
      speed: 1,
      wanderPauseMinMs: DEFAULT_WANDER_PAUSE_MIN_MS,
      wanderPauseMaxMs: DEFAULT_WANDER_PAUSE_MAX_MS,
    };
});

// --- bubble appearance (theme / opacity / fonts) ------------------------------
const FONT_FAMILIES: Record<string, string> = {
  system: '"Segoe UI", system-ui, sans-serif',
  rounded: '"Segoe UI Rounded", "Nunito", "Segoe UI", sans-serif',
  mono: 'Consolas, "Courier New", monospace',
};

function applyBubble() {
  let theme = localStorage.getItem("ap_theme") || "dark";
  if (theme === "system") theme = matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
  const op = (parseInt(localStorage.getItem("ap_opacity") || "92", 10) || 92) / 100;
  const r = document.documentElement.style;
  if (theme === "light") {
    r.setProperty("--bubble-bg", `rgba(255,255,255,${op})`);
    r.setProperty("--bubble-fg", "#3A2C1A");
    r.setProperty("--bubble-border", "rgba(0,0,0,0.08)");
  } else {
    r.setProperty("--bubble-bg", `rgba(26,20,13,${op})`);
    r.setProperty("--bubble-fg", "#ffffff");
    r.setProperty("--bubble-border", "rgba(255,255,255,0.10)");
  }
  r.setProperty("--bubble-font-size", `${parseInt(localStorage.getItem("ap_font_size") || "12", 10) || 12}px`);
  r.setProperty("--bubble-font-family", FONT_FAMILIES[localStorage.getItem("ap_font_family") || "system"] ?? FONT_FAMILIES.system);
}
applyBubble();

// Pet size + idle bob FX. Size belongs to the displayed instance, so two
// instances using the same spritesheet remain independently configurable.
function applyPet() {
  const size = (myPetInstance()?.size ?? 100) / 100;
  canvas.style.width = `${Math.round(160 * size)}px`;
  canvas.style.height = `${Math.round(180 * size)}px`;
  canvas.classList.toggle("bob", localStorage.getItem("ap_fx") === "1");
}
applyPet();

// Simple synthesized chimes (no audio assets needed). Per-event enable, like
// the macOS SoundSettings (done = high glass-ish, waiting = lower submarine).
let audioCtx: AudioContext | null = null;
function chime(event: "done" | "waiting") {
  const key = event === "done" ? "ap_sound_done" : "ap_sound_waiting";
  const legacy = localStorage.getItem("ap_sound"); // pre-split toggle
  const enabled = localStorage.getItem(key) ?? (legacy === "0" ? "0" : "1");
  if (enabled === "0") return;
  // Custom uploaded sound wins (mac SoundSettings custom file).
  const data = localStorage.getItem(`ap_sound_${event}_data`);
  if (data) {
    try { void new Audio(data).play(); return; } catch {}
  }
  try {
    audioCtx = audioCtx || new AudioContext();
    const o = audioCtx.createOscillator();
    const g = audioCtx.createGain();
    o.type = "sine";
    o.frequency.value = event === "done" ? 880 : 560;
    g.gain.value = 0.05;
    o.connect(g);
    g.connect(audioCtx.destination);
    o.start();
    o.stop(audioCtx.currentTime + 0.13);
  } catch {}
}

// --- pick + load this instance's pet sprite -----------------------------------
function loadInstanceSprite() {
  const instance = myPetInstance();
  if (!instance) return;
  const url = libraryUrlForSlug(instance.spriteSlug)
    ?? (instance.id === "legacy-pet" ? localStorage.getItem("ap_pet_custom") || localStorage.getItem("ap_pet_url") : null);
  if (url) pet.load(url);
}
loadInstanceSprite();

// --- mood + render loop --------------------------------------------------------
// Port of PetController: aggregate mood from the activity store, a 3s
// celebrate burst when entering done, a persistent idle line (re-picked on
// mood transitions, not blinking). Phase 1 has no activity producers yet, so
// the pet idles unless a celebrate/level-up flash is triggered (care feeds).
let celebrateUntil = 0;
let wasCelebrating = false;
let moodLine = ""; // the single-bubble line for idle/done/celebrate
let celebrateText = "";

// Quick bubbles override the normal mood bubble until their own timer expires.
// The rendering signature must be invalidated at expiry so a previously cached
// idle state cannot leave the quick text visible indefinitely.
const QUICK_KEY = "ap_quick_bubbles";
let renderSig = "";
let quickBubbleWasVisible = false;
const quickBubble = new QuickBubbleController({
  now: () => Date.now(),
  schedule: (callback, delay) => window.setTimeout(callback, delay),
  cancel: (timer) => window.clearTimeout(timer as number),
}, () => {
  renderSig = "";
  render();
});

function readQuickPresets(): string[] {
  try {
    const v = JSON.parse(localStorage.getItem(QUICK_KEY) || "[]");
    return Array.isArray(v) ? v.filter((x: unknown) => typeof x === "string" && x.trim()) : [];
  } catch { return []; }
}
function randomPreset(): string | null {
  const list = readQuickPresets();
  if (!list.length) return null;
  return list[Math.floor(Math.random() * list.length)];
}
function showQuickBubble(text: string) {
  quickBubble.show(text, readQuickBubbleDurationMs());
  render();
}
/// Play a celebrate burst with a custom line (achievement/level-up), then
/// settle back to the aggregate mood (mac PetController.flashCelebrate).
function flashCelebrate(line: string) {
  celebrateText = line;
  celebrateUntil = Date.now() + 3000;
  render();
}

/// Feed this instance through a care mutation; on level-up plays a celebrate
/// burst in the same window.
export function feedPet(instanceId: string, change: (s: care.CareState) => void) {
  const before = care.stateFor(instanceId);
  const levelBefore = care.levelForXP(before.xp);
  care.mutate(instanceId, change);
  const after = care.stateFor(instanceId);
  emit("care-updated", { instanceId });
  const levelAfter = care.levelForXP(after.xp);
  if (levelAfter > levelBefore) {
    flashCelebrate(`${t("Level up")}! ${t("Lv")} ${care.displayLevel(after.xp)}`);
    chime("done");
  }
}

function pickMoodLine(mood: string) {
  // Custom/system pools; working/waiting fall back to the PetChat lines so the
  // simple bubble always has something to say.
  let pool = bubbleLines(null, mood);
  if (!pool.length) pool = PET_CHAT[mood] ?? [];
  moodLine = pool.length ? pool[Math.floor(Math.random() * pool.length)] : "";
}

// Render signature: when nothing changed, skip the DOM writes and IPC calls
// entirely. The 500ms timer keeps ticking but becomes a no-op while the pet is
// idle, instead of doing a full pass every tick.

function render() {
  const now = Date.now();
  // Quick bubble overrides everything for its configured duration after the
  // user sends a message from the floating ball or clicks the pet.
  const quickBubbleText = quickBubble.current();
  if (quickBubbleText !== null) {
    quickBubbleWasVisible = true;
    bubble.renderLine(quickBubbleText);
    snugBubble();
    reportHitRect();
    return;
  }
  if (quickBubbleWasVisible) {
    quickBubbleWasVisible = false;
    renderSig = "";
  }
  const resolved = store.topState() === "done" ? "done" : "idle";
  const celebrating = now < celebrateUntil;
  const mood = celebrating ? "celebrate" : resolved;
  pet.setState(mood);
  setMood(mood);
  if (celebrating && wasCelebrating && now >= celebrateUntil) {
    // burst ended, settle into the actual mood's line
    pickMoodLine(resolved === "idle" ? "idle" : "done");
  }
  wasCelebrating = celebrating;

  const sig = [mood, moodLine, celebrating ? celebrateText : ""].join("|");
  if (sig !== renderSig) {
    renderSig = sig;
    if (celebrating) {
      bubble.renderLine(celebrateText || t("Done"));
    } else if (resolved === "done") {
      if (!moodLine) pickMoodLine("done");
      bubble.renderLine(moodLine);
    } else if (localStorage.getItem("ap_idle") !== "0") {
      if (!moodLine) pickMoodLine("idle");
      bubble.renderLine(moodLine);
    } else {
      bubble.hide();
    }
  }

  snugBubble();
  reportHitRect();
}

setInterval(render, 500);
// Hunger decays over time, so re-render so the idle line (hunger-aware) can
// refresh on the care timer, matching macOS's state-republish trigger.
setInterval(() => { moodLine = ""; render(); }, 60_000);

// Pull the bubble down over the canvas's empty headroom so it sits right
// above the pet's head (the sprite rarely fills the whole canvas height).
// Snapped to an integer px so sub-pixel headroom drift between animation
// frames doesn't trigger a transform write (and the backdrop-filter re-composite).
let lastSnugGap = -Infinity;
function snugBubble() {
  const gap = Math.floor(Math.max(0, canvas.clientHeight * pet.headroom - 4));
  if (gap === lastSnugGap) return;
  lastSnugGap = gap;
  bubbleEl.style.transform = `translateY(${gap}px)`;
}

// --- activity events ----------------------------------------------------------
// Activity integration is opt-in per desktop instance. The migrated legacy pet
// keeps its prior behavior; newly added pets remain independent until enabled.
listen<ActivityEventPayload>("activity-event", (e) => {
  const instance = myPetInstance();
  if (!instance?.reactsToActivity) return;
  store.apply(e.payload);
  if (e.payload.weight > 0) {
    if (e.payload.weight >= 0.5) {
      feedPet(instance.id, (state) => care.recordMeal(state, new Date(e.payload.timestamp)));
    } else {
      feedPet(instance.id, (state) => care.feedTokens(state, Math.max(1, Math.round(e.payload.weight * 10)) * 500, new Date(e.payload.timestamp)));
    }
  }
  render();
});
// Instance configuration changes are delivered only to the matching pet window.
// Structural changes are handled by native reconciliation and do not require
// every existing renderer to reload its sprite.
listen<{ instanceId: string }>("pet-instance-changed", (event) => {
  if (event.payload.instanceId !== MY_INSTANCE_ID) return;
  refreshPetInstance();
  loadInstanceSprite();
  applyPet();
  moodLine = "";
  render();
});
// Language changed from Settings, re-render the bubble in the new language.
listen<Lang>("lang-changed", (e) => { setLang(e.payload); render(); });
// Bubble theme / opacity / messages changed from Settings.
listen("bubble-changed", () => {
  invalidateBubbleConfig();
  applyBubble(); applyPet(); moodLine = ""; render();
});

// Floating-ball and pet-click broadcasts intentionally reach every desktop
// pet; there are no main/extra categories to route between.
listen<{ text: string }>("quick-bubble", (e) => {
  showQuickBubble(e.payload.text);
});

// --- interactions ------------------------------------------------------------
// Manual pointer capture replaces Tauri's native drag loop on Windows. The
// controller waits for Rust's interaction lease before moving the transparent
// window and finalizes the engine only after its last physical move completes.
const LEFT_CLICK_KEY = "ap_left_click_action";

function onPetClick() {
  const action = (localStorage.getItem(LEFT_CLICK_KEY) || "none") as "none" | "self" | "all";
  if (action === "none") return;
  const text = randomPreset();
  if (!text) return;
  if (action === "self") {
    showQuickBubble(text);
  } else {
    emit("quick-bubble", { text, target: "all" });
  }
}

const petInteractionLease = new PetInteractionLease({
  activate: () => invoke<void>("set_pet_dragging", { label: MY_LABEL, dragging: true }),
  deactivate: () => invoke<void>("set_pet_dragging", { label: MY_LABEL, dragging: false }),
  reportError: (error) => console.error("Unable to acquire pet interaction lease", error),
});

function startPetInteractionLease(): void {
  petInteractionLease.begin();
}

async function finishPetInteractionLease(): Promise<void> {
  await petInteractionLease.finish();
}

function beginPetInteraction(): void {
  emit("popover-close", null);
  startPetInteractionLease();
}

const petWindowDrag = new WindowDragController({
  cursorPosition,
  setPosition: moveManualDrag,
  startDrag: async () => {
    await petInteractionLease.wait();
    await beginManualDrag();
  },
  finishDrag: async () => {
    finishManualDrag();
    try {
      await invoke<void>("persist_pet_position", { label: MY_LABEL });
    } finally {
      await finishPetInteractionLease();
    }
  },
}, 4, Number.POSITIVE_INFINITY);

attachPetPointerDrag(canvas, {
  drag: petWindowDrag,
  canBegin: (event) => !pet.spriteRect || pet.hitTest(event.offsetX, event.offsetY),
  onBegin: beginPetInteraction,
  finishCapture: finishPetInteractionLease,
  onClick: onPetClick,
  now: () => performance.now(),
  scale: () => window.devicePixelRatio,
  reportError: (error) => console.error("Unable to finish pet drag", error),
});

attachPetPointerDrag(bubbleEl, {
  drag: petWindowDrag,
  canBegin: () => true,
  onBegin: beginPetInteraction,
  finishCapture: finishPetInteractionLease,
  onClick: () => {},
  now: () => performance.now(),
  scale: () => window.devicePixelRatio,
  reportError: (error) => console.error("Unable to finish pet drag", error),
});
canvas.addEventListener("contextmenu", (e) => {
  e.preventDefault();
  invoke("open_settings").catch(() => {});
});
bubbleEl.addEventListener("contextmenu", (e) => {
  e.preventDefault();
  invoke("open_settings").catch(() => {});
});

// Report the interactive region (physical px) for Windows click-through: the
// union of the SPRITE's true bounds and the visible bubble, not the whole
// canvas, so the empty space beside the pet passes clicks to apps below.
// Every pet window (main, project, extra) registers under its own label so
// the Rust loop can manage click-through independently per window.
const petRoot = document.getElementById("pet-root") as HTMLElement;
let lastHitSig = "";
function reportHitRect() {
  const d = window.devicePixelRatio || 1;
  const rects: { left: number; top: number; right: number; bottom: number }[] = [];
  if (!bubbleEl.hidden) {
    const b = bubbleEl.getBoundingClientRect();
    if (b.width > 0) rects.push({ left: b.left, top: b.top, right: b.right, bottom: b.bottom });
  }
  const cr = canvas.getBoundingClientRect();
  const sr = pet.spriteRect;
  if (sr && canvas.width > 0) {
    const kx = cr.width / canvas.width;
    const ky = cr.height / canvas.height;
    rects.push({
      left: cr.left + sr.x * kx,
      top: cr.top + sr.y * ky,
      right: cr.left + (sr.x + sr.w) * kx,
      bottom: cr.top + (sr.y + sr.h) * ky,
    });
  } else {
    rects.push({ left: cr.left, top: cr.top, right: cr.right, bottom: cr.bottom });
  }
  const left = Math.min(...rects.map((r) => r.left));
  const top = Math.min(...rects.map((r) => r.top));
  const right = Math.max(...rects.map((r) => r.right));
  const bottom = Math.max(...rects.map((r) => r.bottom));
  const sig = [left, top, right, bottom].map((v) => Math.round(v)).join(",");
  if (sig === lastHitSig) return;
  lastHitSig = sig;
  invoke("set_hit_rect", { label: MY_LABEL, x: left * d, y: top * d, w: (right - left) * d, h: (bottom - top) * d })
    .catch((err) => invoke("log_debug", { msg: `set_hit_rect failed: ${err}` }).catch(() => {}));
}
new ResizeObserver(reportHitRect).observe(petRoot);
window.addEventListener("resize", reportHitRect);
reportHitRect();

// Respect the in-app / OS reduced-motion preference on transparent pet windows.
function applyReduceMotion() {
  document.body.classList.toggle("reduce-motion", localStorage.getItem("ap_reduce_motion") === "1");
}
applyReduceMotion();
window.addEventListener("storage", (e) => { if (e.key === "ap_reduce_motion") applyReduceMotion(); });

render();
