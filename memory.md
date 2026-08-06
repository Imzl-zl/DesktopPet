# DesktopPet 项目状态

> 本文件是当前状态快照 + 最近活跃窗口，允许覆盖更新。
> 完整历史归档见 `memory/archive/`，稳定规律见 `tools.md`。

## 当前基线
- macOS 主版（SwiftPM）：`swift build` / `swift test` 通过；打包 `./scripts/build-app.sh release`。
- Windows 版（.NET 8 + WPF）：`dotnet build DesktopPet.sln`（x64）通过。
- Windows 上 Swift core 验证：`./scripts/verify-core-windows.sh`（54 个 core 测试）。
- 最后更新：2026-08-06

## 已完成能力
- macOS 桌宠完整功能：养成（XP/五阶段/成就）、精灵导入切片、气泡、休息提醒、4 语言本地化、菜单栏应用 + Sparkle 更新。
- Windows .NET 8 + WPF 迁移全部完工，Tauri 旧版（`windows/`）已删除。
- Windows 版：宠物/漫游（stay/wander/cursor/climb 四策略）、气泡多行自适应、初始化引导（称呼+人格 onboarding）、分层会话记忆（L1 工作区 + L2 滚动摘要）、输出模式"仅聊天"（停 Agent、不后台截屏烧 token）、漫游设置中文化。
- Agent 屏幕分析评论为空问题已修复（推理模型思考吞 token 处理）。

## 进行中 / 未完成
- Roadmap 未启动项：v0.2 桌面感知（宠物感知当前应用）+ 本地/云端多模态对话；v0.3 每日总结 + 总结图。
- Windows 版待定决策（见 `docs/windows-architecture.md` §10）：Provider 默认实现范围、记忆画像字段清单、亲密度/XP 联动曲线、主动互动触发阈值、自动更新方案（Velopack vs 自建）。

## 关键决策（仍有效）
- Tauri → .NET 8 + WPF 整体迁移（透明窗口软渲染是卡顿根因，原生渲染是唯一合理选择）。
- 双进程架构：PetApp.exe + PetAgent.exe（截屏/分析/总结），Agent 崩溃看门狗自动重启。
- 模型/TTS/生图统一 OpenAI 兼容协议，Provider 可插拔；API Key 存 Windows Credential Manager，不落明文。
- 领域层（Core）零 UI 零 IO、可单测；宠物窗口自绘渲染器豁免 MVVM，设置/对话走 MVVM。
- AI 总开关：关闭即纯桌宠模式，无截屏/网络/后台进程。

## 仍需注意的坑点
- Windows 上 `swift test` 崩溃（SwiftPM llbuild bug #6605），core 测试必须走 `verify-core-windows.sh`。
- `windows-native/测试.txt` 含密钥，勿读取/提交。
- 模型请求限流：SemaphoreSlim(3) 并发闸 + 优先级队列，防云端 RPM/TPM 与本地 Ollama 雪崩。

## 最近活跃窗口
- 2026-08-05：删除 Tauri 旧版，.NET 8 + WPF 迁移全部完工。
- 2026-08-05：分层会话记忆（L1 工作区 + L2 滚动摘要）+ 输出模式"仅聊天"。
- 2026-08-05：初始化引导（称呼 + 人格选择）；漫游设置中文化。
- 2026-08-05：Agent 屏幕分析评论为空修复；气泡自适应多行显示。

> 初始内容参考了最近 8 条 git 提交（均为 2026-08-05 windows-native 工作），用于恢复近期项目状态。
