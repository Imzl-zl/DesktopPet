# Windows 生图架构 v2：能力自描述 + 图生图（增量设计）

> 生图模块设计分两卷，职责不同，**改动前先确认看哪卷**：
>
> | 卷 | 内容 | 何时看 |
> |---|---|---|
> | [`windows-imagegen-design.md`](windows-imagegen-design.md)（v1） | **已实施架构总纲**：协议族适配器、门面、透明策略、连接配置、总结图集成 | 理解运行期实现、改代码 |
> | **本文档（v2）** | **增量设计与状态跟踪**：能力自描述、图生图、SenseNova 支持；只记「v1 之外新增什么、为什么」 | v2 实施、新增模型、扩展能力 |
>
> **维护约定（防止双文档漂移）**：
> 1. v2 实施完成后，实现细节以代码为准，v2 只保留设计决策（why），不追更实现细节（how）——v1 与代码是运行期真值。
> 2. **模型数据唯一真值 = `DesktopPet.Core/Resources/image-models.json`**；本文档 §5 矩阵与 v1 §1.3 调研表都是快照。新增/修改模型：改 JSON → 同步 §5 矩阵（否则 reviewer 对照文档误判）。
> 3. v2 阶段表（§6）每完成一阶段，把 ⏳ 改为 ✅ 并写日期。
> 4. 新增能力字段：必须同步修改 `ImageGenCapabilities`（Core）、目录 JSON 解析、连接编辑器能力表单、本文档 §2 能力表——四处一致。

---

## 1. v2 范围（相对 v1 的增量）

**目标：UI 与模型彻底解耦**——UI 只认统一能力模型，不认模型 id；用户自配连接/模型，自己了解渠道能力。

| 增量 | 说明 |
|---|---|
| 固定尺寸表 | SenseNova 只认 11 个固定 2K 尺寸，无法用 v1 的「比例+档位」换算表达 → 能力新增 `FixedSizes` |
| 尺寸参数形态 | 像素 size（v1）/ 固定查表 / `aspect_ratio+resolution`（Grok 官方形态，v1 对其发 size 与文档不符）→ 能力新增 `SizeStyle` |
| 编辑请求形态 | `image` 数组（v1）/ `images` 数组（gpt-image-2 官方新形态）/ 单对象（Grok，v1 已有）→ 能力新增 `EditStyle` |
| 质量参数开关 | SenseNova 无 quality → 能力新增 `QualityLevels`，false 时隐藏 UI 下拉且不发参数 |
| 比例枚举 | 补 `R5x4`/`R4x5`（Gemini 官方与 SenseNova 都有，v1 枚举缺） |
| 图生图 UI | Windows 生图页新增参考图区（v1 只有门面 `EditAsync`，无 UI 入口） |
| 自定义模型能力声明 | 连接编辑器可声明自定义模型能力（v1 未知模型只能粗推断） |
| **渠道模板（v2 修订）** | 用户显式选厂家/渠道（OpenAI 官方/xAI/SenseNova/newapi 中转等），渠道行为进数据；Auto 推断降级为未知模型兑底 |

**不新增协议族、不新增适配器、不新增设计模式**：SenseNova 归 openai 族（OpenAI 兼容协议 + 尺寸表数据）；Gemini 保持 generateContent 主路径（官方旧 API 与全部主流中转同形态）。

## 2. 能力模型（Core 契约）

```csharp
public enum ImageAspectRatio { R1x1, R3x2, R2x3, R4x3, R3x4, R5x4, R4x5, R16x9, R9x16, R21x9, R9x21, Auto } // 新增 R5x4/R4x5/R9x21（SenseNova 11 尺寸含 9:21）

public enum ImageSizeStyle
{
    PixelCalc,                // 默认：宽高比+档位 → 像素 size（v1 算法，16 倍数 ≤3840 ≤8.29M）
    FixedTable,               // 固定尺寸表：按比例查表发 size（SenseNova）
    AspectRatioResolution,    // aspect_ratio + resolution 枚举（Grok）
}

public enum ImageEditStyle
{
    Auto,               // 按 id 推断兜底：gpt-image-2* → ImagesArray；grok-* → SingleObject；其余 ImageArray
    ImageArray,         // image: [{type:"image_url", image_url:{url}}]（gpt-image-1.x / 中转主流）
    ImagesArray,        // images: [{image_url}]（gpt-image-2 官方新形态，无 type）
    SingleObject,       // image: {url, type:"image_url"}（Grok）
    MultipartFormData,  // multipart/form-data（newapi 类中转必须，2026-08-13 实测）
}

// v1 字段不变，新增 4 个：
public sealed record ImageGenCapabilities(
    bool NativeTransparency,
    IReadOnlyList<ImageAspectRatio> AspectRatios,
    IReadOnlyList<ImageScale> Scales,
    bool Editing,
    int MaxReferenceImages,
    bool Seed,
    bool QualityLevels = true,                  // 新增
    IReadOnlyList<string>? FixedSizes = null,   // 新增：非空 ⇒ SizeStyle=FixedTable
    ImageSizeStyle SizeStyle = ImageSizeStyle.PixelCalc,  // 新增
    ImageEditStyle EditStyle = ImageEditStyle.Auto);      // 新增
```

**尺寸表推导**（`ImageModelCatalog.Resolve` / 用户能力解析执行）：
`FixedSizes` 非空 → `SizeStyle=FixedTable`；`AspectRatios` 自动由表内尺寸去重比例推导；`Scales` 取表内档位（SenseNova 全 2K → `[S2K]`）。用户显式声明可覆盖推断。

**能力来源（四级优先级，v2 修订：用户显式选择优先于推断）**：

```
模型级声明（providers.json modelCapabilities） > 渠道模板（connection.Channel，channels.json） > 目录/推断
```

- **渠道模板**（`Core/Resources/channels.json`，新增渠道 = 改数据零代码）：用户选厂家/渠道后，该渠道全部模型默认按渠道行为（如 newapi 中转编辑=multipart、xAI 尺寸=ratio+resolution）；选模板自动填协议族与官方地址
- 内置渠道：openai-official / google-official / sensenova-official / xai-official / siliconflow / newapi-relay（OpenAI 兼容中转）/ custom
- **Auto 推断只对未知模型兜底**（id 前缀：grok-→单对象、gpt-image-2→images 数组）；已知模型行为全部进目录 JSON 的 `editStyle` 显式声明
- 实测教训（2026-08-13）：同一模型 id 在不同渠道行为不同（newapi 上 gpt-image-2 编辑必须 multipart、gemini 模型要 JSON image 数组）→ 推断必然猜错，所以渠道必须用户显式选择

### 目录 JSON 扩展

```jsonc
{
  "id": "sensenova-u1-fast",
  "family": "openai",
  "name": "SenseNova U1 Fast（商汤）",
  "capabilities": {
    "nativeTransparency": false,
    "fixedSizes": ["2752x1536","1536x2752","2048x2048","2496x1664","1664x2496",
                   "2368x1760","1760x2368","2272x1824","1824x2272","3072x1376","1344x3136"],
    "editing": false, "maxReferenceImages": 0, "seed": false, "quality": false
  },
  "priceHint": "2K 固定档",
  "note": "信息图/海报专精；仅 11 种固定 2K 尺寸；公开 API 无图生图"
}
```

### providers.json 自定义模型（实施偏差 2026-08-12：models 保持 string[]，能力声明独立顶层字典）

```jsonc
"connections": [
  { "id": "relay", "name": "我的端点", "family": "openai",
    "baseUrl": "https://x/v1", "apiKeyRef": "cred:x",
    "models": ["sensenova-u1-fast", "my-relay-model"] }
],
// 自定义模型能力声明（键=模型 id，跨连接共享；不在任何连接白名单的键 Normalize 时剔除）
"modelCapabilities": {
  "my-relay-model": {
    "fixedSizes": ["2048x2048", "1024x1024"],
    "editing": true, "maxReferenceImages": 2,
    "editStyle": "imageArray", "quality": false, "seed": true
  }
}
```

- 原设计（models 数组 string/object 混合）实施时改为独立字典：`ImageConnection.Models` 类型零改动、converter 只加一个可选顶层字段，旧文件天然兼容
- 覆盖语义：仅覆盖声明维度，未声明继承目录/推断；`fixedSizes` 声明 ⇒ 比例/档位重新推导 + `FixedTable`；优先级 用户声明 > 目录 > 推断

## 3. 适配器改动（两族不变）

- `OpenAiImageGenAdapter`：构造注入 `ImageGenCapabilities`（`ImageGenService.CreateAdapter` 传入 Resolve 结果）；
  - 尺寸三形态按 `SizeStyle` 构造 body（FixedTable 精确比例查表，缺项取第一项兜底；PixelCalc 沿用 v1 算法）
  - 编辑三形态按 `EditStyle` 构造 `image`/`images` 字段
  - `EndpointPath` 判定改为显式 `references` 标志（不能再 `body.ContainsKey("image")`——ImagesArray 键名是 `images`）
  - 参数降级 / b64+url 双解析 / 错误归一化：**不动**
- `GeminiImageGenAdapter`：仅 `AspectRatioName` 补 `5:4`/`4:5`；其余不动。Interactions API 记二期（同族可选端点风格，不新增适配器）
- `ImageGenService`：`EditAsync` 链路 v1 已有，UI 接上即可；无其他改动

## 4. UI 改动

**原则：面板固定，能力裁剪渲染，零模型特判。**

- **生图页**（`ImageGenWindow`）：
  - 参考图区（`Editing=true` 才显示）：文件多选 / URL 添加 / chip 列表 / 清空；上限 = `MaxReferenceImages`；有图 → 走 `EditAsync`
  - 尺寸控件：有 `FixedSizes` → 单个「尺寸」下拉（如 "2752×1536（16:9）"）；无 → v1 的「宽高比 + 分辨率」双下拉
  - 质量下拉：`QualityLevels=false` → 隐藏；seed 行：`Seed=false` → 隐藏（v1 已有按能力渲染，新增字段沿用同机制）
- **设置页连接编辑器**：顶部新增「渠道模板」下拉（选模板自动填协议族/地址，行为随渠道生效）；模型白名单下方「模型能力声明（可选，JSON）」文本框（模型 id → 能力字典，Consolas 等宽；解析失败阻止保存并提示）；保存时全局声明合并 + 白名单过滤，删除连接时失效键自动剔除

## 5. 支持矩阵（快照；真值 = image-models.json，新增模型必须同步本表）

| 模型 | 族 | 尺寸 | 图生图 | 透明 | 质量 |
|---|---|---|---|---|---|
| gpt-image-2 | openai | 1K/2K/4K 换算 | ✓ 16（images 数组） | ✗ 绿幕 | ✓ |
| gpt-image-1.5 / 1-mini | openai | 1K | ✓ 1（image 数组） | ✓ 原生 | ✓ |
| grok-imagine-image / -quality | openai | 1k/2k（ratio+resolution） | ✓ 3（单对象） | ✗ | 2.0 有 |
| Qwen/Qwen-Image | openai | 1K | ✗ | ✗ | ✗ |
| Qwen/Qwen-Image-Edit | openai | 1K | ✓ 3（image 数组） | ✗ | ✗ |
| black-forest-labs/FLUX.1-* | openai | 1K | ✗ | ✗ | ✗ |
| Kwai-Kolors/Kolors | openai | 1K | ✗ | ✗ | ✗ |
| **sensenova-u1-fast** | openai | **11 种固定 2K** | ✗（公开 API 无） | ✗ | ✗ |
| gpt-image-2（newapi 类中转） | openai | 1K/2K/4K 换算 | ✓（**必须声明 editStyle=multipartFormData**，渠道模板 newapi-relay 已默认） | ✗ 绿幕 | ✓ |
| gemini-2.5-flash-image | google | 1K/2K + ratio | ✓ 4 | ✗ 绿幕 | ✗ |
| gemini-3.1-flash-image | google | 1K/2K/4K + ratio | ✓ 14 | ✗ 绿幕 | ✗ |
| gemini-3-pro-image-preview | google | 1K/2K/4K + ratio | ✓ 14 | ✗ 绿幕 | ✗ |
| 任意自定义模型 | 自选 | 自选 | 自声明 | 自声明 | 自声明 |

**明确不做**：Seedream（火山专属协议）、Midjourney（无 API）、Interactions API（二期）、mask 抠图 UI（ExtraParams 透传兜底）、批量队列/webhook、**流式生图（`stream:true`+`partial_images`——OpenAI 官方支持，但 Gemini/SenseNova/多数中转不支持或无用，桌宠场景等完整图即可；2026-08 调研结论，需要时在 OpenAI 族适配器加 SSE 解析钩子）**。

## 6. 实施阶段与验收（状态跟踪）

| 阶段 | 内容 | 验收 | 状态 |
|---|---|---|---|
| A ✅ 2026-08-12 | Core：枚举 R5x4/R4x5/R9x21、`FixedSizes`/`SizeStyle`/`EditStyle`/`QualityLevels`、目录 JSON 新字段 + Resolve 尺寸表推导（最近邻比例/长边档位） | Core.Tests：尺寸表推导、枚举解析、旧 JSON 兼容（486 全绿） | ✅ 2026-08-12 |
| B ✅ 2026-08-12 | Infra：适配器注入能力、尺寸三形态、编辑三形态（含 gpt-image-2 images 数组键名）、EndpointPath 判定修正 | Infra.Tests MockHttp 矩阵（SenseNova 查表/比例映射、gpt-image-2 images 数组、Grok ratio+resolution、质量开关；166 全绿） | ✅ 2026-08-12 |
| C ✅ 2026-08-12 | 目录数据：sensenova-u1-fast 条目（11 固定 2K）+ grok 条目标注 aspectRatioResolution | 目录单测 14 模型全绿（含内置断言） | ✅ 2026-08-12 |
| D ✅ 2026-08-12 | 生图页：参考图区（文件多选/URL 下载/chip/上限 MaxReferenceImages，有图走 EditAsync）+ 尺寸表模式下拉（尺寸表 ⇒ 单下拉隐藏比例/档位，比例由选中尺寸推导）+ 质量开关隐藏 + AiCoordinator.EditImageAsync | App 构建 0 错误；全量 754 测试全绿；真实 UI 冒烟留 phase6 | ✅ 2026-08-12 |
| E ✅ 2026-08-12 | 连接编辑器：模型能力声明（JSON 字典文本框）+ providers.json modelCapabilities 读写/白名单过滤/删除清理；CustomImageCapabilities 合并覆盖（目录/推断基底） | Core converter round-trip 3 测试 + Resolve 合并 3 测试 + 门面链路 2 测试（754 全绿） | ✅ 2026-08-12 |
| F ✅ 2026-08-13（部分） | 真实端点验收：sensenova-u1-fast 文生图 ✓（47s/url 回退）；gpt-image-2 文生图 ✓（50s/b64）+ 编辑需 multipart（已补 MultipartFormData 形态并实测 78s ✓）；gemini-3.1-flash-image 走 OpenAI 兼容 images/generations ✓（文生图 40s + 编辑 14s，连接用 openai family）；agnes-image-2.1-flash 文生图 ✓（17s/url，偶发 524 超时）；agnes 编辑渠道后端不稳（400）；剩余 UI 冒烟待跑 | 764 测试全绿；真实端点 curl 实测（key 不入库） | ✅ 2026-08-13 |

依赖：B 依赖 A；D/E 互不依赖可并行；C 随时可做。

## 7. macOS 备注

macOS 生图面板已有参考图入口，但走私有约定（`extra_body.image` + `response_format`），与 OpenAI 官方 JSON 形态不一致。v2 不迁移 macOS（无诉求、改动面大）；若未来对齐，参考本文档统一能力模型与三形态编辑。
