## Critical
- 无。

## Important
- [windows/src/floating-ball-drag.ts:109](C:/sudy/github/DesktopPet/windows/src/floating-ball-drag.ts:109) 的 rejected 分支只清理 `moveTask`，没有重新抛出或记录错误。若拖动期间某次 `cursorPosition()` / `setPosition()` 失败、之后 `pointerup` 的最终移动成功，则先前失败被该 rejection handler 视为已处理，`finish()` 会继续持久化，错误不会传到 [windows/src/floating-ball.ts:203](C:/sudy/github/DesktopPet/windows/src/floating-ball.ts:203) 的显式 `console.error`。这违反“无隐性错误吞没”合同。
- [windows/src/floating-ball.test.ts:9](C:/sudy/github/DesktopPet/windows/src/floating-ball.test.ts:9) 至 [windows/src/floating-ball.test.ts:28](C:/sudy/github/DesktopPet/windows/src/floating-ball.test.ts:28) 仍是源码字符串断言，并未执行 `pointerdown/move/up/cancel/lostpointercapture` 生命周期。控制器单测覆盖了最终 `setPosition` 后持久化的正常竞态，但不覆盖 DOM 到控制器的桥接、`setPointerCapture`/`releasePointerCapture`、`lostpointercapture` 的幂等收尾，或上述异步失败传播。因此不能作为原生 mouseup 丢失修复的充分自动化证据。

## Minor
- [windows/src/floating-ball.ts:186](C:/sudy/github/DesktopPet/windows/src/floating-ball.ts:186) 的 `physicalGrabOffset` 依赖“无边框窗口 client 原点等于 outer 原点”及 `devicePixelRatio` 等于按下显示器的有效缩放比例。实现本身在该假设下保持了 `cursorPosition` 与 `PhysicalPosition` 的物理坐标一致，但现有测试没有验证该假设。仍需在 Windows 原生 Tauri 中手工验证 100%/125%/150% 混合 DPI、跨屏拖动、在边界释放，以及 `pointercancel`/窗口失焦时不再跟随并持久化最终位置。