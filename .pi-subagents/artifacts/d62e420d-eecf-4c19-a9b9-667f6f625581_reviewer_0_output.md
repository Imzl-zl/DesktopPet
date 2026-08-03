## Review
- Correct: `Pet` 已用 3-8fps 的 `setTimeout` 帧循环替代 60Hz rAF，[`windows/src/pet.ts:266-278`](C:/sudy/github/DesktopPet/windows/src/pet.ts:266)；漫游环境查询也有 500ms 缓存，[`windows/src/roam/environment.ts:31-74`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:31)。气泡文本相同会避免 DOM 重建，[`windows/src/bubble.ts:15-37`](C:/sudy/github/DesktopPet/windows/src/bubble.ts:15)，命中区域 IPC 也按几何签名去重，[`windows/src/pet-window.ts:353-382`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:353)。

**Critical**
- `windows/src/settings.ts:48-51,832-840`：尺寸与漫游速度滑块的每一次 `input` 都会保存状态、调用 `sync_desktop_pet_windows`，并广播全局 `pets-changed`。每个宠物窗口收到事件后，无论实例或精灵是否变化，都会调用 `loadInstanceSprite()`，[`windows/src/pet-window.ts:258-263`](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:258)。这会为每个窗口新建 `Image`、解码精灵图，并执行全尺寸 canvas `getImageData` 和像素扫描，[`windows/src/pet.ts:77-110`](C:/sudy/github/DesktopPet/windows/src/pet.ts:77)、[`windows/src/pet.ts:165-197`](C:/sudy/github/DesktopPet/windows/src/pet.ts:165)。触发一次滑块拖动可在最多 12 个桌面宠物窗口上反复放大为图片解码、canvas 读取和 Tauri IPC。最小安全修复：为 `Pet.load` 增加当前/待加载 URL 去重；`pets-changed` 携带实例 ID 与变更类型，仅目标窗口应用尺寸或漫游配置，且只有 `spriteSlug` 变化时加载图片。实例成员不变时不要调用原生窗口同步。

**Important**
- `windows/src/roam/engine.ts:103-132,158-174`：默认新实例启用 `wander`，[`windows/src/settings.ts:54-65`](C:/sudy/github/DesktopPet/windows/src/settings.ts:54)。每个活动漫游窗口以 30ms 执行一轮：读取窗口 scale factor 和外部坐标（两次 IPC），移动时再写一次窗口坐标；每 500ms 还会查询显示器与枚举全部系统窗口，[`windows/src/roam/window.ts:14-27`](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:14)、[`windows/src/roam/environment.ts:37-74`](C:/sudy/github/DesktopPet/windows/src/roam/environment.ts:37)。连续移动约为每窗口每秒 100 次以上 IPC；12 个窗口会线性放大。最小安全修复：在 engine 内保存最后成功写入的位置，避免每 tick 的两次位置读取；`wander` 只获取工作区，不枚举系统窗口，系统窗口枚举限于 `climb` 模式。
- `windows/src/pet.ts:152-158,266-278`、`windows/src/pet-window.ts:30,221-226`：每个 WebView 都启动不可取消的动画递归定时器、漫游循环及三个间隔器，但没有 `visibilitychange`、原生窗口隐藏事件或卸载处理来暂停它们。原生代码实际通过 `window.hide()` 隐藏桌面宠物，[`windows/src-tauri/src/lib.rs:554-560`](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:554)，而 `destroyRoam()` 虽存在却没有调用点。[`windows/src/roam/index.ts:27-30`](C:/sudy/github/DesktopPet/windows/src/roam/index.ts:27) 同一问题也影响失焦后被隐藏的 popover：它保留 `Pet` 实例，[`windows/src/popover.ts:33-43`](C:/sudy/github/DesktopPet/windows/src/popover.ts:33)，但仅执行 `hide()`，[`windows/src/popover.ts:86-108`](C:/sudy/github/DesktopPet/windows/src/popover.ts:86)。最小安全修复：为 `Pet` 提供 `pause/resume/destroy`，为 roam 提供 suspend/resume；由原生 show/hide 事件驱动，隐藏时取消帧定时器和漫游循环，显示时恢复。

**Minor**
- `windows/src/pet-window.ts:226`：每个宠物窗口固定每 120ms 唤醒一次以检查 `bubble.dirty`。该字段仅在 [`windows/src/bubble.ts:9-13`](C:/sudy/github/DesktopPet/windows/src/bubble.ts:9) 初始化为 `false`；在当前 `windows/src` 中没有任何写入。因此这是一条永久空转的 8.3Hz 定时器，12 个窗口为每秒约 100 次无效唤醒。最小安全修复：删除该轮询；实际需要刷新时直接调用 `render()` 或由 renderer 明确发出事件。

**Material Validation Gaps**
- 未执行真实 WebView2 运行时性能采样：缺少 1 个和 12 个宠物在静止、漫游、隐藏、恢复显示时的 CPU、GPU、IPC 次数、内存与图片请求记录。
- 当前测试未覆盖定时器清理、原生 hide 后的前端暂停、`pets-changed` 的扇出范围、精灵加载去重，或漫游 IPC 预算。
- 静态构建与单测通过只能验证编译和现有行为，不能证明透明窗口在 Windows/WebView2 隐藏后是否被运行时节流。