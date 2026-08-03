export type Point = {
  x: number;
  y: number;
};

export type PhysicalPoint = Point;

export type WindowDragHost = {
  cursorPosition(): Promise<PhysicalPoint>;
  setPosition(position: PhysicalPoint): Promise<void>;
  startDrag(): void | Promise<void>;
  finishDrag(): Promise<void>;
};

export type WindowDragOutcome = {
  kind: "click" | "drag" | "cancel";
  completion: Promise<void> | null;
};

type ActiveDrag = {
  pointerId: number;
  startPoint: Point;
  latestPoint: Point;
  grabOffset: PhysicalPoint;
  startedAt: number;
  isDragging: boolean;
  startTask: Promise<boolean>;
  needsMove: boolean;
  moveTask: Promise<void> | null;
  firstMoveError: unknown | null;
  isFinishing: boolean;
};

export class WindowDragController {
  private active: ActiveDrag | null = null;

  constructor(
    private readonly host: WindowDragHost,
    private readonly thresholdPx = 4,
    private readonly clickMaxMs = 280,
  ) {}

  get isDragging(): boolean {
    return this.active?.isDragging ?? false;
  }

  begin(pointerId: number, point: Point, grabOffset: PhysicalPoint, startedAt: number): boolean {
    if (this.active) return false;
    this.active = {
      pointerId,
      startPoint: point,
      latestPoint: point,
      grabOffset,
      startedAt,
      isDragging: false,
      startTask: Promise.resolve(true),
      needsMove: false,
      moveTask: null,
      firstMoveError: null,
      isFinishing: false,
    };
    return true;
  }

  move(pointerId: number, point: Point): boolean {
    const drag = this.active;
    if (!drag || drag.pointerId !== pointerId || drag.isFinishing) return false;
    drag.latestPoint = point;
    if (!drag.isDragging && this.movedBeyondThreshold(drag)) {
      this.start(drag);
      return true;
    }
    if (drag.isDragging) this.queueMove(drag);
    return false;
  }

  finish(
    pointerId: number,
    point: Point,
    allowClick: boolean,
    now: number,
  ): WindowDragOutcome | null {
    const drag = this.active;
    if (!drag || drag.pointerId !== pointerId || drag.isFinishing) return null;

    drag.isFinishing = true;
    drag.latestPoint = point;
    if (!drag.isDragging && this.movedBeyondThreshold(drag)) this.start(drag);
    if (!drag.isDragging) {
      this.active = null;
      return {
        kind: allowClick && now - drag.startedAt <= this.clickMaxMs ? "click" : "cancel",
        completion: null,
      };
    }

    this.queueMove(drag);
    const completion = this.finishAfterFinalMove(drag).finally(() => {
      if (this.active === drag) this.active = null;
    });
    return { kind: "drag", completion };
  }

  private movedBeyondThreshold(drag: ActiveDrag): boolean {
    return Math.abs(drag.latestPoint.x - drag.startPoint.x) > this.thresholdPx
      || Math.abs(drag.latestPoint.y - drag.startPoint.y) > this.thresholdPx;
  }

  private start(drag: ActiveDrag): void {
    drag.isDragging = true;
    try {
      drag.startTask = Promise.resolve(this.host.startDrag()).then(
        () => true,
        (error) => {
          if (drag.firstMoveError === null) drag.firstMoveError = error;
          return false;
        },
      );
    } catch (error) {
      if (drag.firstMoveError === null) drag.firstMoveError = error;
      drag.startTask = Promise.resolve(false);
    }
    this.queueMove(drag);
  }

  private queueMove(drag: ActiveDrag): void {
    drag.needsMove = true;
    if (drag.moveTask) return;

    const task = this.moveToLatestCursorPosition(drag);
    drag.moveTask = task;
    void task.finally(() => {
      if (drag.moveTask === task) drag.moveTask = null;
    });
  }

  private async moveToLatestCursorPosition(drag: ActiveDrag): Promise<void> {
    if (!await drag.startTask) return;
    while (drag.needsMove) {
      drag.needsMove = false;
      try {
        const cursor = await this.host.cursorPosition();
        await this.host.setPosition({
          x: cursor.x - drag.grabOffset.x,
          y: cursor.y - drag.grabOffset.y,
        });
      } catch (error) {
        if (drag.firstMoveError === null) drag.firstMoveError = error;
      }
    }
  }

  private async finishAfterFinalMove(drag: ActiveDrag): Promise<void> {
    while (drag.moveTask) {
      const task = drag.moveTask;
      await task;
      if (drag.moveTask === task) break;
    }
    await this.host.finishDrag();
    if (drag.firstMoveError !== null) throw drag.firstMoveError;
  }
}
