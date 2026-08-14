# Windows 版生图（ImageGen）架构设计

> 本文件是生图模块专项设计，回答：**现状问题、模型市场调研结论、目录驱动架构、协议族适配、透明精灵图管线、连接配置演进、生图页 UX、实施阶段与验收**。
> 关联文档：`windows-architecture.md`（总纲）、`windows-tts-design.md`（TTS 专项，Provider 模式同构）、`feature-research.md`。
> 状态：**已定稿（2026-08）**——方向：模型目录数据驱动 + 协议族适配器（模板方法）+ 透明后处理策略管线 + 连接列表配置（自填端点为默认形态）。
> **v2 演进见 [`windows-imagegen-v2-design.md`](windows-imagegen-v2-design.md)**：能力自描述（固定尺寸表 / 尺寸形态 / 编辑形态 / 质量）、图生图 UI、自定义模型能力声明、SenseNova 支持。
> **维护**：本文档为已实施架构总纲（运行期真值）；§9 阶段表实施状态已过期（4c/5 均已完工，2026-08-12），当前状态以 `memory.md` 与代码为准，本表不再更新。

---

## 1. 背景与问题

### 1.1 现状（2026-08）

| 项 | 现状 | 问题 |
|---|---|---|
| 生图用途 | 仅「总结图」（今日总结配图），`AiRuntimeGeneration` 调用 | 无生图页面、无历史、无多模型选择 |
| Provider | `IImageProvider` + `OpenAiCompatibleImageProvider`（Infra，单连接） | 只支持 OpenAI 兼容协议单端点；无 Gemini 族；无能力描述；无透明支持 |
| 配置 | `providers.json` `image` 段 = 单个 `ImageGenConfig`（baseUrl/keyRef/model/size） | 单连接；尺寸是像素字符串，无法表达宽高比/档位 |
| 透明 | 无 | 精灵图（本项目核心资产形态）必须有透明背景 |
| 需求 | 生图页（主流生图网站形态：提示词 + 参数面板 + 历史画廊）；多模型可选；用户自配第三方端点 | — |

### 1.2 用户场景判定

- **主要场景**：用户自建/小型第三方 OpenAI 兼容端点（官方与大型中转价格高），端点和模型 id 自填
- **次要场景**：官方端点（OpenAI / xAI / Google），通过预置连接模板提供
- **机制同一**：官方/中转/自建只是「连接数据」的差异，运行时完全同构

### 1.3 模型市场调研结论（2026-08 官方文档 + 社区）

**协议族只有两族**（决定适配器数量）：

| 协议族 | 端点形态 | 覆盖模型 |
|---|---|---|
| OpenAI 兼容 | `POST {baseUrl}/images/generations`（+ `/images/edits`） | gpt-image-1/1.5/2/1-mini、Grok Imagine（xAI 官方文档演示直接用 OpenAI SDK）、Qwen-Image、FLUX.1/2、Kolors（均经硅基流动等 OpenAI 兼容端点） |
| Google | `generateContent` / Interactions API（`generationConfig.imageConfig`） | Nano Banana（gemini-2.5-flash-image）、Nano Banana 2（gemini-3.1-flash-image）、Nano Banana Pro（gemini-3-pro-image-preview） |

不兼容族（本期不做）：Seedream（火山方舟专属协议）、Midjourney（无正式 API）、Ideogram。

**透明背景能力（关键差异）**：

| 模型 | 原生透明 | 说明 |
|---|---|---|
| gpt-image-1 / 1.5 / 1-mini | ✅ | `background:"transparent"` + `output_format:"png"` |
| gpt-image-2 | ❌ | 传 transparent 直接 400；官方建议 opaque + 下游抠图 |
| Grok Imagine | ⚠️ 未证实 | 无官方参数；PNG 输出据社区带 alpha，按无原生处理 |
| Nano Banana 全系 | ❌ | 无 alpha 通道；社区两条后处理路径：绿幕键控（philschmid）、黑白双渲染 difference matting（jidefr） |
| Qwen-Image / FLUX | ❌ | 无 |

**尺寸参数形态**：OpenAI 用像素 size（16 倍数约束）；Gemini 用 `aspectRatio` 枚举 + `imageSize`（1K/2K/4K）；Grok 用 `aspect_ratio` 枚举 + `resolution`（1k/2k）。→ 统一模型必须用「宽高比 + 分辨率档位」，像素换算下沉适配器。

**关键模型参考价（Elo / 单张）**：gpt-image-2（1338，$0.025~0.211）；gpt-image-1.5（1272，$0.009~0.2，唯一原生透明前排）；Nano Banana 2（1261，$0.06~0.07）；grok-imagine-image（$0.02，最快 4.4s）；Qwen-Image（$0.02~0.06，中文强）；FLUX.2（$0.03~0.08）。

---

## 2. 目标与设计原则

1. **目录驱动，模型是数据**：内置模型目录 JSON（能力/价格/归属），新增模型 = 改数据零代码；连接（端点 + key）与模型解耦
2. **协议族适配，两族起步**：OpenAI 兼容族一个适配器覆盖全部主流模型；Gemini 单独一族；新协议族 = 新适配器 + 目录加 family
3. **自填端点为默认形态**：连接配置完全用户自填（family/baseUrl/keyRef/模型白名单）；官方端点只做预置模板
4. **透明是后处理策略，不是 provider 参数**：原生透明直传；其余走「绿幕 prompt 增强 + 本地 HSV 键控」管线（纯本地计算，无网络）
5. **复用既有机制**：`providers.json` + `ApiKeyRef`（Credential Manager）+ `ProviderException` 错误归一化 + `ProvidersFileMigrationSource` 迁移
6. **门面统一入口**：生图页与总结图共用 `ImageGenService`；现有总结图路径（`IImageProvider`）保留至迁移完成，避免牵一发动全身
7. **不破坏现有**：阶段 1-3 全部并行新增；迁移（总结图改道、设置页连接列表）为独立阶段

---

## 3. 总体架构

```
┌──────────────────────────────────────────────────────────────┐
│ 生图页（MVVM，新增）              总结图（现有，阶段 4 改道）      │
│  模型选择器（连接×模型）  参数面板（按能力渲染）  历史画廊          │
└──────────────────────────┬───────────────────────────────────┘
                           ▼
┌──────────────────────────────────────────────────────────────┐
│ ImageGenService（门面，App 层）                                 │
│  连接→适配器解析 → 策略选择 → 请求构建 → 生成 → 透明后处理 → 落盘   │
└───────┬──────────────────────────────┬────────────────────────┘
        ▼                               ▼
┌───────────────┐                ┌───────────────────────────┐
│ 适配器（Infra）│                │ 策略（Core，可单测）        │
│ HttpImageAdapterBase（模板方法）│ ITransparencyStrategy      │
│  ├ OpenAiImageGenAdapter       │  ├ NativeTransparency      │
│  └ GeminiImageGenAdapter       │  └ ChromakeyStrategy（HSV）│
└───────────────┘                └───────────────────────────┘
        ▲
        │ 目录（Core embedded JSON）：ImageModelCatalog
        │ 连接（providers.json image.connections[]）
```

### 设计模式映射

| 模式 | 位置 | 解决 |
|---|---|---|
| 端口-适配器（依赖倒置） | `IImageGenProvider`（Core）← 适配器（Infra） | 协议差异限定 Infra；Core 可单测 |
| 模板方法 | `HttpImageAdapterBase` | 中转三坑（错误归一化/参数降级/b64+url 双解析）收敛基类一次实现，子类只填请求构建与响应解析钩子 |
| 策略 | `ITransparencyStrategy`（两段式）、参数降级 `IImageGenRetryPolicy` | 透明按模型能力选策略；400/422 降参重试开关可配 |
| 注册表 + 工厂 | `ImageGenProviderRegistry`（App/Infra 边界） | family → 适配器实例、HttpClient 复用、连接绑定 |
| 门面 | `ImageGenService` | 生图页/总结图单一入口；任务、管线、落盘编排 |
| 数据驱动目录 | `ImageModelCatalog` + `ImageConnection` | 模型多 = 数据问题；能力元数据驱动 UI 与策略 |

不采用 Builder/过度抽象：C# record + `with` 表达式足够（YAGNI）。

---

## 4. Core 接口设计（`DesktopPet.Core/ImageGen/`，新增）

### 4.1 统一参数模型

```csharp
namespace DesktopPet.Core.ImageGen;

/// <summary>宽高比（统一枚举；像素换算在各适配器内完成）。</summary>
public enum ImageAspectRatio { R1x1, R3x2, R2x3, R4x3, R3x4, R16x9, R9x16, R21x9, Auto }

/// <summary>分辨率档位（OpenAI 适配器换算像素：短边=档位像素，对齐 16 倍数）。</summary>
public enum ImageScale { S1K, S2K, S4K }

/// <summary>质量档位（OpenAI 族有；Google 族忽略）。</summary>
public enum ImageQuality { Auto, Low, Medium, High }

/// <summary>参考图（图生图/编辑）。</summary>
public sealed record ReferenceImage(byte[] Bytes, string MimeType = "image/png");

/// <summary>统一生成请求（跨协议族）。</summary>
public sealed record ImageGenSpec(
    string Prompt,
    ImageAspectRatio AspectRatio = ImageAspectRatio.R1x1,
    ImageScale Scale = ImageScale.S1K,
    ImageQuality Quality = ImageQuality.Auto,
    bool Transparent = false,
    IReadOnlyList<ReferenceImage>? ReferenceImages = null, // 非空 = 编辑模式
    long? Seed = null,
    IReadOnlyDictionary<string, object>? ExtraParams = null); // provider 特有参数透传（不进统一模型）

/// <summary>统一输出。</summary>
public sealed record ImageGenOutput(byte[] Bytes, string MimeType, string? SeedUsed = null);

/// <summary>能力描述（驱动 UI 渲染与策略选择）。</summary>
public sealed record ImageGenCapabilities(
    bool NativeTransparency,
    IReadOnlyList<ImageAspectRatio> AspectRatios,
    IReadOnlyList<ImageScale> Scales,
    bool Editing,
    int MaxReferenceImages,
    bool Seed);
```

### 4.2 目录与连接

```csharp
/// <summary>模型目录条目（内置 JSON 数据，随应用分发）。</summary>
public sealed record ImageModelDescriptor(
    string Id,        // 真实模型 id：gpt-image-2 / grok-imagine-image / gemini-3.1-flash-image ...
    string Family,    // "openai" | "google"
    string Name,      // 显示名
    ImageGenCapabilities Capabilities,
    string? PriceHint = null,
    string? Note = null);

/// <summary>连接配置（providers.json image.connections[]；ApiKeyRef 引用 Credential Manager）。</summary>
public sealed record ImageConnection(
    string Id,
    string Name,
    string Family,              // "openai" | "google"
    string BaseUrl,
    string ApiKeyRef,           // 空 = 无鉴权（本地 ComfyUI 等）
    IReadOnlyList<string> Models); // 模型白名单（真实 id）；空 = family 目录全量

/// <summary>目录查找规则：先精确匹配 Id；未命中按 Family 默认能力 + id 前缀（grok- / qwen / flux）推断。</summary>
public sealed class ImageModelCatalog { /* embedded JSON 加载 + 查询 */ }

/// <summary>协议族端口。</summary>
public interface IImageGenProvider
{
    string Family { get; }
    Task<ImageGenOutput> GenerateAsync(ImageGenSpec spec, CancellationToken ct);
    Task<ImageGenOutput> EditAsync(ImageGenSpec spec, IReadOnlyList<ReferenceImage> references, CancellationToken ct);
}
```

### 4.3 透明策略（两段式，Core + ImageSharp 可单测）

```csharp
/// <summary>透明处理策略。两段式：请求前可能改 prompt，响应后可能做像素级后处理。</summary>
public interface ITransparencyStrategy
{
    bool RequiresPromptEnhancement { get; }
    /// <summary>请求前：包装 chromakey 规范（纯绿 #00FF00 + 白描边 + 无绿色主体等）。</summary>
    string EnhancePrompt(string prompt);
    /// <summary>响应后：HSV 键控 → 边缘清理 → 输出 RGBA PNG。原生透明策略为 no-op。</summary>
    Task<ImageGenOutput> PostProcessAsync(ImageGenOutput output, CancellationToken ct);
}
```

- `NativeTransparencyStrategy`：两段 no-op；适配器直传 `background:"transparent"`
- `ChromakeyStrategy`：`EnhancePrompt` 追加绿幕规范（纯色 `#00FF00`、白描边 2-3px、主体无绿、居中留白、贴纸风格）；`PostProcessAsync` 用 ImageSharp 做 HSV 检测（hue 中心 120°±25、饱和度/亮度阈值）、形态学膨胀清理抗锯齿边缘、输出 RGBA PNG。主体含绿时由调用方提示（返回键控后孔洞检测可选，二期）
- 二期可选：黑白双渲染 difference matting（半透明/阴影更准）、本地 rembg 模型——均新增 Strategy 实现

---

## 5. Infra 适配器设计（`DesktopPet.Infra/Providers/`，新增）

### 5.1 模板方法基类

```csharp
/// <summary>HTTP 生图适配器基类：中转三坑在此兜底，子类只填两个钩子。</summary>
public abstract class HttpImageAdapterBase : IImageGenProvider
{
    protected abstract JsonObject BuildRequestBody(ImageGenSpec spec, IReadOnlyList<ReferenceImage>? refs);
    protected abstract Task<byte[]> ParseImageAsync(JsonDocument response, CancellationToken ct);

    // 基类固定实现：
    // 1. Bearer/UA（对齐 ModelProvider）/ 超时 deadline（默认 300s，可配）
    // 2. ProviderException 错误归一化（auth/timeout/network/rate-limit/server/invalid-response）
    // 3. 参数降级：400/422 且 IRenewableImageGenRetryPolicy 启用时，去掉高风险参数
    //    （background/quality/seed）重试一次；连接配置 strictParams=true 关闭降级
    // 4. b64 优先、url 回退（立即下载，中转 url 有效期短）
}
```

### 5.2 适配器

| 适配器 | family | 请求构建钩子 | 响应解析钩子 |
|---|---|---|---|
| `OpenAiImageGenAdapter` | openai | 像素 size 换算（短边=档位、按 ratio、对齐 16 倍数、总像素约束）；透明直传（能力允许时）；extra_body 透传 ExtraParams | `data[0]` b64 优先 / url 回退 |
| `GeminiImageGenAdapter` | google | `generateContent`，`generationConfig.responseModalities=["IMAGE"]` + `imageConfig.aspectRatio/imageSize`；编辑走多模态 content parts | candidates[].content.parts[].inlineData（base64）；文本候选跳过 |

### 5.3 错误与降级语义

- 复用 `ProviderException`（Core/Scheduling/ModelContracts.cs）：`auth`/`timeout`/`network`/`rate-limit`/`server`/`invalid-response`
- 生图超时默认 300s（实测慢渠道单张 3 分半），门面层再叠 `SummaryImageRetryPolicy` 类策略（失败当天补试，可配）

---

## 6. 配置演进（providers.json）

```jsonc
// 旧（阶段 0）：单连接
"image": { "baseUrl": "...", "apiKeyRef": "...", "modelName": "gpt-image-1", "size": "1024x1024" }

// 新（阶段 4 起）：连接列表
"image": {
  "connections": [
    { "id": "my-relay", "name": "我的端点", "family": "openai",
      "baseUrl": "https://自填/v1", "apiKeyRef": "cred:relay",
      "models": ["gpt-image-2", "grok-imagine-image-quality"] },
    { "id": "google", "name": "Google 官方", "family": "google",
      "baseUrl": "https://generativelanguage.googleapis.com/v1beta",
      "apiKeyRef": "cred:google",
      "models": ["gemini-3.1-flash-image"] }
  ]
}
```

- 迁移：`ProvidersFileMigrationSource` 新增一条（旧单连接 → 连接列表，无损）
- 连接模板（预置 4 个，UI 新建连接可套用）：OpenAI 官方 / xAI 官方 / 硅基流动 / Google 官方；自建端点 = 空模板手填
- 兼容性兜底：模型 id 以连接配置为准；目录匹配不到时按 family 默认能力 + id 前缀推断

---

## 7. 生图页 UX（阶段 4，MVVM）

| 区域 | 内容 | 驱动 |
|---|---|---|
| 模型选择器 | 「连接 · 模型」两级平铺（仅已配置） | 连接列表 × 目录能力 |
| 提示词 | 多行输入 + 字符计数 | — |
| 参数面板 | 宽高比 / 分辨率档位 / 质量（OpenAI 族才显示）/ 张数 / seed 开关 / 透明开关 | `ImageGenCapabilities` 动态渲染 |
| 生成按钮 | 提交任务 → 进度 → 结果 | 门面异步任务 |
| 历史画廊 | 会话内网格；二期本地落盘 `%APPDATA%/DesktopPet/gallery/`（图片文件 + JSON 索引） | 存储契约进 Core（二期） |

---

## 8. 总结图集成（阶段 4b，已实施）

### 8.1 定位

总结图（今日总结配图）是 ImageGen 子域的下游消费者：与生图页共用连接列表、模型目录、适配器与门面，但**参数形态不同**：

| 维度 | 总结图 | 生图页（精灵图场景） |
|---|---|---|
| 尺寸 | 16:9 横版 + 1K 档（配图够用省钱） | 用户自选（宽高比 + 档位） |
| 透明 | **不需要**（不透明，跳过绿幕管线） | 需要（透明精灵图） |
| 模型 | 可配置（默认首连接首模型） | 用户自选 |
| 容错 | 多模型轮换 + 当天补试 | 用户手动重试 |

### 8.2 配置与解析

- `AiSettings.SummaryImageModelRef`：`"{connectionId}/{modelId}"`；空 = 自动（首连接首模型）
- `SummaryImageTargetResolver`（Core 纯逻辑）：引用解析 + 回退规则——空引用 → 首连接首模型；连接匹配但模型不在白名单 → 该连接首模型；引用失效 → 首连接首模型；无有效连接 → null（跳过生图）

### 8.3 多模型容错

`ImageGenService.GenerateWithFallbackAsync(connection, preferredModelId, spec, ct)`：

- 尝试顺序：首选模型 → 同连接其余模型（去重）
- 可换模型错误码：`network` / `server` / `timeout` / `invalid-response`；**`auth` / `rate-limit` 不换**（同凭据换模型无意义）
- 全部失败抛最后一个错误，交给 `SummaryImageRetryPolicy` 当天补试
- 适配器按 (连接, 模型) 缓存：模型 id 在适配器构造时固定，换模型必须换适配器实例（HttpClient 复用）

### 8.4 运行时路径

```
AiCoordinator 总结生成 → ImageGenService.GenerateWithFallbackAsync
  → 连接解析（SummaryImageTargetResolver）→ 适配器 → 写 diary/yyyy-MM-dd.png
失败 → SummaryImageRetryPolicy（当天最多 2 次/30min 间隔）→ 重读文本重试
```

### 8.5 providers.json 迁移（已实施）

- `image` 段：单连接平铺格式（baseUrl/apiKeyRef/modelName/size）→ `connections[]` 列表 + `summaryModelRef`
- 旧格式由 `ImageConnectionsConfigConverter` 读入 Legacy 字段，`Normalize` 迁移为 `connections[0]`（id=`legacy`，family=openai）
- 凭据引用：每连接独立 `provider/image/{connectionId}/api-key`（对齐 `ForModel`）；旧引用由 `ProviderCredentialMigrator` 逐连接迁移
- 旧 `IImageProvider` / `ImageGenConfig` / `OpenAiCompatibleImageProvider` 已删除（功能由新适配器超集覆盖）

---

## 9. 实施阶段与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| 1 ✅ | Core 契约：`ImageGenContracts.cs` + `ImageModelCatalog`（embedded JSON）+ `ChromakeyStrategy`（ImageSharp）+ 测试 | Core.Tests 新增用例全绿；HSV 键控单测（纯色/边缘/含绿主体） |
| 2 ✅ | Infra：`HttpImageAdapterBase` + `OpenAiImageGenAdapter`（含像素换算/参数降级/b64+url）+ 测试 | Infra.Tests 新增用例全绿（MockHttp 端点矩阵） |
| 3 ✅ | `GeminiImageGenAdapter`（x-goog-api-key / generateContent / inlineData）+ 测试 | 同上 |
| 4a ✅ | `ImageGenService` 门面 + 注册表（能力分流 + 绿幕两段式 + 适配器缓存） | 链路测试全绿（原生直传/绿幕/编辑+透明组合） |
| 4b ✅ | providers.json 连接列表迁移 + `SummaryImageModelRef` + 多模型容错 + 总结图改道 + 设置页连接编辑器适配 | 迁移单测（旧格式→连接/序列化无旧字段/凭据迁移）；全量 700+ 测试；build 0 CS error |
| 4c ⏳ | 设置页连接列表编辑器（多连接管理）+ 总结图模型下拉 | UI 验收 |
| 5 ⏳ | 生图页（MVVM）+ 历史画廊 | 阶段 5 验收 |

阶段 1-4b 全部并行新增后，旧 `IImageProvider`/`ImageGenConfig` 单连接路径已删除；剩余 UI 工作不动 Core/Infra 契约。

---

## 10. 明确不做（本期）

- Seedream / Midjourney / Ideogram 等非兼容协议族（新 family 架构已预留）
- 黑白双渲染 matting、本地 rembg 模型（二期策略）
- 生图历史落盘（阶段 5）
- 批量生成队列、webhook 回调（单用户桌面应用无需求）
