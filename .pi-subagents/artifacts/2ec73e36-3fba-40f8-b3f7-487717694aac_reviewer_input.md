# Task for reviewer

你是资深 Windows 桌面应用审查专家（Windows.Graphics.Capture / WinRT / .NET 进程架构）。审查 C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Agent 与 DesktopPet.AgentHost 及相关 Core 文件，只读不改。

文件清单：
- src/DesktopPet.Agent/Capture/GraphicsCaptureSource.cs, Capture/IScreenCaptureSource.cs, Capture/SwitchableScreenCaptureSource.cs
- src/DesktopPet.Agent/Analysis/AnalysisEngine.cs, Analysis/CapturedFrameExtensions.cs
- src/DesktopPet.Agent/AgentService.cs
- src/DesktopPet.AgentHost/Program.cs
- src/DesktopPet.Core/Ai/FrameHasher.cs, ScreenContextFormatter.cs, ScreenEvent.cs, AgentConfig.cs, AgentConfigBuilder.cs, ModelContracts.cs
- src/DesktopPet.Infra/PipeRpc/PipeRpc.cs

重点找：
1. Windows.Graphics.Capture API 误用（GraphicsCaptureSession 生命周期、Direct3D11CaptureFramePool 未 Disposal、复帧泄漏、Recreate 处理、设备丢失处理）
2. WinRT/.NET 互操作问题（CreateDirect3D11DeviceFromDXGIDevice、Windows SDK 包用法、资源释放）
3. 双进程设计问题（IPC 协议、心跳缺失、Agent 崩溃恢复、配置下发竞态）
4. 硬编码值（截屏间隔、分辨率、JPEG 质量、帧率写死且没有配置对接）
5. 内存/性能问题（每帧分配、Bitmap 转换浪费、事件风暴）

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