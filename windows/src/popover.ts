// The tray/right-click popover, a port of the macOS MenuContentView: Show pet
// toggle, pet-size slider, and a Settings / Updates / Quit footer. Hides
// itself when it loses focus.

import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { getCurrentWindow, LogicalSize } from "@tauri-apps/api/window";
import { check } from "@tauri-apps/plugin-updater";
import { relaunch, exit } from "@tauri-apps/plugin-process";
import { t } from "./i18n";

function applyStatic() {
  const set = (id: string, key: string) => { const el = document.getElementById(id); if (el) el.textContent = t(key); };
  set("pop-sub", "Your little companion");
  set("t-pop-showpet", "Show pet");
  set("t-pop-size", "Pet size");
  set("t-pop-settings", "Settings");
  set("t-pop-updates", "Updates");
  set("t-pop-quit", "Quit");
}

// ---- controls ----------------------------------------------------------------

const showPet = document.getElementById("pop-showpet") as HTMLInputElement;
invoke<boolean>("get_pet_visible").then((v) => { showPet.checked = v; }).catch(() => { showPet.checked = true; });
showPet.onchange = () => invoke("set_pet_visible", { visible: showPet.checked }).catch(() => {});

const size = document.getElementById("pop-size") as HTMLInputElement;
size.value = localStorage.getItem("ap_pet_size") || "100";
size.oninput = () => {
  localStorage.setItem("ap_pet_size", size.value);
  emit("bubble-changed", null);
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
listen("popover-close", () => void getCurrentWindow().hide());
window.addEventListener("keydown", (e) => {
  if (e.key === "Escape") void getCurrentWindow().hide();
});

// Re-sync + refresh whenever the popover is shown again.
listen("popover-shown", () => {
  size.value = localStorage.getItem("ap_pet_size") || "100";
  invoke<boolean>("get_pet_visible").then((v) => { showPet.checked = v; }).catch(() => {});
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
