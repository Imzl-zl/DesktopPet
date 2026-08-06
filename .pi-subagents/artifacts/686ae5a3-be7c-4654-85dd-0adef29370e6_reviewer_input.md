# Task for reviewer

你是资深 .NET 架构审查专家。审查 C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra 与 DesktopPet.Core 的服务类，只读不改。

文件清单：
- src/DesktopPet.Infra/Providers/OpenAiCompatibleModelProvider.cs, OpenAiCompatibleImageProvider.cs, ProviderConfig.cs
- src/DesktopPet.Infra/Tts/EdgeTts.cs, SapiTtsProvider.cs
- src/DesktopPet.Core/Scheduling/ModelRequestScheduler.cs
- src/DesktopPet.Core/Ai/ChatPipeline.cs
- src/DesktopPet.Core/Memory/ConversationMemory.cs, MemoryStore.cs
- src/DesktopPet.Core/Interaction/InteractionEngine.cs, QuickBubbleController.cs
- src/DesktopPet.Core/Care/CareEngine.cs, IntimacyEngine.cs
- src/DesktopPet.Core/Personas/PersonaEngine.cs, BuiltinPersonas.cs, PersonasFileModel.cs
- src/DesktopPet.Core/Storage/TauriMigration.cs
- src/DesktopPet.Core/Pets/Pause.cs, PetAnimationSettings.cs, StateMapping.cs, PetStoreModel.cs

重点找：
1. HttpClient 使用问题（未复用、超时处理、重试指数退避实现错误）
2. 硬编码的用户可配置值（默认值是否与设置项对接：ModelRequestScheduler 并发数、超时、token 上限、温度、间隔等；设置文件中有的字段是否真被消费）
3. 并发/竞态（SemaphoreSlim 使用、取消令牌、共享状态）
4. 错误处理问题（吞异常、误导性降级）
5. 设计缺陷（接口抽象泄漏、依赖倒置破坏、重复逻辑）
6. 数据一致性（读-改-写竞态、持久化时机）

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