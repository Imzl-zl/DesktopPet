import { afterEach, describe, expect, it, vi } from "vitest";

const storage = new Map<string, string>();

vi.stubGlobal("localStorage", {
  getItem: vi.fn((key: string) => storage.get(key) ?? null),
  setItem: vi.fn((key: string, value: string) => storage.set(key, value)),
});

afterEach(() => {
  storage.clear();
  vi.resetModules();
});

describe("visible Windows strings", () => {
  it("translates the shared Settings and level-up labels in every supported non-English language", async () => {
    const { setLang, t } = await import("./i18n");

    for (const lang of ["vi", "zh", "zh-TW"] as const) {
      setLang(lang);
      for (const key of ["Create", "Icon", "Stats", "Level up", "Your little companion"]) {
        expect(t(key), `${lang}: ${key}`).not.toBe(key);
      }
    }
  });
});
