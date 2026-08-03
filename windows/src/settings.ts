import "./styles.css";

import { invoke } from "@tauri-apps/api/core";
import { emit, emitTo, listen } from "@tauri-apps/api/event";
import { getVersion } from "@tauri-apps/api/app";
import { exit } from "@tauri-apps/plugin-process";
import { enable, disable, isEnabled } from "@tauri-apps/plugin-autostart";
import { loadCatalog, savedSlug, getLibrary, addToLibrary, removeFromLibrary, petDisplayName, type Pet as CatalogPet, type LibPet } from "./catalog";
import { t, getLang, setLang, type Lang } from "./i18n";
import { uiIcon } from "./icons";

import { slice, type Rect } from "./pet";
import { Pet as AnimatedPet } from "./pet";

import * as care from "./care";
import {
  createPetInstance,
  initializePetStore,
  newPetInstanceId,
  removePetInstance,
  savePetStore,
  selectPetInstance,
  selectedPetInstance,
  updatePetInstance,
  type PetInstance,
  type PetStore,
} from "./pets";
import { DEFAULT_WANDER_PAUSE_MAX_MS, DEFAULT_WANDER_PAUSE_MIN_MS } from "./roam/pause";
import {
  QUICK_BUBBLE_DURATION_KEY,
  normalizeQuickBubbleDurationSeconds,
} from "./quick-bubble";

function legacyPet() {
  const slug = savedSlug();
  return slug ? { slug, name: petDisplayName(slug) } : null;
}

function currentPetStore(): PetStore {
  return initializePetStore(legacyPet());
}

function currentPetInstance(): PetInstance | null {
  return selectedPetInstance(currentPetStore());
}

function syncDesktopPetWindows(store: PetStore): Promise<void> {
  return invoke("sync_desktop_pet_windows", {
    pets: store.instances.map(({ id, visible }) => ({ id, visible })),
  });
}

type PersistDesktopPetsOptions = {
  syncWindows?: boolean;
  instanceId?: string;
};

function persistDesktopPets(store: PetStore, options: PersistDesktopPetsOptions = {}): void {
  const { syncWindows = true, instanceId } = options;
  savePetStore(store);
  if (syncWindows) void syncDesktopPetWindows(store).catch((error) => alert(String(error)));
  if (instanceId) {
    void emitTo(`pet-${instanceId}`, "pet-instance-changed", { instanceId }).catch(() => {});
  } else {
    void emit("pets-changed");
  }
}

function instanceFromLibrary(pet: LibPet): PetInstance {
  return {
    id: newPetInstanceId(),
    name: pet.name,
    spriteSlug: pet.slug,
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

// ------------------------------------------------------------- segmented ----
// macOS-style segmented controls: <span class="seg" data-key data-default>.
function initSegs() {
  document.querySelectorAll<HTMLElement>(".seg[data-key]").forEach((seg) => {
    const key = seg.dataset.key!;
    const current = localStorage.getItem(key) || seg.dataset.default || "";
    const btns = seg.querySelectorAll<HTMLButtonElement>("button");
    btns.forEach((b) => {
      b.classList.toggle("sel", b.dataset.v === current);
      b.onclick = () => {
        localStorage.setItem(key, b.dataset.v!);
        btns.forEach((x) => x.classList.toggle("sel", x === b));
        emit("bubble-changed", null);
        document.dispatchEvent(new CustomEvent("seg-changed", { detail: key }));
      };
    });
  });
}

// ------------------------------------------------------------------ tabs ----
function initTabs() {
  const tabs = document.querySelectorAll<HTMLButtonElement>(".tabbar .tab");
  tabs.forEach((b) => {
    b.onclick = () => {
      tabs.forEach((x) => x.classList.toggle("sel", x === b));
      document.querySelectorAll<HTMLElement>(".page").forEach((p) => {
        p.classList.toggle("sel", p.dataset.page === b.dataset.tab);
      });
      if (b.dataset.tab === "care") renderCare();
      if (b.dataset.tab === "pet") document.dispatchEvent(new CustomEvent("ap-pet-tab-shown"));
    };
  });
}

// ------------------------------------------------------------------ care ----
function fmtNum(n: number): string {
  n = Number(n) || 0;
  if (n >= 1e9) return (n / 1e9).toFixed(1).replace(/\.0$/, "") + "B";
  if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, "") + "M";
  if (n >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, "") + "K";
  return String(n);
}
function currentPetName(): string {
  return currentPetInstance()?.name || t("Your pet");
}
function renderCare() {
  const instance = currentPetInstance();
  const empty = document.getElementById("care-empty");
  const hud = document.querySelector(".care-hud") as HTMLElement | null;
  if (!instance) {
    if (empty) empty.style.display = "";
    // Keep the left panel visible but show a placeholder state
    const name = document.getElementById("care-name");
    if (name) name.textContent = t("No pet yet");
    const stage = document.getElementById("care-stagename");
    if (stage) stage.textContent = "";
    const hunger = document.getElementById("care-hunger");
    if (hunger) hunger.textContent = "";
    const level = document.getElementById("care-level");
    if (level) level.textContent = "";
    const xp = document.getElementById("care-xp");
    if (xp) xp.textContent = "";
    const tonext = document.getElementById("care-tonext");
    if (tonext) tonext.textContent = "";
    const fill = document.getElementById("care-xpfill");
    if (fill) fill.style.width = "0%";
    return;
  }
  if (empty) empty.style.display = "none";
  if (hud) hud.style.display = "";
  const s = care.stateFor(instance.id);
  const internal = care.levelForXP(s.xp);
  const setTxt = (id: string, v: string) => { const el = document.getElementById(id); if (el) el.textContent = v; };
  setTxt("care-name", instance.name);
  setTxt("care-level", `${t("Lv")} ${care.displayLevel(s.xp)}`);
  setTxt("care-stagename", t(care.stageName(internal)));
  setTxt("care-hunger", t(care.hunger(s)));
  const fill = document.getElementById("care-xpfill");
  if (fill) fill.style.width = `${Math.round(care.levelProgress(s.xp) * 100)}%`;
  setTxt("care-xp", `${s.xp} XP`);
  const toNext = Math.max(0, care.xpToReach(care.levelForXP(s.xp) + 1) - s.xp);
  setTxt("care-tonext", toNext > 0 ? `≈ ${fmtNum(toNext)} ${t("XP to next level")}` : "");
  // achievements
  const unlocked = new Set(s.unlockedAchievements || []);
  setTxt("care-achcount", `${unlocked.size} / ${care.ACHIEVEMENTS.length}`);
  const badges = document.getElementById("care-badges");
  if (badges) badges.innerHTML = care.ACHIEVEMENTS
    .map((a) => `<span class="care-badge${unlocked.has(a) ? " on" : ""}" title="${t(care.ACH_NAME[a])}">${uiIcon(care.ACH_ICON[a])}</span>`)
    .join("");
  setTxt("care-today", fmtNum(s.tokensToday));
  setTxt("care-today-sub", `${s.mealsToday} ${t("sessions")}`);
  setTxt("care-streak", String(s.streakDays));
  setTxt("care-lifetime", fmtNum(s.xp));
  setTxt("care-sessions", String(s.totalMeals));
  const days = care.recentDays(s, 7);
  const max = Math.max(1, ...days.map((d) => d.tokens));
  setTxt("care-burntotal", fmtNum(days.reduce((a, d) => a + d.tokens, 0)));
  const chart = document.getElementById("care-chart");
  if (chart) chart.innerHTML = days
    .map((d) => `<div class="cbar-wrap" title="${fmtNum(d.tokens)}"><div class="cbar" style="height:${Math.max(3, Math.round((d.tokens / max) * 100))}%"></div><div class="cbar-lbl">${d.label}</div></div>`)
    .join("");
}
// Click the pet's name to rename it; Enter or blur saves (empty resets to the
// default). The custom name flows to the HUD, menubar, and web leaderboard.
function setupRename() {
  const nameEl = document.getElementById("care-name");
  const input = document.getElementById("care-rename") as HTMLInputElement | null;
  if (!nameEl || !input) return;
  const startEdit = () => {
    if (!currentPetInstance()) return;
    input.value = currentPetName();
    nameEl.style.display = "none";
    input.style.display = "";
    input.focus();
    input.select();
  };
  const commit = () => {
    const instance = currentPetInstance();
    if (instance) {
      persistDesktopPets(
        updatePetInstance(currentPetStore(), instance.id, { name: input.value.trim() || instance.spriteSlug }),
        { syncWindows: false, instanceId: instance.id },
      );
    }
    input.style.display = "none";
    nameEl.style.display = "";
    renderCare();
    emit("care-updated");
  };
  nameEl.addEventListener("click", startEdit);
  input.addEventListener("blur", commit);
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); input.blur(); }
    else if (e.key === "Escape") { input.value = currentPetName(); input.blur(); }
  });
}
setupRename();

// Manual feeding (pure-pet interaction: the pet eats a snack on demand).
const feedBtn = document.getElementById("care-feed-btn") as HTMLButtonElement | null;
if (feedBtn) {
  feedBtn.onclick = () => {
    const instance = currentPetInstance();
    if (!instance) return;
    const leveled = care.feed(instance.id, (state) => care.recordMeal(state));
    emit("care-updated", { instanceId: instance.id });
    if (leveled) emit("quick-bubble", { text: `${t("Level up")}! ${t("Lv")} ${care.displayLevel(care.stateFor(instance.id).xp)}` });
    renderCare();
  };
}

// Refresh when the pet window feeds the pet, and periodically for the hunger clock.
listen("care-updated", () => { renderCare(); });
// The left pet panel is always visible — keep its state fresh even when
// the Care tab itself isn't selected.
setInterval(() => { renderCare(); }, 30_000);

// ------------------------------------------------------------------ pet ----
// macOS model: the pager shows your INSTALLED pets (library); the full catalog
// lives in the Browse dialog where "Get" adds a pet to the library.
const current = document.getElementById("pet-current") as HTMLDivElement | null;
const search = document.getElementById("pet-search") as HTMLInputElement;
const results = document.getElementById("pet-results") as HTMLDivElement;

// ---- living pet: the left pet-panel canvas animates the current pet with
// the same engine as the desktop window (Pet from ./pet). Identity/XP/hunger
// text is rendered by renderCare() into the care-* hooks in the panel.
let stagePet: AnimatedPet | null = null;

function initLivingPets() {
  const c2 = document.getElementById("hero-live") as HTMLCanvasElement | null;
  if (c2) stagePet = new AnimatedPet(c2);
  let loadedUrl: string | null = null;
  const loadPet = (url: string | null) => {
    if (!url || url === loadedUrl) return;
    loadedUrl = url;
    stagePet?.load(url);
  };
  return { loadPet };
}

const livingPets = initLivingPets();

let catalog: CatalogPet[] = [];

function selectedPet(): LibPet | undefined {
  const instance = currentPetInstance();
  return instance ? getLibrary().find((pet) => pet.slug === instance.spriteSlug) : undefined;
}

let refreshDesktopPets: () => void = () => {};

function addInstanceFromLibrary(pet: LibPet) {
  const store = currentPetStore();
  if (store.instances.length >= MAX_DESKTOP_PETS) return;
  persistDesktopPets(createPetInstance(store, instanceFromLibrary(pet)));
  livingPets.loadPet(pet.url);
  showCurrent();
  refreshDesktopPets();
  renderPage();
  renderCare();
}

function showCurrent() {
  const sel = selectedPet();
  const deselectBtn = document.getElementById("pet-deselect") as HTMLButtonElement | null;
  if (sel) {
    if (current) current.textContent = sel.name;
    if (deselectBtn) deselectBtn.style.display = "none";
  } else {
    if (current) current.textContent = t("No pet yet");
    if (deselectBtn) deselectBtn.style.display = "none";
  }
  const hero = document.getElementById("hero-thumb") as HTMLCanvasElement | null;
  const ctx = hero?.getContext("2d");
  if (ctx && hero) ctx.clearRect(0, 0, hero.width, hero.height);
  if (hero && sel) drawThumb(hero, sel.url);
  livingPets.loadPet(sel?.url ?? null);
  loadHeroDescription(sel);
}

/// Removes the selected desktop instance. This control stays hidden in the
/// refreshed layout; removal normally happens from the desktop-pet list.
function deselectPet() {
  const instance = currentPetInstance();
  if (instance) persistDesktopPets(removePetInstance(currentPetStore(), instance.id));
  const next = getLibrary()[0];
  livingPets.loadPet(next?.url ?? null);
  showCurrent();
  renderPage();
  renderCare();
}

// The pet's own description (from its pet.json on the CDN), like the macOS
// hero card; falls back to the generic caption.
async function loadHeroDescription(sel: LibPet | undefined) {
  const el = document.getElementById("hero-desc");
  if (!el) return;
  if (!sel?.petJsonUrl) { el.textContent = t("Pick the companion that floats on your desktop."); return; }
  try {
    const j: any = await (await fetch(sel.petJsonUrl)).json();
    const desc = (j.description || j.about || "").toString().trim();
    el.textContent = desc || t("Pick the companion that floats on your desktop.");
  } catch {
    el.textContent = t("Pick the companion that floats on your desktop.");
  }
}

// Pet pager , 8 per page (4 × 2) over the LIBRARY, hover ✕ to remove.
const PER_PAGE = 8;
let page = 0;

const pgPrev = document.getElementById("pg-prev") as HTMLButtonElement;
const pgNext = document.getElementById("pg-next") as HTMLButtonElement;
const pgInd = document.getElementById("pg-ind") as HTMLElement;

function libraryView(): LibPet[] {
  const q = search.value.trim().toLowerCase();
  const lib = getLibrary();
  return q ? lib.filter((p) => p.name.toLowerCase().includes(q)) : lib;
}

function renderPage() {
  const lib = getLibrary();
  (document.getElementById("pet-search-wrap") as HTMLElement).style.display = lib.length > 4 ? "" : "none"; // mac shows search only when >4
  (document.getElementById("lib-empty") as HTMLElement).hidden = lib.length > 0;
  const view = libraryView();
  const totalPages = Math.max(1, Math.ceil(view.length / PER_PAGE));
  if (page >= totalPages) page = totalPages - 1;
  results.innerHTML = "";
  for (const p of view.slice(page * PER_PAGE, page * PER_PAGE + PER_PAGE)) {
    const item = document.createElement("article");
    item.className = "library-item";
    item.dataset.slug = p.slug;
    const remove = document.createElement("button");
    remove.className = "icon-btn library-remove";
    remove.type = "button";
    remove.title = t("Remove");
    remove.setAttribute("aria-label", t("Remove"));
    remove.innerHTML = uiIcon("x");
    remove.onclick = () => {
      if (currentPetStore().instances.some((instance) => instance.spriteSlug === p.slug)) {
        alert(t("Remove the desktop pets using this sprite before removing it from your library."));
        return;
      }
      removeFromLibrary(p.slug);
      showCurrent();
      renderPage();
    };

    const cv = document.createElement("canvas");
    cv.width = 48; cv.height = 48; cv.className = "pet-thumb";
    drawThumb(cv, p.url);
    const label = document.createElement("span");
    label.className = "library-name";
    label.textContent = p.name;

    const actions = document.createElement("div");
    actions.className = "library-actions";
    const add = document.createElement("button");
    add.className = "mini library-action";
    add.type = "button";
    add.innerHTML = `${uiIcon("plus")}<span>${t("Add to desktop")}</span>`;
    add.disabled = currentPetStore().instances.length >= MAX_DESKTOP_PETS;
    add.onclick = () => addInstanceFromLibrary(p);

    actions.append(add);
    item.append(remove, cv, label, actions);
    results.appendChild(item);
  }
  const pager = document.getElementById("pet-pager") as HTMLElement;
  pager.style.display = view.length > PER_PAGE ? "" : "none";
  pgPrev.disabled = page === 0;
  pgNext.disabled = page >= totalPages - 1;
  pgInd.innerHTML = "";
  if (totalPages <= 8) {
    for (let i = 0; i < totalPages; i++) {
      const d = document.createElement("span");
      d.className = "pg-dot" + (i === page ? " sel" : "");
      d.onclick = () => { page = i; renderPage(); };
      pgInd.appendChild(d);
    }
  } else {
    pgInd.textContent = `${page + 1} / ${totalPages}`;
  }
}
pgPrev.onclick = () => { if (page > 0) { page--; renderPage(); } };
pgNext.onclick = () => { page++; renderPage(); };

// Draws frame 0 (first column of the Idle row) of an 8x9 spritesheet as a preview.
function drawThumb(cv: HTMLCanvasElement, url: string) {
  const ctx = cv.getContext("2d");
  if (!ctx) return;
  ctx.imageSmoothingEnabled = false;
  const img = new Image();
  img.onload = () => {
    const fw = img.naturalWidth / 8, fh = img.naturalHeight / 9;
    if (!fw || !fh) return;
    const sc = Math.min(cv.width / fw, cv.height / fh);
    const dw = fw * sc, dh = fh * sc;
    ctx.clearRect(0, 0, cv.width, cv.height);
    ctx.drawImage(img, 0, 0, fw, fh, (cv.width - dw) / 2, (cv.height - dh) / 2, dw, dh);
  };
  img.src = url;
}

// Toggle the visibility of all desktop pet windows without changing which
// instances exist or their individual enabled state.
async function initDesktopPetsVisibility() {
  const box = document.getElementById("show-desktop-pets") as HTMLInputElement | null;
  if (!box) return;
  try {
    box.checked = await invoke("get_desktop_pets_visible");
  } catch {
    box.checked = true;
  }
  box.addEventListener("change", () => {
    invoke("set_desktop_pets_visible", { visible: box.checked }).catch(() => {});
  });
}

async function initPet() {
  search.addEventListener("input", () => { page = 0; renderPage(); });
  document.getElementById("pet-deselect")?.addEventListener("click", deselectPet);
  renderPage();
  showCurrent();
  initBrowse();
  initCreate();
  // Seed the library on first run: the currently shown pet (or the catalog
  // default) becomes the first installed pet, so the pager is never empty.
  for (;;) {
    catalog = await loadCatalog();
    if (catalog.length) break;
    if (current) current.textContent = t("Couldn't load pets , check your internet connection.");
    await new Promise((r) => setTimeout(r, 15000));
  }
  if (!getLibrary().length) {
    const legacySlug = savedSlug();
    const chosen = catalog.find((pet) => pet.slug === legacySlug) ?? catalog[Math.floor(catalog.length / 2)];
    if (chosen) addToLibrary({ slug: chosen.slug, name: chosen.name, url: chosen.spritesheetUrl, petJsonUrl: chosen.petJsonUrl });
  }
  if (!currentPetInstance()) {
    const first = getLibrary()[0];
    if (first) persistDesktopPets(createPetInstance(currentPetStore(), instanceFromLibrary(first)));
  }
  renderPage();
  showCurrent();
  renderCare();
}

// -------------------------------------------------------------- browse ----
// The macOS BrowsePetsView: community pets first, Petdex shuffled, category
// segmented filter, search, Get/Added per row, lazy thumbnails.
interface RemotePet { slug: string; name: string; url: string; petJsonUrl?: string; kind: string; author: string; community: boolean }
let browseAll: RemotePet[] = [];
let bwCat = "all";
let bwShown = 0;
const BW_CHUNK = 60;

function initBrowse() {
  const modal = document.getElementById("browse-modal") as HTMLElement;
  const list = document.getElementById("bw-list") as HTMLElement;
  const status = document.getElementById("bw-status") as HTMLElement;
  const searchEl = document.getElementById("bw-search") as HTMLInputElement;

  (document.getElementById("open-browse") as HTMLButtonElement).onclick = async () => {
    modal.hidden = false;
    if (!browseAll.length) {
      status.style.display = "";
      status.textContent = t("Loading pets…");
      browseAll = await loadBrowseSources();
      if (!browseAll.length) {
        status.textContent = t("Couldn't load the pet library. Check your connection.");
        return;
      }
    }
    status.style.display = "none";
    repaint();
  };
  (document.getElementById("browse-done") as HTMLButtonElement).onclick = () => { modal.hidden = true; renderPage(); showCurrent(); };

  document.querySelectorAll<HTMLButtonElement>("#bw-cat button").forEach((b) => {
    b.onclick = () => {
      bwCat = b.dataset.v!;
      document.querySelectorAll("#bw-cat button").forEach((x) => x.classList.toggle("sel", x === b));
      repaint();
    };
  });
  searchEl.addEventListener("input", () => repaint());

  const thumbIO = new IntersectionObserver((entries) => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      const cv = e.target as HTMLCanvasElement;
      thumbIO.unobserve(cv);
      drawThumb(cv, cv.dataset.url!);
    }
  }, { root: list, rootMargin: "200px" });

  function filtered(): RemotePet[] {
    let v = browseAll;
    if (bwCat !== "all") v = v.filter((p) => p.kind === bwCat);
    const q = searchEl.value.trim().toLowerCase();
    if (q) v = v.filter((p) => p.name.toLowerCase().includes(q) || p.slug.includes(q));
    return v;
  }

  function repaint() {
    list.innerHTML = "";
    bwShown = 0;
    appendChunk();
  }

  function appendChunk() {
    const v = filtered();
    const installed = new Set(getLibrary().map((p) => p.slug));
    for (const p of v.slice(bwShown, bwShown + BW_CHUNK)) {
      const row = document.createElement("div");
      row.className = "bw-row";
      const cv = document.createElement("canvas");
      cv.width = 44; cv.height = 48; cv.className = "bw-thumb";
      cv.dataset.url = p.url;
      thumbIO.observe(cv);
      const meta = document.createElement("div");
      meta.className = "bw-meta";
      meta.innerHTML = `<span class="bw-name">${esc(p.name)}${p.community ? ` <span class="bw-badge">${t("Community")}</span>` : ""}</span>` +
        `<span class="cap">${t("by")} ${esc(p.author)}</span>`;
      const btn = document.createElement("button");
      if (installed.has(p.slug)) {
        btn.className = "bw-added";
        btn.textContent = `✓ ${t("Added")}`;
        btn.disabled = true;
      } else {
        btn.className = "mini";
        btn.textContent = t("Get");
        btn.onclick = () => {
          addToLibrary({ slug: p.slug, name: p.name, url: p.url, petJsonUrl: p.petJsonUrl });
          renderPage();
          btn.className = "bw-added";
          btn.textContent = `✓ ${t("Added")}`;
          btn.disabled = true;
        };
      }
      row.appendChild(cv);
      row.appendChild(meta);
      row.appendChild(btn);
      list.appendChild(row);
    }
    bwShown = Math.min(bwShown + BW_CHUNK, v.length);
  }

  list.addEventListener("scroll", () => {
    if (list.scrollTop + list.clientHeight > list.scrollHeight - 300) appendChunk();
  });
}

/// Community manifest first, Petdex library shuffled after, deduped by slug.
async function loadBrowseSources(): Promise<RemotePet[]> {
  const norm = (p: any, community: boolean): RemotePet | null => {
    if (!p?.slug || !p?.spritesheetUrl) return null;
    const author = (p.submittedBy || "").trim() || "community";
    return { slug: p.slug, name: p.displayName ?? p.slug, url: p.spritesheetUrl,
      petJsonUrl: p.petJsonUrl, kind: p.kind ?? "creature", author, community };
  };
  const fetchList = async (url: string, community: boolean): Promise<RemotePet[]> => {
    try {
      const j: any = await (await fetch(url)).json();
      return (j.pets ?? []).map((p: any) => norm(p, community)).filter(Boolean);
    } catch { return []; }
  };
  const [community, library] = await Promise.all([
    fetchList("https://agentpet.thenightwatcher.online/api/pets", true),
    fetchList("https://pets.thenightwatcher.online/manifest.json", false),
  ]);
  for (let i = library.length - 1; i > 0; i--) { // shuffle like macOS
    const j = Math.floor(Math.random() * (i + 1));
    [library[i], library[j]] = [library[j], library[i]];
  }
  const seen = new Set<string>();
  return [...community, ...library].filter((p) => seen.has(p.slug) ? false : (seen.add(p.slug), true));
}

// -------------------------------------------------------------- create ----
/// Auto-slice preview (mac SpriteSlicer equivalent): show the first row of
/// detected frames so the user sees how the sheet will animate.
function showSlicePreview(img: HTMLImageElement) {
  const wrap = document.getElementById("cr-preview-wrap") as HTMLElement | null;
  const cv = document.getElementById("cr-preview") as HTMLCanvasElement | null;
  const info = document.getElementById("cr-preview-info") as HTMLElement | null;
  if (!wrap || !cv || !info) return;
  const clips = slice(img);
  const rows = clips.length;
  const frames = rows > 0 ? clips[0].length : 0;
  if (!rows || !frames) {
    wrap.hidden = true;
    return;
  }
  wrap.hidden = false;
  const ctx = cv.getContext("2d");
  if (!ctx) return;
  ctx.clearRect(0, 0, cv.width, cv.height);
  const pad = 2;
  const scale = Math.min((cv.height - pad * 2) / clips[0][0].h, 1);
  clips[0].forEach((r, i) => {
    const x = pad + i * (r.w * scale + pad * 2);
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(img, r.x, r.y, r.w, r.h, x, pad, r.w * scale, r.h * scale);
  });
  info.textContent = `${rows} ${t("rows")} × ${frames} ${t("frames")} — auto-detected`;
}

function initCreate() {
  const modal = document.getElementById("create-modal") as HTMLElement;
  const name = document.getElementById("cr-name") as HTMLInputElement;
  const desc = document.getElementById("cr-desc") as HTMLInputElement;
  const fileName = document.getElementById("cr-file-name") as HTMLElement;
  const err = document.getElementById("cr-error") as HTMLElement;
  const createBtn = document.getElementById("cr-create") as HTMLButtonElement;
  let dataUrl = "";

  const filePick = document.createElement("input");
  filePick.type = "file";
  filePick.accept = "image/png,image/webp,image/*";
  filePick.style.display = "none";
  document.body.appendChild(filePick);

  const sync = () => { createBtn.disabled = !(name.value.trim() && dataUrl); };
  name.addEventListener("input", sync);

  (document.getElementById("open-create") as HTMLButtonElement).onclick = () => {
    modal.hidden = false;
    name.value = ""; desc.value = ""; dataUrl = "";
    fileName.textContent = t("No image selected");
    err.hidden = true;
    sync();
  };
  (document.getElementById("create-cancel") as HTMLButtonElement).onclick = () => { modal.hidden = true; };
  (document.getElementById("cr-choose") as HTMLButtonElement).onclick = () => {
    filePick.onchange = () => {
      const f = filePick.files?.[0];
      if (!f) return;
      const reader = new FileReader();
      reader.onload = () => {
        const img = new Image();
        img.onload = () => {
          dataUrl = String(reader.result);
          fileName.textContent = f.name;
          err.hidden = true;
          showSlicePreview(img);
          sync();
        };
        img.onerror = () => { err.textContent = t("Could not create this pet. Check that the image is a valid spritesheet."); err.hidden = false; };
        img.src = String(reader.result);
      };
      reader.readAsDataURL(f);
      filePick.value = "";
    };
    filePick.click();
  };
  createBtn.onclick = () => {
    const pet = { slug: `local-${Date.now()}`, name: name.value.trim(), url: dataUrl, custom: true };
    addToLibrary(pet);
    addInstanceFromLibrary(pet);
    modal.hidden = true;
  };
}

// --------------------------------------------------------- desktop pets ----
// Every card below represents a persistent PetInstance. The library provides
// sprite definitions only; it never doubles as an implicit "main" pet.
const MAX_DESKTOP_PETS = 12;

function initDesktopPets() {
  const capMsg = document.getElementById("extra-cap-msg") as HTMLElement;
  const emptyMsg = document.getElementById("desktop-empty") as HTMLElement;
  const context = document.getElementById("desktop-editor-context") as HTMLElement;
  const countEl = document.getElementById("extra-count") as HTMLElement;
  const runningEl = document.getElementById("desktop-instance-list") as HTMLDivElement;
  const closeAllBtn = document.getElementById("extra-close-all") as HTMLButtonElement;

  const instanceCard = (instance: PetInstance): HTMLElement => {
    const item = document.createElement("article");
    item.className = "desktop-instance";
    item.classList.toggle("selected", currentPetInstance()?.id === instance.id);

    const select = document.createElement("button");
    select.className = "desktop-instance-select";
    select.type = "button";
    select.setAttribute("aria-label", instance.name);
    const canvas = document.createElement("canvas");
    canvas.width = 48;
    canvas.height = 48;
    canvas.className = "pet-thumb";
    const definition = getLibrary().find((pet) => pet.slug === instance.spriteSlug);
    if (definition) drawThumb(canvas, definition.url);
    const name = document.createElement("span");
    name.textContent = instance.name;
    select.append(canvas, name);
    select.onclick = () => {
      persistDesktopPets(selectPetInstance(currentPetStore(), instance.id), { syncWindows: false });
      if (definition) livingPets.loadPet(definition.url);
      renderDesktopPets();
      renderPage();
      renderCare();
    };

    const remove = document.createElement("button");
    remove.className = "icon-btn desktop-instance-remove";
    remove.type = "button";
    remove.title = t("Remove");
    remove.setAttribute("aria-label", t("Remove"));
    remove.innerHTML = uiIcon("x");
    remove.onclick = () => {
      persistDesktopPets(removePetInstance(currentPetStore(), instance.id));
      renderDesktopPets();
      renderPage();
      renderCare();
    };

    item.append(select, remove);
    return item;
  };

  const renderDesktopPets = () => {
    const store = currentPetStore();
    const selected = selectedPetInstance(store);
    countEl.textContent = `(${store.instances.length}/${MAX_DESKTOP_PETS})`;
    closeAllBtn.hidden = store.instances.length === 0;
    capMsg.hidden = store.instances.length < MAX_DESKTOP_PETS;
    emptyMsg.hidden = store.instances.length > 0;
    runningEl.hidden = store.instances.length === 0;
    context.textContent = selected ? `${t("Editing")}: ${selected.name}` : "";
    runningEl.innerHTML = "";
    for (const instance of store.instances) runningEl.appendChild(instanceCard(instance));
  };

  refreshDesktopPets = renderDesktopPets;
  closeAllBtn.onclick = () => {
    let store = currentPetStore();
    for (const instance of [...store.instances]) store = removePetInstance(store, instance.id);
    persistDesktopPets(store);
    renderDesktopPets();
    renderPage();
    renderCare();
  };

  renderDesktopPets();
  document.addEventListener("ap-pet-tab-shown", renderDesktopPets);
}

// ---------------------------------------------------------------- bubble ----
const MSG_STATES: [string, string][] = [
  ["working", "Working"], ["waiting", "Needs you"], ["done", "Done"],
  ["celebrate", "Celebrate"], ["idle", "Idle"],
];
function initBubble() {
  const changed = () => { emit("bubble-changed", null); };
  const opacity = document.getElementById("opacity") as HTMLInputElement;
  const editors = document.getElementById("msg-editors")!;

  opacity.value = localStorage.getItem("ap_opacity") || "92";
  opacity.oninput = () => {
    localStorage.setItem("ap_opacity", opacity.value);
    changed();
    document.dispatchEvent(new CustomEvent("seg-changed", { detail: "ap_opacity" }));
  };

  const build = () => {
    editors.innerHTML = "";
    for (const [st, label] of MSG_STATES) {
      const wrap = document.createElement("div");
      wrap.className = "msg-editor";
      const lbl = document.createElement("div");
      lbl.className = "msg-label";
      lbl.dataset.label = label;
      lbl.textContent = t(label);
      const ta = document.createElement("textarea");
      const key = `ap_msg_all_${st}`;
      ta.value = localStorage.getItem(key) || "";
      ta.addEventListener("input", () => { localStorage.setItem(key, ta.value); changed(); });
      wrap.appendChild(lbl);
      wrap.appendChild(ta);
      editors.appendChild(wrap);
    }
  };
  build();

  // System/custom source (segmented, saved by initSegs) + reset.
  const customWrap = document.getElementById("msg-custom-wrap") as HTMLElement;
  const syncSrc = () => { customWrap.style.display = (localStorage.getItem("ap_msg_src") || "system") === "custom" ? "" : "none"; };
  syncSrc();
  document.addEventListener("seg-changed", (e) => { if ((e as CustomEvent).detail === "ap_msg_src") syncSrc(); });
  (document.getElementById("msg-reset") as HTMLButtonElement).onclick = () => {
    for (const [st] of MSG_STATES) localStorage.removeItem(`ap_msg_all_${st}`);
    build();
    changed();
  };

  const idle = document.getElementById("idle") as HTMLInputElement;
  idle.checked = localStorage.getItem("ap_idle") !== "0";
  idle.onchange = () => { localStorage.setItem("ap_idle", idle.checked ? "1" : "0"); changed(); };
}

// ----------------------------------------------- instance controls ----
function initPetControls() {
  const size = document.getElementById("pet-size") as HTMLInputElement;
  const roamMode = document.getElementById("roam-mode") as HTMLSelectElement;
  const roamSpeed = document.getElementById("roam-speed") as HTMLInputElement;
  const roamSpeedVal = document.getElementById("roam-speed-val") as HTMLSpanElement;
  const wanderPauseMin = document.getElementById("wander-pause-min") as HTMLInputElement;
  const wanderPauseMax = document.getElementById("wander-pause-max") as HTMLInputElement;

  const updateSelected = (patch: Partial<Omit<PetInstance, "id">>) => {
    const instance = currentPetInstance();
    if (!instance) return;
    persistDesktopPets(
      updatePetInstance(currentPetStore(), instance.id, patch),
      { syncWindows: false, instanceId: instance.id },
    );
  };

  const sync = () => {
    const instance = currentPetInstance();
    size.disabled = !instance;
    roamMode.disabled = !instance;
    roamSpeed.disabled = !instance;
    wanderPauseMin.disabled = !instance;
    wanderPauseMax.disabled = !instance;
    size.value = String(instance?.size ?? 100);
    roamMode.value = instance?.roamMode ?? "wander";
    roamSpeed.value = String(instance?.roamSpeed ?? 5);
    roamSpeedVal.textContent = roamSpeed.value;
    wanderPauseMin.value = String((instance?.wanderPauseMinMs ?? DEFAULT_WANDER_PAUSE_MIN_MS) / 1000);
    wanderPauseMax.value = String((instance?.wanderPauseMaxMs ?? DEFAULT_WANDER_PAUSE_MAX_MS) / 1000);
  };

  size.oninput = () => updateSelected({ size: parseInt(size.value, 10) });
  document.querySelectorAll<HTMLButtonElement>(".size-presets button").forEach((button) => {
    button.onclick = () => {
      size.value = button.dataset.size!;
      updateSelected({ size: parseInt(size.value, 10) });
    };
  });
  roamMode.onchange = () => updateSelected({ roamMode: roamMode.value as PetInstance["roamMode"] });
  roamSpeed.oninput = () => updateSelected({ roamSpeed: parseInt(roamSpeed.value, 10) });
  const saveWanderPause = () => {
    updateSelected({
      wanderPauseMinMs: Math.round(Number.parseFloat(wanderPauseMin.value) * 1000),
      wanderPauseMaxMs: Math.round(Number.parseFloat(wanderPauseMax.value) * 1000),
    });
    sync();
  };
  wanderPauseMin.onchange = saveWanderPause;
  wanderPauseMax.onchange = saveWanderPause;

  sync();
  void listen("pets-changed", sync);
  void listen<{ instanceId: string }>("pet-instance-changed", (event) => {
    if (currentPetInstance()?.id === event.payload.instanceId) sync();
  });
}

// ------------------------------------------------------------ animations ----
// The macOS AnimationPicker: a segmented mood selector over a grid of clip
// thumbnails sliced from the current pet's sheet. Hover = animated preview.
// Non-idle moods use single-select binding (ap_bind_<mood>). The idle mood
// uses multi-select: the checked clips form a playlist cycled while idle.
const MOOD_DEFAULT_ROW: Record<string, number> = { idle: 0, working: 7, waiting: 6, done: 3, celebrate: 4 };

const IDLE_CLIPS_KEY = "ap_idle_clips";
const IDLE_MODE_KEY = "ap_idle_mode";
const IDLE_INTERVAL_KEY = "ap_idle_interval";
const DEFAULT_IDLE_INTERVAL = 5;

function initAnimations() {
  const grid = document.getElementById("anim-grid")!;
  const moodSeg = document.getElementById("anim-mood")!;
  let mood = "working";
  let img: HTMLImageElement | null = null;
  let clips: Rect[][] = [];
  let hoverTimer: number | null = null;

  // Container for idle-only controls (mode + interval); injected below the grid.
  const idleWrap = document.createElement("div");
  idleWrap.className = "idle-playlist-wrap";
  grid.parentElement!.appendChild(idleWrap);

  const boundClip = (m: string) => {
    const v = parseInt(localStorage.getItem(`ap_bind_${m}`) ?? "", 10);
    return Number.isFinite(v) && v >= 0 ? Math.min(v, Math.max(0, clips.length - 1)) : Math.min(MOOD_DEFAULT_ROW[m] ?? 0, Math.max(0, clips.length - 1));
  };

  const readIdleClips = (): number[] => {
    try {
      const v = JSON.parse(localStorage.getItem(IDLE_CLIPS_KEY) || "[]");
      if (Array.isArray(v) && v.length) return v.filter((x) => Number.isFinite(x) && x >= 0).map((x) => Number(x));
    } catch {}
    return clips.length ? [boundClip("idle")] : [];
  };
  const saveIdleClips = (vals: number[]) => {
    const clean = vals.filter((x) => Number.isFinite(x) && x >= 0).map((x) => Number(x));
    localStorage.setItem(IDLE_CLIPS_KEY, JSON.stringify(clean));
    emit("bubble-changed", null);
  };
  const readIdleMode = () => localStorage.getItem(IDLE_MODE_KEY) || "random";
  const readIdleInterval = () => {
    const n = Number.parseFloat(localStorage.getItem(IDLE_INTERVAL_KEY) ?? "");
    return Number.isFinite(n) && n >= 1 ? n : DEFAULT_IDLE_INTERVAL;
  };

  const drawFrame = (cv: HTMLCanvasElement, clip: Rect[], frame: number) => {
    const ctx = cv.getContext("2d");
    if (!ctx || !img || !clip.length) return;
    const r = clip[frame % clip.length];
    const maxW = Math.max(...clip.map((x) => x.w));
    const sc = Math.min(cv.width / maxW, cv.height / r.h);
    const dw = r.w * sc, dh = r.h * sc;
    ctx.imageSmoothingEnabled = false;
    ctx.clearRect(0, 0, cv.width, cv.height);
    ctx.drawImage(img, r.x, r.y, r.w, r.h, (cv.width - dw) / 2, cv.height - dh, dw, dh);
  };

  const paint = () => {
    grid.innerHTML = "";
    if (!clips.length) { idleWrap.style.display = "none"; return; }

    const isIdle = mood === "idle";
    const idleSelected = new Set(readIdleClips());
    const currentBinding = boundClip(mood);

    clips.forEach((clip, i) => {
      const cell = document.createElement("button");
      const selected = isIdle ? idleSelected.has(i) : i === currentBinding;
      cell.className = "anim-cell" + (selected ? " sel" : "");
      const cv = document.createElement("canvas");
      cv.width = 54; cv.height = 44;
      drawFrame(cv, clip, 0);
      const label = document.createElement("span");
      label.className = "cap";
      label.textContent = `${t("Clip")} ${i + 1}`;
      cell.appendChild(cv);
      cell.appendChild(label);
      cell.onclick = () => {
        if (isIdle) {
          // Multi-select playlist; never leave it empty.
          if (idleSelected.has(i) && idleSelected.size <= 1) return;
          if (idleSelected.has(i)) idleSelected.delete(i);
          else idleSelected.add(i);
          saveIdleClips(Array.from(idleSelected).sort((a, b) => a - b));
        } else {
          localStorage.setItem(`ap_bind_${mood}`, String(i));
        }
        emit("bubble-changed", null);
        paint();
      };
      // Hover = animate this clip (mac hover preview).
      cell.onmouseenter = () => {
        let f = 0;
        if (hoverTimer) clearInterval(hoverTimer);
        hoverTimer = window.setInterval(() => drawFrame(cv, clip, ++f), 125);
      };
      cell.onmouseleave = () => {
        if (hoverTimer) clearInterval(hoverTimer);
        hoverTimer = null;
        drawFrame(cv, clip, 0);
      };
      grid.appendChild(cell);
    });
    paintIdleControls();
  };

  const paintIdleControls = () => {
    idleWrap.innerHTML = "";
    idleWrap.style.display = mood === "idle" && clips.length ? "block" : "none";
    if (mood !== "idle" || !clips.length) return;

    const title = document.createElement("div");
    title.className = "idle-title";
    title.textContent = t("Idle animations");
    idleWrap.appendChild(title);

    const note = document.createElement("div");
    note.className = "cap";
    note.style.cssText = "margin: 0 0 8px; color: rgba(255,255,255,0.55);";
    note.textContent = t("Pick clips to cycle while idle.");
    idleWrap.appendChild(note);

    const toolbar = document.createElement("div");
    toolbar.className = "idle-toolbar";

    const allBtn = document.createElement("button");
    allBtn.className = "link";
    allBtn.textContent = t("Select all");
    allBtn.onclick = () => { saveIdleClips(clips.map((_, i) => i)); paint(); };

    const clearBtn = document.createElement("button");
    clearBtn.className = "link";
    clearBtn.textContent = t("Clear");
    clearBtn.onclick = () => { saveIdleClips([boundClip("idle")]); paint(); };

    toolbar.appendChild(allBtn);
    toolbar.appendChild(clearBtn);
    idleWrap.appendChild(toolbar);

    const modeRow = document.createElement("div");
    modeRow.className = "idle-mode-row";
    const modeLabel = document.createElement("span");
    modeLabel.className = "cap";
    modeLabel.textContent = t("Mode");
    modeRow.appendChild(modeLabel);

    const modeSeg = document.createElement("span");
    modeSeg.className = "seg";
    for (const [v, label] of [["random", t("Random")], ["sequential", t("Sequential")]] as const) {
      const b = document.createElement("button");
      b.textContent = label;
      b.classList.toggle("sel", readIdleMode() === v);
      b.onclick = () => {
        localStorage.setItem(IDLE_MODE_KEY, v);
        paintIdleControls();
      };
      modeSeg.appendChild(b);
    }
    modeRow.appendChild(modeSeg);
    idleWrap.appendChild(modeRow);

    const intRow = document.createElement("div");
    intRow.className = "idle-interval-row";
    const intLabel = document.createElement("span");
    intLabel.className = "cap";
    intLabel.textContent = t("Interval");
    intRow.appendChild(intLabel);

    const intInput = document.createElement("input");
    intInput.type = "number";
    intInput.min = "1";
    intInput.step = "1";
    intInput.value = String(readIdleInterval());
    intInput.onchange = () => {
      const n = Number.parseFloat(intInput.value);
      localStorage.setItem(IDLE_INTERVAL_KEY, String(Number.isFinite(n) && n >= 1 ? n : DEFAULT_IDLE_INTERVAL));
      emit("bubble-changed", null);
    };
    intRow.appendChild(intInput);

    const intUnit = document.createElement("span");
    intUnit.className = "cap";
    intUnit.textContent = t("seconds");
    intRow.appendChild(intUnit);
    idleWrap.appendChild(intRow);
  };

  moodSeg.querySelectorAll<HTMLButtonElement>("button").forEach((b) => {
    b.onclick = () => {
      mood = b.dataset.v!;
      moodSeg.querySelectorAll("button").forEach((x) => x.classList.toggle("sel", x === b));
      paint();
    };
  });

  const loadSheet = () => {
    const lib = getLibrary();
    const sel = lib.find((pet) => pet.slug === currentPetInstance()?.spriteSlug) ?? lib[0];
    const url = sel?.url;
    if (!url) { setTimeout(loadSheet, 3000); return; } // library may seed late
    const im = new Image();
    im.crossOrigin = "anonymous";
    im.onload = () => { img = im; clips = slice(im); paint(); };
    im.onerror = () => { img = null; clips = []; grid.innerHTML = ""; };
    im.src = url.startsWith("data:") ? url : url + (url.includes("?") ? "&" : "?") + "cors=1";
  };
  loadSheet();
  void listen("pets-changed", () => setTimeout(loadSheet, 50));
}

// ----------------------------------------------------------------- sounds ----
let settingsAudioCtx: AudioContext | null = null;
function playSound(ev: "done" | "waiting") {
  const data = localStorage.getItem(`ap_sound_${ev}_data`);
  if (data) {
    try { void new Audio(data).play(); return; } catch {}
  }
  try {
    settingsAudioCtx = settingsAudioCtx || new AudioContext();
    const o = settingsAudioCtx.createOscillator();
    const g = settingsAudioCtx.createGain();
    o.type = "sine";
    o.frequency.value = ev === "done" ? 880 : 560;
    g.gain.value = 0.05;
    o.connect(g);
    g.connect(settingsAudioCtx.destination);
    o.start();
    o.stop(settingsAudioCtx.currentTime + 0.13);
  } catch {}
}

function initSounds() {
  const filePick = document.createElement("input");
  filePick.type = "file";
  filePick.accept = "audio/*";
  filePick.style.display = "none";
  document.body.appendChild(filePick);

  const syncNames = () => {
    for (const ev of ["done", "waiting"] as const) {
      const name = localStorage.getItem(`ap_sound_${ev}_name`);
      (document.getElementById(`sound-${ev}-name`) as HTMLElement).textContent = name || t("Default");
      (document.getElementById(`t-df-${ev}`) as HTMLElement).style.display = name ? "" : "none";
    }
  };
  syncNames();

  document.querySelectorAll<HTMLButtonElement>(".sound-btns .mini").forEach((b) => {
    const ev = b.dataset.ev as "done" | "waiting";
    b.onclick = () => {
      switch (b.dataset.act) {
        case "play": playSound(ev); break;
        case "reset":
          localStorage.removeItem(`ap_sound_${ev}_data`);
          localStorage.removeItem(`ap_sound_${ev}_name`);
          syncNames();
          break;
        case "upload":
          filePick.onchange = () => {
            const f = filePick.files?.[0];
            if (!f) return;
            if (f.size > 2_000_000) { alert(t("Sound file too large (max 2 MB)")); return; }
            const reader = new FileReader();
            reader.onload = () => {
              localStorage.setItem(`ap_sound_${ev}_data`, String(reader.result));
              localStorage.setItem(`ap_sound_${ev}_name`, f.name);
              syncNames();
              playSound(ev); // preview, like macOS
            };
            reader.readAsDataURL(f);
            filePick.value = "";
          };
          filePick.click();
          break;
      }
    };
  });
}

// --------------------------------------------------------- notifications ----
function initNotify() {
  const box = document.getElementById("notify") as HTMLInputElement;
  box.checked = localStorage.getItem("ap_notify") !== "0";
  box.addEventListener("change", () => localStorage.setItem("ap_notify", box.checked ? "1" : "0"));
  // Per-event sound toggles (mac SoundSettings); legacy ap_sound seeds both.
  const legacyOff = localStorage.getItem("ap_sound") === "0";
  for (const ev of ["done", "waiting"] as const) {
    const el = document.getElementById(`sound-${ev}`) as HTMLInputElement;
    const key = `ap_sound_${ev}`;
    el.checked = (localStorage.getItem(key) ?? (legacyOff ? "0" : "1")) !== "0";
    el.addEventListener("change", () => localStorage.setItem(key, el.checked ? "1" : "0"));
  }
}

// --------------------------------------------------------------- startup ----
async function initAutostart() {
  const box = document.getElementById("autostart") as HTMLInputElement;
  try { box.checked = await isEnabled(); } catch {}
  box.addEventListener("change", async () => {
    try { box.checked ? await enable() : await disable(); } catch (e) { alert(String(e)); }
  });
}

// --------------------------------------------------------------- motion ----
function initReduceMotion() {
  const box = document.getElementById("reduce-motion") as HTMLInputElement;
  const apply = () => {
    const on = box.checked;
    localStorage.setItem("ap_reduce_motion", on ? "1" : "0");
    document.body.classList.toggle("reduce-motion", on);
  };
  box.checked = localStorage.getItem("ap_reduce_motion") === "1";
  box.addEventListener("change", apply);
  apply();
}

// ----------------------------------------------------------------- icons ----
/// Fill every `<span class="ui-ic" data-icon="name">` with the matching SVG.
function initIcons() {
  document.querySelectorAll<HTMLElement>(".ui-ic[data-icon]").forEach((el) => {
    const name = el.dataset.icon;
    if (!name) return;
    const svg = uiIcon(name);
    if (svg) el.innerHTML = svg;
  });
}

// ----------------------------------------------------------------- i18n ----
function applyStatic() {
  document.documentElement.lang = getLang();
  const set = (id: string, key: string) => { const el = document.getElementById(id); if (el) el.textContent = t(key); };
  // tabs
  set("tab-general", "General");
  set("tab-pet", "Pet");
  set("tab-bubble", "Bubble");
  set("tab-care", "Care");
  set("tab-advanced", "Advanced");
  // page titles / subtitles
  set("t-pet-title", "Your companion");
  set("t-pet-subtitle", "Choose, dress up, and animate your desktop pet.");
  set("t-bubble-title", "Bubble");
  set("t-bubble-subtitle", "Appearance, style, and quick messages.");
  set("t-care-title", "Care");
  set("t-care-subtitle", "Feed, level up, and check in on your companion.");
  set("t-general-title", "General");
  set("t-general-subtitle", "Language, launch, notifications, and app info.");
  set("t-advanced-title", "Advanced");
  // general
  set("t-lang", "Language");
  set("t-lang2", "Language");
  set("t-startup", "Launch");
  set("t-autostart", "Launch at login");
  set("t-autostart-sub", "DesktopPet starts automatically when you sign in.");
  set("t-notif", "Notifications");
  set("t-notify", "Notifications on");
  set("t-notify-sub", "Alerts when something needs your attention.");
  set("t-motion", "Motion");
  set("t-reduce-motion", "Reduce motion");
  set("t-reduce-motion-sub", "Disable idle animations and visual effects to lower GPU usage.");
  set("t-sounds", "Sounds");
  set("t-sound-done", "When a task finishes");
  set("t-sound-waiting", "When your pet needs you");
  set("t-up-done", "Upload…");
  set("t-up-waiting", "Upload…");
  set("t-df-done", "Default");
  set("t-df-waiting", "Default");
  set("t-app", "About");
  set("t-version", "Version");
  set("quit-btn", "Quit DesktopPet");
  // pet
  set("t-pet-sub", "Pick the companion that floats on your desktop.");
  set("t-show-pets", "Show desktop pets");
  set("t-show-pets-sub", "Temporarily hide or show every pet without removing it from your desktop.");
  set("t-library", "Pet library");
  set("t-library-sub", "Add any material to your desktop.");
  set("t-lib-empty", "No pets yet. Tap Browse to add one.");
  set("t-browse", "Browse pets…");
  set("t-create", "Create pet…");
  set("t-bw-title", "Browse pets");
  set("browse-done", "Done");
  set("t-bw-all", "All");
  set("t-bw-char", "Characters");
  set("t-bw-crea", "Creatures");
  set("t-bw-obj", "Objects");
  set("t-cr-title", "Create pet");
  set("create-cancel", "Cancel");
  set("t-cr-name", "Name");
  set("t-cr-desc", "Description");
  set("t-cr-sheet", "Spritesheet");
  set("t-cr-hint", "Use the same 8×9 transparent spritesheet format as downloaded pets.");
  set("cr-create", "Create");
  set("cr-choose", "Choose image…");
  set("t-size", "Size on screen");
  set("t-extra", "Desktop pets");
  set("t-extra-sub", "Every pet is saved independently, with its own name, care, size, and roam settings.");
  set("t-extra-closeall", "Remove all");
  set("t-desktop-empty", "No desktop pets yet. Add one from your library.");
  set("t-extra-cap", "Desktop limit reached. Remove one to add another.");
  set("t-anims", "Animations");
  set("t-anim-hint", "Hover a clip to preview it.");
  set("am-idle", "Idle");
  set("am-working", "Working");
  set("am-waiting", "Waiting");
  set("am-done", "Done");
  set("am-celebrate", "Celebrate");
  // care
  set("t-care-head", "Your companion");
  set("t-care-stats", "Stats");
  set("t-care-help", "Feeding earns XP; your pet levels up through five stages. Chat with it, finish tasks and let it summarize your day.");
  set("care-empty", "Pick a pet in the Pet tab to start raising it.");
  set("care-feed-btn", "Feed");
  set("t-care-ach", "Achievements");
  set("t-care-today", "Today");
  set("t-care-streak", "Streak");
  set("t-care-lifetime", "Lifetime");
  set("t-care-sessions", "Sessions");
  set("care-streak-sub", "days fed");
  set("care-lifetime-sub", "XP earned");
  set("care-sessions-sub", "completed");
  set("t-care-burn", "Burn, last 7 days");
  // bubble
  set("t-appearance", "Appearance");
  set("t-theme", "Theme");
  set("t-opacity", "Opacity");
  set("t-fontsize", "Text size");
  set("o-dark", "Dark");
  set("o-light", "Light");
  set("o-theme-system", "System");
  set("t-idle", "Show idle message");
  set("t-idle-sub", "The pet's chatter while nothing is happening.");
  set("t-reactive-head", "Reactive comments");
  set("t-reactive", "React to activity");
  set("t-reactive-sub", "The pet reacts to token usage, streaks, hunger, and busy sessions.");
  set("t-display", "Display");
  set("t-rows", "Rows");
  set("o-bm-list", "All rows");
  set("o-bm-carousel", "Carousel");
  set("o-bm-compact", "Compact");
  set("t-grouping", "Sessions");
  set("o-bg-all", "All sessions");
  set("t-maxrows", "Max rows");
  set("t-filter", "Include states");
  set("t-vocab-foot", "Whimsical phrases shown while something is happening, e.g. \"Brewing…\" or \"Compiling…\".");
  set("t-current", "Current pet");
  set("t-ball", "Floating ball");
  set("t-ball-on", "Show floating ball");
  set("t-ball-on-sub", "A draggable ball on your desktop. Left-click for a bubble, right-click for settings. Snaps to screen edges.");
  set("t-roam", "Roam");
  set("t-roam-mode", "Mode");
  set("t-roam-speed", "Speed");
  set("t-wander-pause", "Wander pause");
  set("t-wander-pause-unit", "seconds");
  set("t-roam-stay", "Stay");
  set("t-roam-wander", "Wander");
  set("t-roam-cursor", "Follow cursor");
  set("t-roam-climb", "Climb windows");
  set("t-style", "Style");
  set("t-separator", "Separator");
  set("t-dotstyle", "State dot");
  set("o-dot-plain", "Plain dot");
  set("t-ic-brand", "Brand logos");
  set("t-ic-sym", "Symbols");
  set("t-icon-title", "Icon");
  set("t-icon-done", "Done");
  set("t-icon-reset", "Reset to default");
  set("t-care-feed-sub", "A snack for your companion. Feeding earns XP.");
  set("t-click", "Left-click pet");
  set("t-click-action", "Action");
  set("t-click-sub", "What happens when you left-click a pet without dragging. Uses a random line from your quick bubbles below.");
  set("o-lc-none", "Off");
  set("o-lc-self", "This pet");
  set("o-lc-all", "All pets");
  set("t-quick", "Quick bubbles");
  set("t-quick-sub", "One message per line. Left-click a pet or send from the floating ball to show one at random.");
  set("t-quick-duration", "Display duration");
  set("t-quick-duration-unit", "seconds");
  set("t-quick-help", "Shift-click a preset on the floating ball to delete it.");
  set("t-messages", "Bubble messages");
  set("t-msg-src", "Messages");
  set("o-ms-system", "System");
  set("o-ms-custom", "Custom");
  set("msg-reset", "Reset to defaults");
  set("o-sep-space", "space");
  set("quick-reset", "Reset");
  set("t-msg-help", "One message per line; a random one is shown.");
  document.querySelectorAll<HTMLElement>(".msg-label").forEach((el) => {
    if (el.dataset.label) el.textContent = t(el.dataset.label);
  });
  // bottom bar + demo panel
  set("t-dp-quick", "Quick scenarios");
  set("dp-spawn", "Spawn 3 working");
  set("dp-finish", "Finish all");
  set("dp-clear", "Clear all");
  set("t-multi-sub", "Structured rows with icons, state dots, and activity messages.");
  set("t-history", "Session history");
  set("t-fontsize", "Font size");
  search.placeholder = t("Search your pets");
  (document.getElementById("bw-search") as HTMLInputElement).placeholder = t("Search pets");
}

// ------------------------------------------------- version / quit / links ----
function initMisc() {
  getVersion().then((v) => {
    const a = document.getElementById("app-version");
    if (a) a.textContent = v;
    const a2 = document.getElementById("app-version2");
    if (a2) a2.textContent = v;
  }).catch(() => {});
  (document.getElementById("quit-btn") as HTMLButtonElement).onclick = () => { exit(0); };
  document.querySelectorAll<HTMLElement>("[data-url]").forEach((el) => {
    el.addEventListener("click", () => invoke("open_url", { url: el.dataset.url }).catch(() => {}));
  });
}

function initLang() {
  const sel = document.getElementById("lang") as HTMLSelectElement;
  sel.value = getLang();
  applyStatic();
  // Tell the tray (Rust) + the pet window about the initial language too.
  invoke("set_lang", { code: getLang() }).catch(() => {});
  sel.addEventListener("change", async () => {
    setLang(sel.value as Lang);
    applyStatic();
    showCurrent();
    invoke("set_lang", { code: getLang() }).catch(() => {});
    await emit("lang-changed", getLang());
  });
}

function esc(s: string): string {
  return s.replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c] || c));
}

// Paint the filled-left part of every slider (drives the --fill CSS variable)
// and the numeric value label next to it.
function initSliders() {
  document.querySelectorAll<HTMLInputElement>('input[type="range"]').forEach((r) => {
    const val = document.getElementById(`${r.id}-val`);
    const paint = () => {
      const min = Number(r.min) || 0;
      const max = Number(r.max) || 100;
      const pct = ((Number(r.value) - min) / (max - min)) * 100;
      r.style.setProperty("--fill", `${pct}%`);
      if (val) val.textContent = r.value + (r.id === "opacity" ? "%" : "");
    };
    r.addEventListener("input", paint);
    paint();
  });
}

// -------------------------------------------------------- floating ball ----
// Toggles whether the floating-ball window is visible. The state lives in a
// Rust-side file (read at launch so the ball can spawn before Settings opens),
// so we go through commands instead of localStorage.
function initFloatingBall() {
  const box = document.getElementById("ball-on") as HTMLInputElement | null;
  if (!box) return;
  invoke<boolean>("get_floating_ball_visible")
    .then((v) => { box.checked = v; })
    .catch(() => { box.checked = true; });
  box.addEventListener("change", () => {
    invoke("set_floating_ball_visible", { visible: box.checked }).catch(() => {});
  });
}

// -------------------------------------------------------- quick bubbles ----
// The quick-bubble preset pool: one message per line, shared by the floating
// ball (send menu) and left-click-on-pet (random line). Stored in localStorage
// as a JSON array; the textarea shows raw text for easy editing.
const QUICK_KEY = "ap_quick_bubbles";
const QUICK_DEFAULTS = [
  "Hello!",
  "Coding…",
  "Need a break?",
  "What's up?",
  "Let's ship something.",
];
function readQuickList(): string[] {
  try {
    const v = JSON.parse(localStorage.getItem(QUICK_KEY) || "[]");
    return Array.isArray(v) ? v.filter((x: unknown) => typeof x === "string") : [];
  } catch { return []; }
}
function writeQuickList(list: string[]) {
  localStorage.setItem(QUICK_KEY, JSON.stringify(list));
  emit("bubble-changed", null); // floating ball listens and refreshes its presets
}
function initQuickBubbles() {
  const ta = document.getElementById("quick-bubbles") as HTMLTextAreaElement | null;
  const reset = document.getElementById("quick-reset") as HTMLButtonElement | null;
  const duration = document.getElementById("quick-bubble-duration") as HTMLInputElement | null;
  if (!ta) return;
  if (duration) {
    const syncDuration = () => {
      duration.value = String(normalizeQuickBubbleDurationSeconds(localStorage.getItem(QUICK_BUBBLE_DURATION_KEY)));
    };
    syncDuration();
    duration.onchange = () => {
      const seconds = normalizeQuickBubbleDurationSeconds(duration.value);
      localStorage.setItem(QUICK_BUBBLE_DURATION_KEY, String(seconds));
      syncDuration();
    };
  }
  const current = readQuickList();
  ta.value = (current.length ? current : QUICK_DEFAULTS).join("\n");
  if (!current.length) writeQuickList(QUICK_DEFAULTS);
  ta.addEventListener("change", () => {
    const lines = ta.value.split("\n").map((s) => s.trim()).filter(Boolean);
    writeQuickList(lines);
  });
  if (reset) reset.onclick = () => {
    ta.value = QUICK_DEFAULTS.join("\n");
    writeQuickList(QUICK_DEFAULTS);
  };
}

initTabs();
initLang();
initIcons();
renderCare();
initPet();
initDesktopPetsVisibility();
initPetControls();
initBubble();
initAnimations();
initSounds();
initNotify();
initAutostart();
initReduceMotion();
initSliders();
initSegs();
initMisc();
initDesktopPets();
initFloatingBall();
initQuickBubbles();
