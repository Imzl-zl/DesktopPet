import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { check } from "@tauri-apps/plugin-updater";
import { relaunch } from "@tauri-apps/plugin-process";
import { petDisplayName, savedSlug } from "./catalog";
import * as care from "./care";
import { initializePetStore } from "./pets";

const instanceId = new URLSearchParams(location.search).get("pet");

async function bootstrapDesktopPets(): Promise<void> {
  const legacySlug = savedSlug();
  const store = initializePetStore(legacySlug ? { slug: legacySlug, name: petDisplayName(legacySlug) } : null);
  if (legacySlug && store.instances.some((instance) => instance.id === "legacy-pet")) {
    care.migrateLegacyCareState(legacySlug, "legacy-pet");
  }
  await invoke("sync_desktop_pet_windows", {
    pets: store.instances.map(({ id, visible }) => ({ id, visible })),
  });

  try {
    const update = await check();
    if (update) {
      await update.downloadAndInstall();
      await relaunch();
    }
  } catch {
    // Updates are optional and must not block restoring the desktop pets.
  }
}

if (instanceId) {
  void import("./pet-window");
} else {
  void bootstrapDesktopPets().catch((error) => {
    void invoke("log_debug", { msg: `desktop pet bootstrap failed: ${error}` });
  });
  void getCurrentWindow().hide();
}
