import type { FloatingBallDragOutcome, PhysicalPoint, Point } from "./floating-ball-drag";

export type FloatingBallPointerDrag = {
  begin(pointerId: number, point: Point, grabOffset: PhysicalPoint, startedAt: number): boolean;
  move(pointerId: number, point: Point): boolean;
  finish(pointerId: number, point: Point, allowClick: boolean, now: number): FloatingBallDragOutcome | null;
};

export type FloatingBallPointerOptions = {
  drag: FloatingBallPointerDrag;
  isMenuOpen(): boolean;
  hideMenu(): void;
  showMenu(): void;
  now(): number;
  scale(): number;
  reportError(error: unknown): void;
};

function gesturePoint(e: PointerEvent): Point {
  return { x: e.screenX, y: e.screenY };
}

function physicalGrabOffset(e: PointerEvent, scale: number): PhysicalPoint {
  return { x: e.clientX * scale, y: e.clientY * scale };
}

export function attachFloatingBallPointerDrag(ball: HTMLElement, options: FloatingBallPointerOptions): void {
  let activePointerId: number | null = null;

  function finish(e: PointerEvent, allowClick: boolean): void {
    if (activePointerId !== e.pointerId) return;
    const outcome = options.drag.finish(e.pointerId, gesturePoint(e), allowClick, options.now());
    activePointerId = null;
    ball.classList.remove("pressed", "dragging");
    if (ball.hasPointerCapture(e.pointerId)) ball.releasePointerCapture(e.pointerId);
    if (!outcome) return;
    if (outcome.kind === "click") {
      options.showMenu();
      return;
    }
    if (outcome.completion) void outcome.completion.catch(options.reportError);
  }

  ball.addEventListener("pointerdown", (e) => {
    if (e.button !== 0) return;
    if (options.isMenuOpen()) {
      options.hideMenu();
      return;
    }
    if (activePointerId !== null) return;

    e.preventDefault();
    if (!options.drag.begin(e.pointerId, gesturePoint(e), physicalGrabOffset(e, options.scale()), options.now())) return;
    activePointerId = e.pointerId;
    ball.setPointerCapture(e.pointerId);
    ball.classList.add("pressed");
  });

  ball.addEventListener("pointermove", (e) => {
    if (activePointerId !== e.pointerId) return;
    if (options.drag.move(e.pointerId, gesturePoint(e))) {
      ball.classList.remove("pressed");
      ball.classList.add("dragging");
    }
  });

  ball.addEventListener("pointerup", (e) => finish(e, true));
  ball.addEventListener("pointercancel", (e) => finish(e, false));
  ball.addEventListener("lostpointercapture", (e) => finish(e, false));
}
