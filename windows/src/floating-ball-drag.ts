import {
  WindowDragController,
  type PhysicalPoint,
  type Point,
  type WindowDragHost,
  type WindowDragOutcome,
} from "./window-drag";

export type { PhysicalPoint, Point };

export type FloatingBallDragHost = Omit<WindowDragHost, "startDrag" | "finishDrag"> & {
  persistPosition(): Promise<void>;
};

export type FloatingBallDragOutcome = WindowDragOutcome;

export class FloatingBallDragController extends WindowDragController {
  constructor(host: FloatingBallDragHost, thresholdPx = 4, clickMaxMs = 280) {
    super({
      cursorPosition: () => host.cursorPosition(),
      setPosition: (position) => host.setPosition(position),
      startDrag: () => {},
      finishDrag: () => host.persistPosition(),
    }, thresholdPx, clickMaxMs);
  }
}
