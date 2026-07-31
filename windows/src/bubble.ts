// Simple speech bubble (port of the macOS ChatBubble): a single capsule line
// driven by `renderLine`.

export function invalidateBubbleConfig(): void {
  // No cached config anymore (appearance lives in CSS variables via
  // `applyBubble`); kept as a no-op so Settings can call it unconditionally.
}

export class BubbleRenderer {
  /// Set by the app when a re-render is desired (e.g. bubble needs repaint).
  dirty = false;

  constructor(private root: HTMLElement) {}

  /// Single plain line (idle / done / celebrate), the mac ChatBubble.
  /// Re-entrant: repeated calls with the same text are no-ops (no DOM churn,
  /// no flicker), only an actual text change cross-fades.
  renderLine(text: string) {
    let line = this.root.querySelector<HTMLElement>(".single-line");
    if (!line) {
      this.clear(); // leaving rows mode, rebuild as a single capsule line
      line = document.createElement("div");
      line.className = "single-line";
      line.textContent = text;
      this.root.appendChild(line);
      this.root.classList.add("capsule");
      this.root.hidden = false;
      return;
    }
    this.root.hidden = false;
    if (line.textContent !== text) {
      // Cross-fade the text swap (mac contentTransition(.opacity)).
      line.classList.add("fade");
      line.textContent = text;
      requestAnimationFrame(() => line!.classList.remove("fade"));
    }
  }

  hide() {
    this.clear();
    this.root.hidden = true;
  }

  private clear() {
    this.root.classList.remove("capsule");
    this.root.textContent = "";
  }
}
