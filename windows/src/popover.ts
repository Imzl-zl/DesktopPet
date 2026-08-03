// The tray/right-click popover, a port of the macOS MenuContentView: Show pet
// toggle, pet-size slider, and a Settings / Updates / Quit footer. Hides
// itself when it loses focus.

import "./styles.css";

import { invoke } from "@tauri-apps/api/core";
import { emitTo, listen } from "@tauri-apps/api/event";
import { getCurrentWindow, LogicalSize } from "@tauri-apps/api/window";
import { check } from "@tauri-apps/plugin-updater";
import { relaunch, exit } from "@tauri-apps/plugin-process";
import { t, setLang, type Lang } from "./i18n";
import { Pet } from "./pet";
import { getLibrary } from "./catalog";
import { loadPetStore, savePetStore, selectedPetInstance, updatePetInstance } from "./pets";

function applyStatic() {
  const set = (id: string, key: string) => { const el = document.getElementById(id); if (el) el.textContent = t(key); };
  set("pop-sub", "Your little companion");
  set("t-pop-showpet", "Show desktop pets");
  set("t-pop-size", "Pet size");
  set("t-pop-settings", "Settings");
  set("t-pop-updates", "Updates");
  set("t-pop-quit", "Quit");
}

// ---- controls ----------------------------------------------------------------

const showPet = document.getElementById("pop-showpet") as HTMLInputElement;
invoke<boolean>("get_desktop_pets_visible").then((visible) => { showPet.checked = visible; }).catch(() => { showPet.checked = true; });
showPet.onchange = () => invoke("set_desktop_pets_visible", { visible: showPet.checked }).catch(() => {});

// Living pet in the popover header: mirror the instance selected in Settings.
const popPetCanvas = document.getElementById("pop-pet") as HTMLCanvasElement | null;
const popPet = popPetCanvas ? new Pet(popPetCanvas) : null;

function loadPopoverPet(): void {
  const store = loadPetStore();
  const instance = store ? selectedPetInstance(store) : null;
  const pet = getLibrary().find((candidate) => candidate.slug === instance?.spriteSlug) ?? getLibrary()[0];
  if (pet) popPet?.load(pet.url);
}
loadPopoverPet();

const size = document.getElementById("pop-size") as HTMLInputElement;
function syncSize(): void {
  const store = loadPetStore();
  size.value = String((store ? selectedPetInstance(store) : null)?.size ?? 100);
}
syncSize();
size.oninput = () => {
  const store = loadPetStore();
  if (!store) return;
  const instance = selectedPetInstance(store);
  if (!instance) return;
  savePetStore(updatePetInstance(store, instance.id, { size: parseInt(size.value, 10) }));
  void emitTo(`pet-${instance.id}`, "pet-instance-changed", { instanceId: instance.id }).catch(() => {});
  void emitTo("settings", "pet-instance-changed", { instanceId: instance.id }).catch(() => {});
};

(document.getElementById("pop-settings") as HTMLButtonElement).onclick = () => {
  invoke("open_settings").catch(() => {});
  void getCurrentWindow().hide();
};
(document.getElementById("pop-quit") as HTMLButtonElement).onclick = () => { exit(0); };

const updatesBtn = document.getElementById("pop-updates") as HTMLButtonElement;
updatesBtn.onclick = async () => {
  const label = document.getElementById("t-pop-updates")!;
  label.textContent = t("Checking…");
  try {
    const update = await check();
    if (update) {
      label.textContent = t("Installing…");
      await update.downloadAndInstall();
      await relaunch();
    } else {
      label.textContent = t("Up to date");
      setTimeout(() => { label.textContent = t("Updates"); }, 2500);
    }
  } catch {
    label.textContent = t("Up to date");
    setTimeout(() => { label.textContent = t("Updates"); }, 2500);
  }
};

// ---- lifecycle ----------------------------------------------------------------

// Hide when clicking anywhere outside (the popover loses focus), like the
// macOS transient popover. Backed up by a Rust-side Focused(false) handler,
// a "popover-close" broadcast from the pet window, and the Escape key.
getCurrentWindow().onFocusChanged(({ payload: focused }) => {
  if (!focused) void getCurrentWindow().hide();
});
listen<Lang>("lang-changed", (event) => {
  setLang(event.payload);
  applyStatic();
});
listen("popover-close", () => void getCurrentWindow().hide());
window.addEventListener("keydown", (e) => {
  if (e.key === "Escape") void getCurrentWindow().hide();
});

// Re-sync + refresh whenever the popover is shown again.
listen("popover-shown", () => {
  applyStatic();
  syncSize();
  loadPopoverPet();
  invoke<boolean>("get_desktop_pets_visible").then((visible) => { showPet.checked = visible; }).catch(() => {});
});
listen("pets-changed", () => {
  syncSize();
  loadPopoverPet();
});

// Hug the content height like the macOS popover (no dead space).
let lastH = 0;
function fitWindow() {
  const card = document.querySelector(".pop-card") as HTMLElement;
  if (!card) return;
  const h = Math.min(560, Math.max(220, card.scrollHeight + 20));
  if (Math.abs(h - lastH) < 2) return;
  lastH = h;
  getCurrentWindow().setSize(new LogicalSize(300, h)).catch(() => {});
}
fitWindow();

applyStatic();
