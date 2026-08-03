import type { PhysicalPoint, Point, WindowDragOutcome } from "./window-drag";

export type PetPointerDrag = {
  begin(pointerId: number, point: Point, grabOffset: PhysicalPoint, startedAt: number): boolean;
  move(pointerId: number, point: Point): boolean;
  finish(pointerId: number, point: Point, allowClick: boolean, now: number): WindowDragOutcome | null;
};

export type PetPointerOptions = {
  drag: PetPointerDrag;
  canBegin(event: PointerEvent): boolean;
  onBegin(): void;
  finishCapture(): Promise<void>;
  onClick(): void;
  now(): number;
  scale(): number;
  reportError(error: unknown): void;
};

function gesturePoint(event: PointerEvent): Point {
  return { x: event.screenX, y: event.screenY };
}

function physicalGrabOffset(event: PointerEvent, scale: number): PhysicalPoint {
  return { x: event.clientX * scale, y: event.clientY * scale };
}

export function attachPetPointerDrag(element: HTMLElement, options: PetPointerOptions): void {
  let activePointerId: number | null = null;

  function finish(event: PointerEvent, allowClick: boolean): void {
    if (activePointerId !== event.pointerId) return;
    const outcome = options.drag.finish(event.pointerId, gesturePoint(event), allowClick, options.now());
    activePointerId = null;
    if (element.hasPointerCapture(event.pointerId)) element.releasePointerCapture(event.pointerId);
    if (!outcome) return;
    if (outcome.kind === "click") {
      void options.finishCapture().catch(options.reportError);
      options.onClick();
      return;
    }
    if (outcome.kind === "cancel") {
      void options.finishCapture().catch(options.reportError);
      return;
    }
    if (outcome.completion) void outcome.completion.catch(options.reportError);
  }

  element.addEventListener("pointerdown", (event) => {
    if (event.button !== 0 || activePointerId !== null || !options.canBegin(event)) return;

    if (!options.drag.begin(
      event.pointerId,
      gesturePoint(event),
      physicalGrabOffset(event, options.scale()),
      options.now(),
    )) return;
    options.onBegin();
    event.preventDefault();
    activePointerId = event.pointerId;
    element.setPointerCapture(event.pointerId);
  });

  element.addEventListener("pointermove", (event) => {
    if (activePointerId !== event.pointerId) return;
    options.drag.move(event.pointerId, gesturePoint(event));
  });

  element.addEventListener("pointerup", (event) => finish(event, true));
  element.addEventListener("pointercancel", (event) => finish(event, false));
  element.addEventListener("lostpointercapture", (event) => finish(event, false));
}
