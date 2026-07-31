// Tracks live activities and derives the pet's mood — a TS port of the macOS
// DesktopPetCore/ActivityStore + MoodResolver. Phase 1 has no producers yet
// (desktop monitoring / conversation land in Phase 2); the store and event
// shape are the reserved interface they will publish into.

export type ActivityState = "active" | "paused" | "done" | "idle";

export interface ActivitySession {
  id: string;
  kind: string;
  state: ActivityState;
  title: string;
  detail?: string;
  project?: string;
  createdAt: number;
  updatedAt: number;
  stateSince: number;
}

export interface ActivityEventPayload {
  id: string;
  kind: string;
  source: string;
  timestamp: number;
  title: string;
  detail?: string;
  weight: number;
  project?: string;
}

const PRIORITY: Record<ActivityState, number> = { active: 4, paused: 3, done: 2, idle: 0 };
// Timeouts mirror the macOS ActivityStore: done falls back to idle briefly,
// then idle is removed; active/paused activities that go quiet are dropped.
const DONE_TO_IDLE_MS = 30_000;
const REMOVE_IDLE_MS = 600_000;
const STALE_ACTIVE_MS = 300_000;
const STALE_PAUSED_MS = 90_000;

export class ActivityStore {
  private activities = new Map<string, ActivitySession>();

  /// The normalized state a fresh event of this kind starts in
  /// (port of ActivityStore.initialState).
  initialState(kind: string): ActivityState {
    switch (kind) {
      case "appFocus":
      case "inputBurst":
      case "agentActivity":
        return "active";
      default:
        return "done"; // chatMessage / dailySummary / userAction
    }
  }

  /// Applies an event, creating or updating the matching activity.
  apply(e: ActivityEventPayload): ActivitySession | null {
    const now = e.timestamp > 0 ? e.timestamp : Date.now();
    const state = this.initialState(e.kind);
    const prev = this.activities.get(e.id);
    if (prev) {
      const next: ActivitySession = {
        ...prev,
        state,
        stateSince: prev.state === state ? prev.stateSince : now,
        title: e.title,
        detail: e.detail ?? prev.detail,
        project: e.project ?? prev.project,
        updatedAt: now,
      };
      this.activities.set(e.id, next);
      return next;
    }
    const created: ActivitySession = {
      id: e.id,
      kind: e.kind,
      state,
      title: e.title,
      detail: e.detail,
      project: e.project,
      createdAt: now,
      updatedAt: now,
      stateSince: now,
    };
    this.activities.set(e.id, created);
    return created;
  }

  remove(id: string) {
    this.activities.delete(id);
  }

  clear() {
    this.activities.clear();
  }

  snapshot(): ActivitySession[] {
    return [...this.activities.values()];
  }

  /// Prune stale activities; returns the list (highest priority first).
  active(): ActivitySession[] {
    const now = Date.now();
    for (const [k, s] of [...this.activities]) {
      const quiet = now - s.updatedAt;
      if (s.state === "done" && quiet > DONE_TO_IDLE_MS) {
        this.activities.set(k, { ...s, state: "idle", updatedAt: now, stateSince: now });
      } else if (s.state === "idle" && quiet > REMOVE_IDLE_MS) {
        this.activities.delete(k);
      } else if (s.state === "paused" && quiet > STALE_PAUSED_MS) {
        this.activities.delete(k);
      } else if (s.state === "active" && quiet > STALE_ACTIVE_MS) {
        this.activities.delete(k);
      }
    }
    return [...this.activities.values()].sort(
      (a, b) => (PRIORITY[b.state] ?? 0) - (PRIORITY[a.state] ?? 0) || b.updatedAt - a.updatedAt
    );
  }

  topState(): ActivityState {
    return this.active()[0]?.state ?? "idle";
  }
}

/// Aggregate pet mood (port of MoodResolver): running activity wins; nothing
/// active reads as idle. `celebrate` is a transient the caller layers on top
/// when entering done.
export function aggregateMood(activities: ActivitySession[]): "working" | "waiting" | "done" | "idle" {
  if (activities.some((s) => s.state === "active")) return "working";
  if (activities.some((s) => s.state === "paused")) return "waiting";
  if (activities.some((s) => s.state === "done")) return "done";
  return "idle";
}

export function basename(p: string): string {
  return p.split(/[\\/]/).filter(Boolean).pop() ?? p;
}
