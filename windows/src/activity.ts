export const PET_CHAT: Record<string, string[]> = {
  working: [
    "Thinking…", "Working on it…", "On it!", "Crunching code…",
    "Hmm, let me see…", "Cooking something up…", "Deep in thought…",
    "Brain go brrr…", "Almost there…", "Wiring it up…",
  ],
  waiting: ["Waiting for your input", "Your turn — over to you", "Needs your input"],
  done: [
    "All done!", "Finished!", "Ta-da!", "Done and dusted!",
    "Nailed it!", "That's a wrap!", "Mission complete!",
  ],
  celebrate: ["Woohoo!", "We did it!", "Victory!", "Yesss!", "High five!", "Champion!"],
};

export const IDLE_BOOST = [
  "Let's grill some bugs.",
  "I miss you. Open a branch for me.",
  "Tiny commit, tiny dopamine.",
  "The build is quiet. Too quiet.",
  "Ship something small. Future you is watching.",
  "Your TODOs are pretending not to see us.",
  "Turn coffee into code. Carefully.",
  "Open one file. Intimidate it professionally.",
  "The repo is calm. Suspicious, but calm.",
  "Refactor lightly. Leave with dignity.",
  "One clean diff can fix the whole afternoon.",
];

/// Default editable lines per mood (port of BubbleMessages.defaultLines).
export function defaultLines(mood: string): string[] {
  switch (mood) {
    case "waiting": return PET_CHAT.waiting;
    case "done": return PET_CHAT.done;
    case "celebrate": return PET_CHAT.celebrate;
    case "idle": return IDLE_BOOST;
    default: return []; // working: blank = live activity wins
  }
}

/// Effective custom/system lines for a mood (port of BubbleMessages).
/// Keys: ap_msg_src, ap_msg_all_<mood>.
export function bubbleLines(kind: string | null, mood: string): string[] {
  if ((localStorage.getItem("ap_msg_src") || "system") === "system") return defaultLines(mood);
  const read = (key: string): string[] | null => {
    const raw = localStorage.getItem(key);
    if (!raw) return null;
    const lines = raw.split("\n").map((s) => s.trim()).filter(Boolean);
    return lines.length ? lines : null;
  };
  if (kind) { const v = read(`ap_msg_${kind}_${mood}`); if (v) return v; }
  const all = read(`ap_msg_all_${mood}`);
  return all ?? defaultLines(mood);
}

/// A stable line seeded by session id (djb2), like the macOS app.
export function bubbleLine(kind: string | null, mood: string, seed: string): string {
  const pool = bubbleLines(kind, mood);
  if (!pool.length) return "";
  let h = 5381;
  for (const c of seed) h = (Math.imul(h, 33) + c.charCodeAt(0)) | 0;
  return pool[Math.abs(h) % pool.length];
}
