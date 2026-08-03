# Code Context

## Files Retrieved
1. `windows/settings.html`（75-167 行）- Pet 页静态 DOM；“选择宠物”和“桌面宠物”各自渲染了一遍同一类素材卡片，实例设置随后依赖当前选中实例。
2. `windows/src/settings.ts`（27-64、248-440 行）- 素材选择会替换当前 `PetInstance` 的 `spriteSlug`；首次启动还会自动创建首个实例。
3. `windows/src/settings.ts`（662-759、815-852、1458-1475 行）- “桌面宠物”区域从库创建实例、渲染实例、切换 `selectedId`；尺寸和漫游控制写入选中实例。
4. `windows/src/pets.ts`（1-167 行）- 持久 `PetInstance`/`PetStore` 模型，以及创建、选择、更新和删除的单一数据源。
5. `windows/src/i18n.ts`（369-378、468-480 行）- 简体中文中已有两组容易混淆的标题和提示语键。
6. `windows/src/settings.ts`（1184-1352 行）- 静态 i18n 与对应 DOM ID 的绑定位置。
7. `windows/src/catalog.ts`（41-79 行）- 为区分数据语义而补充读取：`LibPet` 是已下载/创建的素材定义，保存在 `ap_library`，不含实例状态。

## Key Code

### 数据语义必须保持分离

`LibPet` 是可复用的宠物素材定义，按 `slug` 存于 `ap_library`；它含名称和精灵图 URL，但没有桌面位置、尺寸、漫游、可见性或养成数据：

```ts
export interface LibPet {
  slug: string;
  name: string;
  url: string;
  petJsonUrl?: string;
  custom?: boolean;
}
```

`PetInstance` 才是一个持久的桌宠。它以独立 `id` 存于 `ap_pet_instances`，通过 `spriteSlug` 引用素材库；同一 `slug` 可以创建多个实例，而每个实例拥有独立名称、可见性、尺寸、漫游和养成归属：

```ts
export interface PetInstance {
  id: string;
  name: string;
  spriteSlug: string;
  visible: boolean;
  size: number;
  roamEnabled: boolean;
  roamMode: RoamMode;
  roamSpeed: number;
  reactsToActivity: boolean;
}

export interface PetStore {
  version: 1;
  selectedId: string | null;
  instances: PetInstance[];
}
```

`PetStore.selectedId` 是设置页目前唯一的编辑上下文。`initPetControls()` 将“屏幕上的大小”“漫游”写入该实例，而非素材库（`windows/src/settings.ts:815-852`）。左侧养成 HUD 同样读取 `currentPetInstance()`（`windows/src/settings.ts:36-38、98-164`）。

### 重复与歧义的直接根因

1. HTML 在 `#pet-results`（“选择宠物”）和 `#extra-grid`（“选择一只宠物添加到桌面”）中同时放置素材库卡片（`windows/settings.html:100-133`）。截图中的两行 `shuijun` / `zetzb booster` 正是这两个容器。
2. 两组卡片视觉结构同为 `pet-results > .pet-item`，均为 48px 缩略图；但交互不是一回事：
   - `renderPage()` 中点击 `#pet-results` 调用 `pick(p)`，对已有选中实例执行 `updatePetInstance(..., { spriteSlug })`，即**替换已选桌宠的素材**（`windows/src/settings.ts:253-264、325-361`）。若没有实例才创建一个。
   - `initDesktopPets()` 中点击 `#extra-grid` 调用 `createPetInstance(store, instanceFromLibrary(pet))`，即**新建桌宠实例**（`windows/src/settings.ts:676-694、729-746`）。
3. 运行中的实例又在 `#extra-running` 显示第三次卡片形态；点击它才改变 `selectedId`（`windows/src/settings.ts:697-726`）。因此用户很难推断：上方高亮素材、下方“添加”素材、再下方正在桌面的实例，三者各自是什么对象。
4. 截图还显示布局层面的问题：`#pet-results` 被 `group wide` 占满整行，但“桌面宠物”和“动画”随后进入双列，因此同样的素材卡片在不同宽度、不同纵向位置重复出现。根因是 DOM 分组，不是 CSS 微调能解决的。

## Architecture

### 当前数据流

`Browse/Create` -> `addToLibrary()` -> `LibPet[]` (`ap_library`)。

已有素材可走两条 UI 路径：

- `#pet-results` -> `pick()` -> 更新 `selectedId` 对应实例的 `spriteSlug` -> `persistDesktopPets()` -> `ap_pet_instances`、Tauri `sync_desktop_pet_windows`、`pets-changed`。
- `#extra-grid` -> `instanceFromLibrary()` + `createPetInstance()` -> 同一持久化与同步链路。

`#extra-running` -> `selectPetInstance()` -> 变更编辑上下文；`initPetControls()`、`renderCare()`、左侧预览随后使用它。

所以正确的 IA 不是把两类数据合成一类，而是让每种对象仅在其自己的区域展示一次，并明确两类动作：**添加实例**与**替换某实例素材**。

## 发现

1. **高优先级：信息对象重复但操作语义不同。** “选择宠物”实际上是“为当前选中的桌宠更换素材”，不是选择一个抽象的全局宠物；“桌面宠物”上半部实际仍是素材库，而非桌宠。标题与实现不一致。
2. **高优先级：实例列表被埋在素材选择器之后。** 真正影响养成、尺寸和漫游的 `PetInstance` 位于 `#extra-running`，却要先经过两个同素材网格才可见；用户无法建立“当前正在编辑哪一只”的心智模型。
3. **中优先级：`selectedId` 是隐式上下文。** 素材卡片的高亮仅按 `spriteSlug` 判断（`windows/src/settings.ts:337`），而一只素材可对应多只实例；这会把“素材已被当前实例使用”误读成“当前实例已被选中”。
4. **中优先级：文案重复放大误解。** `t-choose` / `t-extra-pick` 都以“选择宠物”为核心动词；`t-extra` 把同时包含素材选择器和实例列表的区域统称“桌面宠物”。相关中文键在 `windows/src/i18n.ts:369-378`，静态绑定在 `windows/src/settings.ts:1230-1255`。

## 推荐 IA

在 Pet 页采用一个清晰的三层结构，顺序固定如下。

1. **显示**：保留“悬浮球”和“显示桌面宠物”两个开关。可合入一个“显示”组，使截图中的两个相邻开关左右对齐；这是独立于素材/实例的全局显示设置。
2. **桌面宠物（实例）**：作为主区域并置于素材库之前。
   - 标题：`桌面宠物 (n/12)`，右侧保留“全部移除”。
   - 内容：仅 `#extra-running`，每卡代表一个 `PetInstance`；点击卡片选为当前编辑对象，明确选中态，删除仍删除实例。
   - 空状态：`桌面上还没有宠物。请从素材库添加。`
   - “屏幕上的大小”“漫游”“动画”仍仅编辑该选中实例，建议紧跟该区域或保持现位置但显示“正在编辑：{实例名}”。不改变 `selectedId` 机制。
3. **宠物素材库（LibPet）**：素材只渲染一次。
   - 标题：`宠物素材库`，说明“素材可用于创建多个桌宠”。
   - 内容：保留搜索、分页、`#pet-results`、浏览和创建入口；删除素材的现有保护逻辑保持不变。
   - 每张素材卡提供两个显式动作：主动作 `添加到桌面`（创建 `PetInstance`）；当已有选中实例时，次动作 `替换当前桌宠的素材`（调用现有 `pick(p)`）。两者不能再由两套重复网格隐式区分。

这套结构把实体（桌宠实例）置前、资源（可复用素材）置后，且保留“相同素材可创建多个实例”的现有模型。它也解释了为什么尺寸/漫游是实例级，而浏览/创建/移除的是库级。

## 最小 DOM/TS 改动

### `windows/settings.html`

1. 删除或折叠现有 `#extra-grid` 与 `#extra-label` 的“从库添加”子区，只保留一个素材网格。推荐保留 `#pet-results`、`#pet-search-wrap`、`#lib-empty`、`#pet-pager` 和 Browse/Create 按钮作为唯一素材库 DOM。
2. 将 `#extra-desktop-wrap` / `#extra-running` 提升为“桌面宠物”主组，放在素材库之前；移除当前同一 `gcard` 中的 `#extra-grid`、`#extra-empty`、`#extra-cap-msg`。空状态改为实例空状态。
3. 将 `#extra-count` 放入“桌面宠物”标题或该组头部；保留 ID 可避免无关的 TS 改动。
4. 为 `#pet-results` 的卡片追加一个固定 ID 或类，例如 `.library-add` 与 `.library-replace`。若优先极小 diff，可仅在每张卡内加入一个 `+` 图标按钮表示添加，并让卡片主体继续调用 `pick(p)`；图标需有 `title`/aria 标签以避免仅靠视觉猜测。
5. 将素材库组标题的 `t-choose` 改名为 `t-library`；将 `t-extra` 继续用于真正的实例区。删除不再使用的 `t-extra-sub`、`t-extra-pick`、`t-extra-no`、`t-extra-cap` 对应 DOM 后，也同步移除静态绑定。

### `windows/src/settings.ts`

1. 保留 `pick(pet)`（`253-264`）作为“替换当前实例素材”的唯一函数，不改变其持久化路径。
2. 把 `initDesktopPets()` 内 `addCard` 的创建逻辑抽为一个小的 `addInstanceFromLibrary(pet)`，内容仍是 `createPetInstance(currentPetStore(), instanceFromLibrary(pet))` + `persistDesktopPets()` + 刷新实例/养成。随后由素材库卡上的 `+` 调用该函数。这样同一创建逻辑不再要求 `#extra-grid`。
3. `renderDesktopPets()`（`729-747`）只渲染 `#extra-running` 和容量/空状态，不再遍历 library 渲染 `#extra-grid`。容量状态应同时使素材库“添加”按钮禁用，替换动作仍可用。
4. `renderPage()`（`325-378`）是唯一的 `LibPet` 卡片渲染位置：保留当前主体点击 `pick(p)`；新增嵌套添加按钮，`stopPropagation()` 后调用上述创建函数。卡片选中样式建议改为 `p.slug === currentPetInstance()?.spriteSlug` 的“当前素材”标识，而不是实例选中态。
5. `instanceCard()`（`697-726`）保留作为唯一 `PetInstance` 卡片渲染，并继续调用 `selectPetInstance()`。可为卡片追加“当前编辑中”标签，但不是数据模型改动。
6. 保留 `initPetControls()`、`renderCare()`、`persistDesktopPets()` 与 `pets.ts` 的所有调用接口。`pets.ts` 无需改动。

### `windows/src/i18n.ts` 与 `applyStatic()`

新增并绑定以下键，避免复用“选择宠物”：

- `Pet library` -> `宠物素材库`
- `Add to desktop` -> `添加到桌面`
- `Replace current pet` -> `替换当前桌宠`
- `No desktop pets yet. Add one from your library.` -> `桌面上还没有宠物。请从素材库添加。`
- `This library item can be used to create multiple desktop pets.` -> `一个素材可用于创建多只桌面宠物。`

在 `applyStatic()` 中以新 ID 绑定以上键；移除 DOM 后，删除对 `t-extra-sub`、`t-extra-pick`、`t-extra-no`、`t-extra-cap` 的 `set()` 调用。保留 `t-extra-running`、`t-extra-closeall`，或按新 DOM 名称一致地重命名。

## 受影响 ID/函数

### DOM IDs

- **保留且重排**：`show-desktop-pets`、`pet-search-wrap`、`pet-search`、`lib-empty`、`pet-results`、`pet-pager`、`pg-prev`、`pg-next`、`pg-ind`、`open-browse`、`open-create`、`extra-desktop-wrap`、`extra-count`、`extra-close-all`、`extra-running`。
- **建议删除**：`extra-grid`、`extra-empty`、`extra-cap-msg`、`t-extra-sub`、`t-extra-pick`、`t-extra-no`、`t-extra-cap`。
- **建议新增**：`t-library`、实例空状态 ID（例如 `desktop-empty`）；素材卡动作可用 class 而不必引入固定 ID，例如 `.library-add` / `.library-replace`。
- **文案 ID 的语义调整**：`t-choose` 可改为 `t-library`；`t-extra` 应只标题化实例区；`t-extra-running` 可改为当前编辑/实例列表的小标题，或在一个区域内移除其冗余二级标题。

### TypeScript 函数与类型

- **保留、复用**：`pick`、`renderPage`、`initDesktopPets`、`instanceFromLibrary`、`persistDesktopPets`、`currentPetInstance`、`initPetControls`、`renderCare`。
- **建议新增小函数**：`addInstanceFromLibrary(pet: LibPet)`，抽取当前 `addCard(...).onclick` 的 4 行创建与刷新流程。
- **不应改动的数据契约**：`PetInstance`、`PetStore`、`createPetInstance`、`updatePetInstance`、`selectPetInstance`、`removePetInstance`（`windows/src/pets.ts`）。

## Start Here

先打开 `windows/settings.html:100-133`。这里可直接删除造成视觉重复的第二份素材网格，并把实例列表提升为页面主对象；随后在 `windows/src/settings.ts:325-378` 和 `662-759` 将两条现有动作收敛到同一个素材库卡片。该路线无需改动 `pets.ts` 的持久化模型，也不涉及 Rust 或 CSS 审查。