import { describe, expect, it } from "vitest";
import {
  createPetInstance,
  emptyPetStore,
  migrateLegacyPetStore,
  parsePetStore,
  removePetInstance,
  updatePetInstance,
  type PetInstance,
} from "./pets";
import { DEFAULT_WANDER_PAUSE_MAX_MS, DEFAULT_WANDER_PAUSE_MIN_MS } from "./roam/pause";

function pet(id: string, slug: string, name: string): PetInstance {
  return {
    id,
    name,
    spriteSlug: slug,
    visible: true,
    size: 100,
    roamEnabled: true,
    roamMode: "wander",
    roamSpeed: 5,
    wanderPauseMinMs: DEFAULT_WANDER_PAUSE_MIN_MS,
    wanderPauseMaxMs: DEFAULT_WANDER_PAUSE_MAX_MS,
    reactsToActivity: false,
  };
}

describe("desktop pet instance model", () => {
  it("migrates the legacy selected pet into one normal desktop instance", () => {
    const store = migrateLegacyPetStore(null, { slug: "cat", name: "Miso" });

    expect(store).toEqual({
      version: 1,
      selectedId: "legacy-pet",
      instances: [{
        ...pet("legacy-pet", "cat", "Miso"),
        reactsToActivity: true,
      }],
    });
  });

  it("keeps an explicitly empty instance store empty after the legacy migration", () => {
    const empty = emptyPetStore();

    const store = migrateLegacyPetStore(empty, { slug: "cat", name: "Miso" });

    expect(store).toEqual(empty);
  });

  it("selects a newly created instance for immediate editing", () => {
    let store = emptyPetStore();
    store = createPetInstance(store, pet("miso", "cat", "Miso"));
    store = createPetInstance(store, pet("nori", "cat", "Nori"));

    expect(store.selectedId).toBe("nori");
  });

  it("keeps identical sprite instances independent when one is renamed or removed", () => {
    let store = emptyPetStore();
    store = createPetInstance(store, pet("miso", "cat", "Miso"));
    store = createPetInstance(store, pet("nori", "cat", "Nori"));
    store = updatePetInstance(store, "miso", { name: "Miso Prime", size: 125 });

    expect(store.instances).toEqual([
      { ...pet("miso", "cat", "Miso"), name: "Miso Prime", size: 125 },
      pet("nori", "cat", "Nori"),
    ]);

    store = removePetInstance(store, "miso");
    expect(store.selectedId).toBe("nori");
    expect(store.instances).toEqual([pet("nori", "cat", "Nori")]);
  });
  it("defaults a legacy instance's missing wander pause range to the established behavior", () => {
    const store = parsePetStore({
      version: 1,
      selectedId: "miso",
      instances: [{
        id: "miso",
        name: "Miso",
        spriteSlug: "cat",
        visible: true,
        size: 100,
        roamEnabled: true,
        roamMode: "wander",
        roamSpeed: 5,
        reactsToActivity: false,
      }],
    });

    expect(store?.instances[0]).toMatchObject({
      wanderPauseMinMs: DEFAULT_WANDER_PAUSE_MIN_MS,
      wanderPauseMaxMs: DEFAULT_WANDER_PAUSE_MAX_MS,
    });
  });
});

