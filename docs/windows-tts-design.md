# Windows 版 TTS 架构设计（语音朗读）

> 本文件是语音朗读（TTS）专项设计，回答：**现状问题、Edge TTS 为何不可用（实证）、Provider 栈设计、接口/配置演进、设置页 UX、降级与错误处理、实施阶段与验收**。
> 关联文档：`windows-architecture.md`（总纲，§3.2 为 TTS 契约）、`feature-research.md`（语音输出采纳记录）。
> 状态：**已定稿（2026-08）**——方向：SAPI 兜底 → OneCore 系统自然语音 → OpenAI 兼容端点（用户自配）三级 Provider 栈；Edge TTS 免费端点直连明确不做。

---

## 1. 背景与问题

### 1.1 现状（2026-08 实测）

| 项 | 现状 | 问题 |
|---|---|---|
| 生产实现 | `SapiTtsProvider`（System.Speech，SAPI 5 离线合成），`AiCoordinator` 硬编码 | 用户机器只有 2 个老式语音（`Microsoft Huihui Desktop` zh-CN / `Microsoft Zira Desktop` en-US，V110 旧引擎），音质机械感强 |
| 音色选择 | 设置页「朗读声音」下拉枚举 SAPI 语音（2026-08-09 已从硬编码 Edge 名改为动态枚举） | 选项只有系统装的老式语音；无试听；无语速/音量 |
| 文案 | 设置页「语音朗读：对话模式朗读回复，**Edge TTS**」 | **与实际实现（SAPI）不符**，误导用户 |
| 代码残留 | `EdgeTts.cs`（404 行完整实现 + 11 个测试，含自研 RFC 6455 客户端、Sec-MS-GEC 签名） | 生产零引用；注释"已验证可用"与实测矛盾 |
| OneCore | 未使用 `Windows.Media.SpeechSynthesis` | Win11「自然语音」（Neural，系统设置可免费安装）**SAPI 5 枚举不到**，只能走 OneCore——用户装了也选不到好音色 |

### 1.2 Edge TTS 免费端点为何不可用（实证 + 社区调研）

2026-08 本机实测：`EdgeTtsProvider` 连 `speech.platform.bing.com` 返回 **"Bad request"**。社区（rany2/edge-tts，3k+ stars）一年内演进证实这是**持续打地鼠**，不是一次性问题：

| 时间 | 事件 |
|---|---|
| 2024-10 | 微软加 `Sec-MS-GEC` 参数，**只封大陆 IP**（境外正常）；算法被破解，edge-tts 6.1.16 修复 |
| 2024-11 | 大陆 IP 间歇性 403，仅 Clash TUN 等代理模式可用——**纯 IP 地域风控** |
| 2025 年中 | 升级为 **TLS ClientHello 指纹（JA3/JA4）检测**；社区测试矩阵：只有 Python aiohttp 的特定 OpenSSL 指纹通过，Node/Bun/curl/裸 ssl 全 403 |
| 2025-12-01 | 微软大规模改协议（10 分钟音频上限、4096 字节 chunk、认证 401），edge-tts 一周连发 4 版才稳住 |
| 2026-01 至今 | 7.2.7/7.2.8 恢复；社区已确认新端点 `api.msedgeservices.com` 出现，随时可能再变 |

**根因结论**：本项目 `EdgeTts.cs` 的 Sec-MS-GEC 算法与 edge-tts 一致（正确），但 TLS 走 `SslStream`（SChannel）——SChannel 的 ClientHello 指纹恰是被拒的一类；叠加大陆 IP 地域风控，双重拦截。
**工程判断**：未公开端点 + 随时变更 + 地域风控 = 桌面应用**不可依赖**。即使复刻 aiohttp 指纹通过，下次微软改动即失效。**Edge 直连明确不做**。

---

## 2. 目标与设计原则

1. **音质可升级**：默认零配置可用（SAPI 兜底），系统有自然语音自动可用（OneCore），想要更好的可自配端点（在线）
2. **复用既有 Provider 模式**：`providers.json` + `ApiKeyRef`（Credential Manager）+ OpenAI 兼容协议——与模型/生图完全同构，用户心智一致
3. **AI 总开关语义不变**：TTS 属 AI 陪伴功能，在线 Provider 仅在 AI 总开关开启时可用；本地 Provider（SAPI/OneCore）零网络
4. **失败不打扰对话**：合成失败静默降级（按 Provider 栈向下），对话继续；在线端点错误分类提示（对齐 `ProviderException`）
5. **契约下沉 Core**：修复现状漂移——`ITtsProvider` 目前在 Infra 定义，对齐 `IModelProvider`/`IImageProvider`（Core/Scheduling/ModelContracts.cs）下沉 Core

---

## 3. 总体架构：三级 Provider 栈

```
┌────────────────────────────────────────────────────────────┐
│ 设置页「语音朗读」卡片                                          │
│  引擎选择（单选）：系统语音(SAPI) │ 自然语音(OneCore) │ 自配端点 │
│  音色下拉（来自所选引擎 ListVoices）+ 试听 + 语速滑条             │
└──────────────────────────┬─────────────────────────────────┘
                           ▼
┌────────────────────────────────────────────────────────────┐
│ 运行时（AiCoordinator.Speak）                                │
│  按 TtsProviderId 选引擎 → SynthesizeAsync → 失败按栈降级      │
└──────────────────────────┬─────────────────────────────────┘
                           ▼
┌──────────────┬──────────────┬──────────────────────────────┐
│ SapiTtsProvider │ OneCoreTtsProvider │ OpenAiCompatibleTtsProvider │
│ (Infra, 现有)   │ (App, 新增)         │ (Infra, 新增)              │
│ 离线 WAV        │ 离线 WAV/WMA        │ 在线 MP3/WAV（端点决定）    │
│ 音色=系统 SAPI  │ 音色=系统 OneCore    │ 音色=/v1/audio/voices      │
└──────────────┴──────────────┴──────────────────────────────┘
```

| 引擎 | 层 | 网络 | 音质 | 配置要求 | 适用 |
|---|---|---|---|---|---|
| SAPI（默认） | Infra（现有） | 无 | 老式 V110，机械感 | 零配置 | 兜底，任何机器可用 |
| OneCore | App（新增） | 无 | 系统装的自然语音即 Neural，明显好于 SAPI | 零配置；Win10 1803+ | 主推默认体验；用户可在系统设置装「自然语音」提升音质 |
| OpenAI 兼容端点 | Infra（新增） | 有 | 取决于端点（SiliconFlow CosyVoice2 / Fish Audio / GPT-SoVITS 自托管…） | providers.json `tts` 段 + API Key | 追求音质/克隆音色的用户 |

**层级选择规则**：设置页单选引擎（默认 SAPI，避免行为突变）；运行时合成失败按「当前引擎 → 默认 SAPI」降级一次，日志记录，不打断对话。

---

## 4. 接口设计（Core）

### 4.1 ITtsProvider 契约（下沉 Core，对齐 IModelProvider）

```csharp
// DesktopPet.Core/Tts/TtsContracts.cs（新增；现 Infra/Tts/EdgeTts.cs 中的旧契约迁移至此）

/// <summary>音色信息（引擎无关的展示模型）。</summary>
public sealed record TtsVoiceInfo(
    string Id,        // 引擎内唯一标识：SAPI=VoiceInfo.Name / OneCore=Voice.Id / 端点=voices[].id
    string DisplayName,
    string Language,  // 如 zh-CN（空 = 未知）
    string Gender = ""); // male | female | ""（未知）

/// <summary>合成请求（引擎无关）。</summary>
public sealed record TtsSynthesisRequest(
    string Text,
    string VoiceId,       // 空 = 引擎默认（按界面语言自动）
    double SpeedPercent = 100); // 50-200，各引擎内部换算（SAPI Rate / SSML prosody / 端点 speed）

/// <summary>TTS Provider 契约。实现：SAPI（默认）/ OneCore / OpenAI 兼容端点。</summary>
public interface ITtsProvider
{
    string Id { get; }                       // "sapi" | "onecore" | "openai"
    bool RequiresNetwork { get; }            // 在线端点=true；AI 总开关关闭时禁用

    /// <summary>枚举可用音色（设置页下拉 + 试听用；空 = 端点不支持列表时返回空）。</summary>
    Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct);

    /// <summary>合成语音，返回音频流（SAPI/OneCore=WAV；端点=端点决定，多为 MP3）。</summary>
    Task<Stream> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct);
}
```

- **API Key 语义**：`ListVoicesAsync` 需要鉴权（在线端点）时失败抛出 `ProviderException(auth)`，设置页内联提示，不弹框
- **错误分级**：复用 `ProviderException`（Core/Scheduling/ModelContracts.cs）：`auth` / `timeout` / `network` / `invalid-response`；本地 Provider 不抛（系统无语音时 `ListVoices` 返回空、`Synthesize` 回退默认）

### 4.2 引擎选择与音色解析

```csharp
// DesktopPet.Core/Tts/TtsProviderRegistry.cs（新增，纯逻辑可单测）
// 输入：AiSettings（TtsProviderId / TtsVoiceName / TtsSpeedPercent）+ 界面语言
// 输出：选中的 ITtsProvider + 解析后的 TtsSynthesisRequest
public static class TtsProviderRegistry
{
    public static ITtsProvider Resolve(ITtsProvider[] available, string providerId, string fallbackId = "sapi");
    // 音色解析：VoiceId 空 → 按界面语言查该引擎默认（SAPI/OneCore 用语言回退；端点用 voices 列表语言匹配，无匹配取第一个）
}
```

- **音色回退语义**（与现 SAPI 行为一致）：设置页保存的 VoiceId 在当前引擎中不存在（如引擎切换后）→ 回退「自动（跟随界面语言）」，不报错
- **旧数据兼容**：现存 `AiSettings.TtsVoiceName` 里可能是旧 Edge 名（`zh-CN-XiaoxiaoNeural`）或 SAPI 名——解析失败一律回落「自动」，无需迁移脚本

---

## 5. 配置设计

### 5.1 AiSettings 扩展（DesktopPet.Core/Storage/AiSettings.cs）

| 字段 | 类型 | 默认 | 语义 |
|---|---|---|---|
| `TtsEnabled` | bool | false | 语音朗读总开关（现有，不变） |
| `TtsProviderId` | string | `"sapi"` | 引擎：`sapi` \| `onecore` \| `openai`（未知值归一化回 `sapi`） |
| `TtsVoiceName` | string | `""` | 当前引擎下的音色标识；空 = 自动（跟随界面语言）。**语义从"Edge 声音名"改为"引擎音色标识"** |
| `TtsSpeedPercent` | int | 100 | 语速 50-200（clamp），各引擎换算：SAPI `Rate`、OneCore SSML `prosody rate`、端点 `speed` 字段 |

- `AiSettings.DefaultVoiceFor(lang)`（现返回 Edge 名）**删除**——「自动」语义改为引擎内解析（Registry 按语言匹配），不再有 Edge 名残留
- 归一化/反序列化照现有 converter 模式扩展（缺字段填默认；未知 `TtsProviderId` 归一化）

### 5.2 providers.json 新增 `tts` 段（对齐 `image` 段）

```jsonc
// %APPDATA%/DesktopPet/providers.json（与 models[] / image 并列）
{
  "tts": {
    "baseUrl": "https://api.siliconflow.cn/v1",   // 或 fishaudio.org/v1 / 本地 GPT-SoVITS 端点
    "apiKeyRef": "tts-siliconflow",               // Credential Manager 引用，不落明文
    "modelName": "FunAudioLLM/CosyVoice2-0.5B",   // 端点要求的模型 id
    "voice": ""                                    // 默认音色 id；空 = 引擎自动/列表首个
  }
}
```

```csharp
// DesktopPet.Core/Scheduling/ModelContracts.cs（对齐 ImageGenConfig）
public sealed record TtsEndpointConfig(
    string BaseUrl,      // 如 https://api.siliconflow.cn/v1（/v1/audio/speech）
    string ApiKeyRef,    // 凭据引用 id（空 = 无鉴权，如本地 GPT-SoVITS）
    string ModelName,    // 如 FunAudioLLM/CosyVoice2-0.5B / tts-1 / gpt-sovits
    string Voice = "");  // 默认音色 id；空 = 自动

// ProvidersFileModel 增加：public TtsEndpointConfig? Tts { get; set; }
// Normalize：BaseUrl/ModelName 空白 → null（未配置）；InspectForMigration lossless 判定同步更新
```

**AI 总开关交互**：`Ai.Enabled=false` 时在线端点引擎不可选（设置页置灰 + 说明"需开启 AI 功能"）；本地引擎（SAPI/OneCore）不受网络约束但仍属 AI 陪伴功能，随 `Ai.Enabled` 整体启停（与现有 TtsEnabled 层级一致，不改语义）。

---

## 6. 实现层设计

### 6.1 SapiTtsProvider（Infra，改造）

- 现状：`SynthesizeAsync(text, TtsVoice, ct)` 已支持按名精确选中 + 语言回退 + 系统默认
- 改造：适配新契约 `TtsSynthesisRequest`；新增 `ListVoicesAsync`（复用现有 `GetInstalledVoices()`）；`SpeedPercent` → `synth.Rate`（-10..10 映射：100%→0，50%→-10，200%→+10 线性）
- 保留 `GetInstalledVoices()` 静态方法供设置页（或改走实例方法，静态保留兼容）

### 6.2 OneCoreTtsProvider（App 层新增，`DesktopPet.App/Tts/`）

- 依赖 WinRT `Windows.Media.SpeechSynthesis`，需 TFM `net8.0-windows10.0.19041.0`（App 已是）→ **放 App 层**（与 `MediaPlayer` 播放同层，不进 Infra 避免多目标）
- `ListVoicesAsync`：`SpeechSynthesizer.AllVoices` → 过滤 `Enabled`，映射 `TtsVoiceInfo(Id=Voice.Id, DisplayName, Language, Gender)`
- `SynthesizeAsync`：`SynthesizeTextToStreamAsync(text)` → `SpeechSynthesisStream` 拷贝为 `MemoryStream`；语速经 SSML `prosody rate`（`SynthesizeSsmlToStreamAsync`，文本需 XML 转义）；语音选择经 `Voice` 属性（按 Id 精确）→ 失败回退 `SpeechSynthesizer.DefaultVoice`
- **注意**：OneCore 语音流格式为 WAV；`MediaPlayer` 直接可播

### 6.3 OpenAiCompatibleTtsProvider（Infra 新增，`Infra/Providers/`）

- 复用 `ProviderHttpClient.Create()`（现有单例、连接复用、PooledConnectionLifetime 5min）
- 请求：`POST {BaseUrl}/audio/speech`，body `{ model, input, voice, response_format, speed }`，`Authorization: Bearer {key}`（经 `ICredentialStore` 取 ApiKeyRef）
- `response_format`：默认 `mp3`（MediaPlayer 可播）；端点报错（400 含"BadRquestData"等）→ 分类为 `ProviderException(invalid-response)`，消息含端点原文供排查
- `ListVoicesAsync`：优先 `GET {BaseUrl}/audio/voices`（SiliconFlow/Fish/Neiroha 均支持）→ `voices[].id`；404/不支持 → 返回空（设置页音色下拉显示"手动输入"文本框 + 取 `Tts.Voice`）
- 超时：30s 整体 deadline（对齐 EdgeTts 兜底模式）；重试：不做（TTS 一次失败降级 SAPI 更合理，避免重复扣费）

### 6.4 AiCoordinator.Speak 改造

```csharp
// 现状：_tts 字段硬编码 new SapiTtsProvider() → 改为构造时注入 IReadOnlyList<ITtsProvider>
// 运行时选择（每次 Speak 或设置变更时）：
var provider = TtsProviderRegistry.Resolve(available, _settings.Ai.TtsProviderId);
try { stream = await provider.SynthesizeAsync(req, ct); }
catch (ProviderException) { stream = await fallbackSapi.SynthesizeAsync(req, ct); /* DebugLog 记录 */ }
// 播放链路不变：临时文件 → MediaPlayer（已支持 WAV/MP3）
```

- 设置变更时 `ApplySettings` 重建 provider 选择（引擎切换即时生效，不需要重启）
- `TtsSessionEnabled` 会话内开关逻辑不变

---

## 7. 设置页 UX（AI 助手 → 「语音朗读」卡片）

```
┌─ 语音朗读 ─────────────────────────────────────────────┐
│ [开关] 对话模式朗读回复；弹幕模式不朗读                     │
│ 引擎：[系统语音] [自然语音] [自配端点]   ← 单选（在线需 AI 开启）│
│ 朗读声音：[下拉：当前引擎音色]  [试听]                    │
│ 语速：[滑条 50%─200%]                                   │
│ ── 仅自配端点 ──                                       │
│ 连接卡片：BaseUrl / 模型 / API Key / [测试连接] [获取音色] │
│ 说明：自配端点支持 OpenAI 兼容 TTS（SiliconFlow / Fish    │
│   Audio / GPT-SoVITS…）；Key 存系统凭据，不落明文          │
└────────────────────────────────────────────────────────┘
```

- **试听**：固定文案（本地化："嗨，我是你的桌面宠物~"）调当前引擎 `SynthesizeAsync` → 临时文件 → `MediaPlayer` 播放（复用 Speak 播放路径）；失败内联提示
- **音色下拉**：`ListVoicesAsync` 结果；空列表（端点不支持）→ 显示文本框手动填 VoiceId
- **测试连接**（在线）：`ListVoicesAsync` 成功即通过；失败按 `ProviderException.Code` 显示超时/401/URL 错误分类（复用模型连接卡片既有交互）
- **文案修正**：删除所有 "Edge TTS" 字样（含 ToggleRow 描述），改为"系统离线语音 / 自配在线端点"；`EdgeTts.cs` 注释改"已知不可用（TLS 指纹 + 地域风控，2026-08 实测）"

---

## 8. 错误处理与可观测性

| 场景 | 行为 |
|---|---|
| 本地引擎无音色（SAPI/OneCore 空列表） | 设置页下拉仅「自动」；合成走系统默认语音，不报错 |
| 在线端点鉴权失败（401） | 试听/合成时静默降级 SAPI + DebugLog；设置页测试连接显示内联错误条 |
| 在线端点超时/网络错误 | 降级 SAPI；对话继续；日志记录端点 + 错误码（不记录 Key） |
| 在线端点 400（voice/model 无效） | 降级 SAPI + DebugLog 含端点原文；提示用户检查音色/模型配置 |
| 播放失败（文件损坏等） | 现状逻辑不变（MediaEnded 清理 + 异常日志） |
| AI 总开关关闭 | 在线引擎置灰不可选；TTS 整体不触发（现状语义） |

- 日志：`AiCoordinator` DebugLog 增加 `tts: provider=sapi|onecore|openai voice=... ms=... fallback=...`；不记录文本内容与 Key
- 可观测性对齐 §8 总纲：合成延迟（试听）可纳入设置页「关于」自采样？——**不做**（TTS 低频，YAGNI）

---

## 9. 明确不做（本期）

1. **Edge TTS 免费端点直连**——未公开端点 + TLS 指纹 + 大陆 IP 地域风控，打地鼠不可维护（§1.2 实证）。`EdgeTts.cs` 保留代码与测试但注释更新，不接生产
2. **TTS 结果缓存**——桌宠短句几乎不重复，收益低
3. **流式合成**（边合成边播）——对话文本短，一次合成 <1s 足够；接口不设 Streaming
4. **音量/音调设置**——语速已覆盖主要诉求；音量用户可调系统/播放器；后续需要再加（契约 `SpeedPercent` 留了扩展位）
5. **每引擎独立音色记忆**（字典）——引擎切换后音色回落「自动」即可，YAGNI
6. **Agent 进程内 TTS**——朗读只在 PetApp 对话路径，Agent 无 TTS 需求
7. **macOS 版同步改造**——本设计仅 Windows（macOS 无 TTS 功能，仅音效，另文档）

---

## 10. 实施阶段与验收

| 阶段 | 内容 | 验证 |
|---|---|---|
| P0 文案/契约对齐（低风险） | ① 设置页/注释 "Edge TTS" 文案修正 ② `ITtsProvider` 下沉 Core 并适配新契约（SAPI 同步改造）③ 删除 `DefaultVoiceFor` ④ 架构文档 §3.2 同步 | 全量单测 + x64 build 0 warn/error |
| P1 OneCore + 试听 + 语速 | ① `OneCoreTtsProvider`（App）② 设置页引擎单选/音色下拉/试听/语速 ③ `TtsProviderRegistry` 单测 | 真机：装「自然语音」前后枚举差异；试听播放；语速生效 |
| P2 在线端点 | ① `TtsEndpointConfig` + providers.json `tts` 段 + 迁移判定 ② `OpenAiCompatibleTtsProvider`（mock HttpClient 单测：请求体/错误分类）③ 设置页连接卡片 + 降级链路 | 真机：SiliconFlow 免费端点全链路；断 Key/断网降级；AI 总开关置灰 |
| P3 收尾 | memory.md/工具文档更新；验收矩阵记录 | 全量回归 + 真实机器 smoke（对齐 child 5 矩阵） |

**验收矩阵（真实机器）**：SAPI 精确选中/语言回退/合成 WAV；OneCore 枚举（装自然语音前后）、合成、语速；在线端点（SiliconFlow）连接/音色列表/试听/合成；401/断网/400 三类降级；AI 总开关关闭后在线引擎置灰且无网络请求；旧数据（Edge 名）回落「自动」。

---

## 附录 A：调研纪要（2026-08）

- Edge 免费端点风控时间线：见 §1.2 表（来源：rany2/edge-tts issues #290/#293/#295/#401/#440/#442/#447/#458 + voipi#7 指纹测试矩阵 + edge-tts-universal README）
- OpenAI 兼容 TTS 端点生态（国内可用）：
  - **SiliconFlow** `api.siliconflow.cn/v1/audio/speech`——MOSS-TTSD / CosyVoice2-0.5B 免费开源模型，支持情感/笑声标注，`/v1/audio/voices` 列表，国内直连
  - **Fish Audio** `fishaudio.org/v1/audio/speech`——高质量克隆音色，免费额度，`/v1/audio/voices`
  - **GPT-SoVITS 自托管**——本地 `:9880/v1/audio/speech`，OpenAI 兼容，零成本，支持音色克隆
  - OpenAI/Azure 官方——标准端点，付费
- 本机实测数据（2026-08）：SAPI 仅 2 音色（Huihui/Zira，V110）；OneCore 仅 3 音色（Huihui/Yaoyao/Kangkang，V110）——均无 Neural 自然语音；Edge 端点 "Bad request"
