import {
  DEFAULT_WANDER_PAUSE_MAX_MS,
  DEFAULT_WANDER_PAUSE_MIN_MS,
  normalizeWanderPauseRange,
} from "./roam/pause";

export type RoamMode = "stay" | "wander" | "cursor" | "climb";

export interface PetInstance {
  id: string;
  name: string;
  spriteSlug: string;
  visible: boolean;
  size: number;
  roamEnabled: boolean;
  roamMode: RoamMode;
  roamSpeed: number;
  wanderPauseMinMs: number;
  wanderPauseMaxMs: number;
  reactsToActivity: boolean;
}

export interface PetStore {
  version: 1;
  selectedId: string | null;
  instances: PetInstance[];
}

export interface LegacyPet {
  slug: string;
  name: string;
}

const STORE_KEY = "ap_pet_instances";
const STORE_VERSION = 1 as const;
const LEGACY_INSTANCE_ID = "legacy-pet";
const VALID_ROAM_MODES: RoamMode[] = ["stay", "wander", "cursor", "climb"];

export function emptyPetStore(): PetStore {
  return { version: STORE_VERSION, selectedId: null, instances: [] };
}

function clamp(value: number, min: number, max: number, fallback: number): number {
  return Number.isFinite(value) ? Math.max(min, Math.min(max, Math.round(value))) : fallback;
}

function normalizeInstance(value: unknown): PetInstance | null {
  if (!value || typeof value !== "object") return null;
  const raw = value as Partial<PetInstance>;
  if (typeof raw.id !== "string" || !raw.id || typeof raw.spriteSlug !== "string" || !raw.spriteSlug) return null;
  const roamMode = VALID_ROAM_MODES.includes(raw.roamMode as RoamMode) ? raw.roamMode as RoamMode : "wander";
  const wanderPause = normalizeWanderPauseRange(raw.wanderPauseMinMs, raw.wanderPauseMaxMs);
  return {
    id: raw.id,
    name: typeof raw.name === "string" ? raw.name.slice(0, 40) : raw.spriteSlug,
    spriteSlug: raw.spriteSlug,
    visible: raw.visible !== false,
    size: clamp(Number(raw.size), 70, 130, 100),
    roamEnabled: raw.roamEnabled !== false,
    roamMode,
    roamSpeed: clamp(Number(raw.roamSpeed), 1, 10, 5),
    wanderPauseMinMs: wanderPause.minMs,
    wanderPauseMaxMs: wanderPause.maxMs,
    reactsToActivity: raw.reactsToActivity === true,
  };
}

export function parsePetStore(value: unknown): PetStore | null {
  if (!value || typeof value !== "object") return null;
  const raw = value as Partial<PetStore>;
  if (raw.version !== STORE_VERSION || !Array.isArray(raw.instances)) return null;
  const ids = new Set<string>();
  const instances = raw.instances.flatMap((instance) => {
    const normalized = normalizeInstance(instance);
    if (!normalized || ids.has(normalized.id)) return [];
    ids.add(normalized.id);
    return [normalized];
  });
  const selectedId = typeof raw.selectedId === "string" && ids.has(raw.selectedId)
    ? raw.selectedId
    : instances[0]?.id ?? null;
  return { version: STORE_VERSION, selectedId, instances };
}

export function loadPetStore(): PetStore | null {
  try {
    return parsePetStore(JSON.parse(localStorage.getItem(STORE_KEY) || "null"));
  } catch {
    return null;
  }
}

export function savePetStore(store: PetStore): void {
  localStorage.setItem(STORE_KEY, JSON.stringify(store));
}

/**
 * Converts the old selected-sprite concept once. A persisted empty store is a
 * deliberate user choice and must never recreate the old pet.
 */
export function migrateLegacyPetStore(store: PetStore | null, legacy: LegacyPet | null): PetStore {
  if (store) return store;
  if (!legacy?.slug) return emptyPetStore();
  const instance: PetInstance = {
    id: LEGACY_INSTANCE_ID,
    name: legacy.name.trim().slice(0, 40) || legacy.slug,
    spriteSlug: legacy.slug,
    visible: true,
    size: 100,
    roamEnabled: true,
    roamMode: "wander",
    roamSpeed: 5,
    wanderPauseMinMs: DEFAULT_WANDER_PAUSE_MIN_MS,
    wanderPauseMaxMs: DEFAULT_WANDER_PAUSE_MAX_MS,
    reactsToActivity: true,
  };
  return { version: STORE_VERSION, selectedId: instance.id, instances: [instance] };
}

export function initializePetStore(legacy: LegacyPet | null): PetStore {
  const store = migrateLegacyPetStore(loadPetStore(), legacy);
  savePetStore(store);
  return store;
}

export function createPetInstance(store: PetStore, instance: PetInstance): PetStore {
  if (store.instances.some((candidate) => candidate.id === instance.id)) {
    throw new Error(`duplicate pet instance id: ${instance.id}`);
  }
  const normalized = normalizeInstance(instance);
  if (!normalized) throw new Error("invalid pet instance");
  return {
    ...store,
    selectedId: normalized.id,
    instances: [...store.instances, normalized],
  };
}

export function updatePetInstance(
  store: PetStore,
  id: string,
  patch: Partial<Omit<PetInstance, "id">>,
): PetStore {
  const index = store.instances.findIndex((instance) => instance.id === id);
  if (index < 0) throw new Error(`unknown pet instance id: ${id}`);
  const updated = normalizeInstance({ ...store.instances[index], ...patch });
  if (!updated) throw new Error("invalid pet instance update");
  const instances = [...store.instances];
  instances[index] = updated;
  return { ...store, instances };
}

export function removePetInstance(store: PetStore, id: string): PetStore {
  const instances = store.instances.filter((instance) => instance.id !== id);
  if (instances.length === store.instances.length) return store;
  return {
    ...store,
    selectedId: store.selectedId === id ? instances[0]?.id ?? null : store.selectedId,
    instances,
  };
}

export function selectPetInstance(store: PetStore, id: string | null): PetStore {
  if (id !== null && !store.instances.some((instance) => instance.id === id)) {
    throw new Error(`unknown pet instance id: ${id}`);
  }
  return { ...store, selectedId: id };
}

export function selectedPetInstance(store: PetStore): PetInstance | null {
  return store.instances.find((instance) => instance.id === store.selectedId) ?? null;
}

export function petInstanceById(store: PetStore, id: string): PetInstance | null {
  return store.instances.find((instance) => instance.id === id) ?? null;
}

export function newPetInstanceId(): string {
  return `pet-${crypto.randomUUID().replace(/-/g, "")}`;
}
