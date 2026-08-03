# Windows UI 布局只读审计报告

## 审计范围与结论

已只读检查截图 `C:\Users\zhanglu\AppData\Local\Temp\pi-clipboard-aefc5785-dd44-439f-8242-f04f26e2dfc1.png`（2058 x 1398）、`windows/src/styles.css`、`windows/settings.html`、`windows/floating-ball.html`、`windows/popover.html`，并核对窗口创建尺寸与 popover 动态尺寸逻辑。

截图中“悬浮球”标题只在左侧、右侧“显示桌面宠物”没有标题的现象，不是文案或渲染问题，而是 DOM 语义分组与全局 CSS Grid 自动排布相冲突。当前设置页以一个通用两列网格承载所有 `.group`；每一个 `.group` 都是一个独立 grid item，浏览器只按 DOM 顺序把它们放入列。第一页第一个 group 有 `.ghead`，紧随其后的第二个 group 没有 `.ghead`，于是两个不属于同一标题的卡片同行，形成截图中的横向“标题/内容不对齐”。

当前设置主窗最小内宽 760px；固定左栏 300px、内容区左右 padding 合计 48px、列间距 22px 后，双列卡片宽度约为 `(760 - 300 - 48 - 22) / 2 = 195px`。这不足以稳定承载带开关的多行说明、分段控件、滑杆和本地化文案。CSS 目前没有任何按宽度的媒体查询，只有减少动效查询。因此窄窗口不是降级问题，而是现有布局的确定性失效。

## Files Retrieved

1. `windows/src/styles.css`（90-445）- 设置窗外层、固定左栏、顶部标签、`.pages`、`.page.sel`、`.group`、`.ghead`、`.gcard`、`.row` 的核心布局规则。
2. `windows/src/styles.css`（480-850）- 滑杆、分段控件、宠物/动画网格、文本域与 care 二级网格的最小宽度压力来源。
3. `windows/src/styles.css`（1330-1533）- 浮球菜单的绝对定位、固定菜单宽度和内容滚动边界。
4. `windows/src/styles.css`（1538-1644）- popover 卡片、行、底部操作区及唯一的 `prefers-reduced-motion` 媒体查询。
5. `windows/settings.html`（75-172）- Pet 页面中导致标题/内容错位的相邻 group，以及宽卡与普通卡混排。
6. `windows/settings.html`（176-290）- Bubble 与 Care 页的宽卡/普通卡混排模式。
7. `windows/settings.html`（294-374）- General 页中四张普通设置卡和一个宽卡交错的模式。
8. `windows/floating-ball.html`（8-29）- 小尺寸独立窗口内的球体与快速消息菜单 DOM。
9. `windows/popover.html`（8-49）- 300px popover 的头部、两条操作行和三项底部操作 DOM。
10. `windows/src-tauri/src/lib.rs`（176-196）- settings 默认 `1000 x 680`、最小 `760 x 560`、允许缩放。
11. `windows/src-tauri/src/lib.rs`（405-428）- 浮球是不可缩放、透明、无边框的独立固定窗口。
12. `windows/src-tauri/src/lib.rs`（518-540）与 `windows/src/popover.ts`（108-117）- popover 初始 `300 x 430`、不可缩放，脚本会按内容把高度调整到 220-560px，宽度固定 300px。

## 具体原因

### 1. 通用 `.page.sel` 强制双列，且缺少断点

`windows/src/styles.css:351-360`：

```css
.page { display: none; }
.page.sel {
  display: grid;
  grid-template-columns: 1fr 1fr;
  column-gap: 22px;
  align-content: start;
}
.page.sel > .group.wide { grid-column: 1 / -1; }
.page.sel > .page-header { grid-column: 1 / -1; }
```

问题：

- `.page.sel` 是所有四个页面共享的布局控制器，却无条件规定两列；页面内容复杂度和语义不相同，不能共用“所有普通 group 两列排”的规则。
- `wide` 是仅有的布局语义，实际变成“手工逃离全局网格”的补丁。普通卡、宽卡按 DOM 插入顺序交错后，整体流向不可从页面语义推导。
- 只设 `column-gap`，未明确 `row-gap`；相邻项只能依赖各自 `margin-bottom: 24px`（`styles.css:396`）产生行距。不同内容高度时，Grid 同一行会以该行最高项决定下一行起点，造成额外空白和扫描节奏不稳定。
- 没有 `@media (max-width: ...)`。现有 `@media` 只处理减少动效（`styles.css:1633-1644`），不能切换主栏、页内容、控件行或网格密度。

### 2. 截图中标题与右侧卡片不对应的 DOM 根因

`windows/settings.html:83-98`：

```html
<div class="group">
  <div class="ghead"><span id="t-ball">Floating ball</span></div>
  <div class="gcard">...</div>
</div>
<div class="group">
  <div class="gcard">...</div>
</div>
```

两个 `.group` 是 `.page.sel` 的前两个 grid children，因此分别落在第一列和第二列。第一个有“悬浮球”标题，第二个没有 `.ghead`；第二张卡视觉上只能被误读为“悬浮球”的右栏内容。截图恰好显示此结构：左上为“悬浮球/显示悬浮球”，右上为无标题的“显示桌面宠物”。

这不是通过给第二卡补一个边距、给标题绝对定位、或用 `nth-child` 可修复的问题。正确的结构需要让共享标题与其全部内容拥有同一个布局容器，或给每个并列卡明确完整标题。

同类风险还存在于：

- Pet 页：宽“选择宠物”之后，`Desktop pets`、`Animations`、`Size on screen`、`Roam` 都是普通 group，`settings.html:118-172`。它们会按两个一组自动成行，不一定是语义上的成对关系。
- Bubble 页：`Appearance` 与 `Style` 可并排，但其后“Bubble messages”（wide）、“Left-click pet”（普通）、“Quick bubbles”（wide）依赖 DOM 顺序和 `.wide` 来恢复行流，`settings.html:182-259`。
- General 页：Language、Launch、Notifications、Motion 四个普通 group 自动两两配对；Sounds 是 wide，About 又回到普通 group，`settings.html:300-374`。只要新增、隐藏或移动一个 group，配对就会改变。
- Care 页的所有 group 都强制 `wide`，`settings.html:269-290`，说明它本来就是单列 dashboard；通用双列在该页没有价值。

### 3. 尺寸契约与布局规则矛盾

- 设置窗允许压缩到 760px（`lib.rs:188-190`）。
- 左侧宠物面板固定 `flex: 0 0 300px`（`styles.css:118-124`）。
- 右内容 `.pages` 固定左右 24px padding（`styles.css:344-350`）。
- 内容区在最小窗口约 412px，双列加 22px 间距后每列约 195px。
- `.row` 是不可换行的横向 `flex`，默认 `justify-content: space-between`，并固定左右 16px padding（`styles.css:424-434`）；说明文本 `.rt` 没有 `min-width: 0`/可控换行策略，开关、select、seg、slider 会争抢同一行宽度。
- 分段控件按钮 `white-space: nowrap`（`styles.css:551-569`）；滑杆容器最大 240px（`styles.css:481-483`）；动画卡固定 78px 列（`styles.css:675-691`）。在 195px 卡宽下，中文、英文和越南文均可能挤压或溢出。

截图宽窗口下卡片很宽，因此问题表现为大块无效空白和不对应的横向成对关系；最小窗口下则转为控件挤压、文本行数激增和横向溢出风险。两者同根：布局以“元素顺序”代替“页面布局计划”。

### 4. 浮球和 popover 不应继承 settings 的响应式方案

三份 HTML 都链接同一份 `styles.css`，但窗口契约不同：

- `floating-ball.html:8-29` 是球体和绝对定位快速菜单；其窗口由 Rust 以固定 `BALL_W x BALL_H` 创建且不可缩放（`lib.rs:414-426`）。菜单 CSS 也使用 `left/top` 与 `width: var(--menu-width)`（`styles.css:1387-1403`）。它需要的是窗口内菜单位置翻转与内容上限，而不是 settings 的双列/单列断点。
- `popover.html:8-49` 是固定 300px 宽的独立瞬态窗口。`.pop-card` 没有高度填满规则，`popover.ts:108-117` 会把窗口高度收敛到内容高度。`.pop-foot` 是单行 flex，含 Settings、Updates、弹性 spacer、Quit（`styles.css:1605-1617`）；300px 宽度下多语言文字会先挤压该行，不能指望主设置页的断点修复它。
- CSS 选择器已通过 `body.settings`、`.floating-ball-body`、`body.pop` 分域；后续改动应保持域隔离，避免对裸 `body`、`.row`、`.seg` 做跨窗口的响应式覆盖。

## 可落地的布局规范

### A. 设置主窗采用“页面计划 + 明确布局容器”

1. `.page` 只负责显示、动画、纵向内容流和统一间距；不再作为所有业务卡片的无条件双列容器。
2. 每页以明确的 section/布局容器表达意图：
   - `page-stack`：单列，适合说明长、表单复杂、库/编辑器、Care dashboard。
   - `page-grid`：仅容纳可独立阅读且高度相近的 sibling group；由该容器决定两列，不靠 page 根节点自动配对。
   - `group`：一个完整、可独立扫描的设置单元，必须包含自己的 `.ghead`，或被放入带共享标题的 section 容器。
   - `group--wide`：内容天然跨列（宠物库、动画库、文本域、声音列表、Care 摘要），不是用于修复不小心落到第二列的逃生类。
3. “悬浮球”和“显示桌面宠物”应选择其中一种明确语义：
   - 若同属“桌面显示”设置：外层 section 给一个共享 `.ghead`，内部再用局部 `settings-pair-grid` 放两张完整卡；或合并成一张 `.gcard` 的两条 `.row`。
   - 若是独立概念：两张 group 都带各自 `.ghead`，例如“悬浮球”和“桌面宠物”。不能只给左卡标题。
4. 将“会动态变长/有可滚动内容”的区域放入单列：Choose pet、Desktop pets、Animations、Bubble messages、Quick bubbles、Sounds、Care。它们不应与无关短卡抢同一 grid row。

### B. 桌面宽度策略

以实际可用的 `.pages` 内容宽度为判断对象，而不是仅凭系统窗口总宽度：

- 宽布局：内容区至少约 760px 时，局部 `page-grid` 使用两列 `minmax(0, 1fr)`，gap 20-24px；每张可并列 group 保持完整标题与卡片。
- 常规设置窗（默认 1000px 总宽，减 300px 左栏和内容 padding 后约 652px）：默认应为单列，或仅对轻量、确定成对的局部区块双列。截图虽是超宽窗口，不能反推默认 1000px 窗口也适合全局双列。
- `.pages` 使用 `min-width: 0`，局部 grid 使用 `minmax(0, 1fr)`；卡内文字主体使用 `min-width: 0`。这是防止 flex/grid intrinsic sizing 引入横向滚动的基础约束。

### C. 窄窗口策略

建议使用内容区导向的 CSS 断点；若无法引入 container query，可用总窗口断点并扣除左栏影响：

- `<= 900px` 总窗口（或容器约 `< 760px`）：所有 settings content grid 降为单列。
- `<= 760px` 总窗口，恰好是当前最小宽：左侧 `.pet-panel` 不应继续固定 300px。推荐将 `.shell2` 改为纵向，宠物区压缩为横向摘要条（品牌、小画布、名字/等级、Feed），主内容占满宽度；若产品明确要求左栏持续存在，则应提高最小窗口宽度到能保证内容单列可用的值，约 900px 以上，并把这作为窗口契约变更。
- 紧凑模式中，`.row` 对多行文字使用 `align-items: flex-start`，文本主列 `min-width: 0; flex: 1`；长分段控件允许自身换行或切为纵向/全宽，而不是依赖 `white-space: nowrap` 在小卡中硬塞。
- 宠物网格继续使用自身的 `repeat(auto-fill, minmax(...))`，但最小列宽应来自可点击目标和缩略图尺寸；动画网格应允许 `repeat(auto-fit, minmax(78px, 1fr))` 或在窄宽中减少卡片数量，不能由外层两列卡进一步压缩。
- 所有长文案和多语言控件需在中文、英文、越南文下验证。越南文通常是最早暴露 segmented control 与 footer 溢出的语言。

### D. 浮球与 popover 的独立策略

- 浮球：维持固定窗口与绝对锚定。菜单必须由运行时基于可用工作区选择下方/上方及左右翻转，且 `max-height` 保持内部滚动。不要给它添加 settings 页面断点；它不是可缩放设置表单。
- Popover：固定 300px 宽的条件下，`.pop-foot` 应允许底部操作换行，或在窄文本语言下切成两行/图标按钮，而非依赖 spacer 挤压。高度仍交给既有 `fitWindow()` 动态收敛。`.pop-row` 中 label 保留 `min-width: 0`，range 保留合理最小宽度。

## 最小调整范围（建议实施顺序）

1. `windows/src/styles.css`
   - 移除或改写 `styles.css:352-360` 的全局两列声明：`.page.sel` 变为单列垂直流；新增局部 `.page-grid`/`.settings-pair-grid` 负责确实需要的两列。
   - 新增 settings 专属宽度媒体规则，并为最小窗口规定左栏折叠/纵向重排策略。
   - 补齐 `.row` 文本主列、卡片 grid item、局部 grid 的 `min-width: 0`，并在紧凑规则下处理 `.seg`、滑杆、`.two-btns`、`.pop-foot` 的换行/堆叠。
   - 不修改浮球、popover 的现有结构性定位，只增加它们各自必要的窄内容规则；所有选择器限定在 `body.settings`、`.floating-ball-body`、`body.pop` 域内。
2. `windows/settings.html`
   - 修复 `75-172`：将“悬浮球”与“显示桌面宠物”收进同一带标题的 section/卡，或为后者新增完整标题。
   - 为 Pet、Bubble、General 页显式加入局部布局容器并分类：短且独立的卡可以成对；库、编辑器、长说明、声音和 Care 维持单列宽卡。
   - 将 `.wide` 从泛化布局逃生标记收敛为真正的业务语义（可保留类名以降低改动量，但其位置只能由局部容器解释）。
3. `windows/floating-ball.html`、`windows/popover.html`
   - 原则上无需为“设置页卡片错位”改 DOM。仅在实施 popover 底部操作区的窄语言策略时，才考虑为 footer 分组或图标可访问标签增加极小 DOM 调整。
4. `windows/src-tauri/src/lib.rs`（决策性而非首选的必改项）
   - 只有在不做左栏折叠且仍要保证 760px 可用时，才需提高 `.min_inner_size(760, 560)`。推荐优先做 CSS 重排，避免把可达性问题转化为强制更大窗口。

## 验收标准

- Pet 页顶部两张控制卡不再出现“左卡有标题、右卡像是同一标题下的另一项”的视觉关系。
- 每个并列卡都能独立说明自身类别，或由同一个可见 section 标题统领；新增/隐藏一个 group 不会改变无关卡片的配对关系。
- 在 1000x680 默认窗口、760x560 最小窗口、截图对应的宽窗口分别检查 Pet/Bubble/Care/General；不出现横向滚动、控件覆盖、标题与卡片脱节。
- 用中文、英文、越南文验证：分段按钮、滑杆数值、开关行、底部操作区均不截断关键操作。
- 浮球仍能在上下方打开菜单；popover 在内容增减后仍由 `fitWindow()` 正确调整高度，且固定 300px 宽时底部操作不重叠。

## Start Here

先打开 `windows/src/styles.css:343-445`。这里是全局 `.page.sel` 双列和卡片/行基础规则的根因；先把它收敛为页面级纵向流加局部网格，之后再按 `windows/settings.html:75-374` 的页面语义重组 DOM，改动最小且不会波及浮球/Popover 的独立窗口样式。
