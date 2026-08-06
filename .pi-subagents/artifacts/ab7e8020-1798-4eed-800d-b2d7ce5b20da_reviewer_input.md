# Task for reviewer

你是资深 WPF/Win32 桌面应用审查专家。审查 C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App 下的渲染与窗口模块，只读不改。

文件清单：
- Rendering/SpriteLoader.cs, Rendering/SpriteFrameBitmapSourceCache.cs, Rendering/PixelBuffer.cs, Rendering/ReusablePixelBuffer.cs, Rendering/PlaceholderPet.cs
- Windows/PetWindow.cs, Windows/FloatingBallWindow.cs, Windows/DanmakuWindow.cs, Windows/BubbleView.cs, Windows/XamlIslandHost.cs, Windows/PetWindowManager.cs, Windows/SpritePreviewWindow.cs, Windows/WelcomeWindow.cs
- Interop/NativeMethods.cs（已确认基本合理，可跳过）
- src/DesktopPet.Infra/Hotkey/HotkeyManager.cs（在 windows-native/src 下）
- src/DesktopPet.Core/Rendering/OverlayRenderer.cs, PetRenderer.cs, SpriteSheet.cs（纯逻辑，可快速扫）

重点找：
1. Win32 API / WPF 官方 API 的误用（如 DispatcherTimer 使用不当、WriteableBitmap 写像素方式错误、HWND 生命周期、WndProc 泄漏、SetWindowPos/WS_EX_TRANSPARENT 用法、XAML Islands 用法）
2. 硬编码的用户可配置值（尺寸、时间、颜色、频率、路径写死在源码里且没有设置项对接）
3. 资源泄漏（timer 未释放、事件未退订、位图缓存无上限、句柄泄漏）
4. 线程问题（UI 线程阻塞、跨线程访问、Dispatcher.Invoke 死锁风险）
5. 设计缺陷（重复代码、职责混乱、状态不同步）

输出格式：按严重程度分组列出发现（Critical / Important / Minor），每条含 文件:行号、问题描述、为何是问题、修复建议。没有发现的类别不要硬凑。最后给 1-2 行总体结论。

## Acceptance Contract
Acceptance level: attested
Completion is not accepted from prose alone. End with a structured acceptance report.

Criteria:
- criterion-1: Return concrete findings with file paths and severity when applicable

Required evidence: review-findings, residual-risks

Finish with a fenced JSON block tagged `acceptance-report` in this shape:
Use empty arrays when no items apply; array fields contain strings unless object entries are shown.
`criteriaSatisfied[].status` must be exactly one of: satisfied, not-satisfied, not-applicable.
`commandsRun[].result` must be exactly one of: passed, failed, not-run.
`manualNotes` and `notes` are optional strings; an empty string means no note and does not satisfy `manual-notes` evidence.
```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "specific proof"
    }
  ],
  "changedFiles": [
    "src/file.ts"
  ],
  "testsAddedOrUpdated": [
    "test/file.test.ts"
  ],
  "commandsRun": [
    {
      "command": "command",
      "result": "passed",
      "summary": "short result"
    }
  ],
  "validationOutput": [
    "validation output or concise summary"
  ],
  "residualRisks": [
    "none"
  ],
  "noStagedFiles": true,
  "diffSummary": "short description of the diff",
  "reviewFindings": [
    "blocker: file.ts:12 - issue found, or no blockers"
  ],
  "manualNotes": "anything else the parent should know"
}
```