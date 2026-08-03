# 窗口位置缓存架构评估（只读）

## Inherited decisions

- 范围是 Windows Tauri v2 漫游窗口。30 ms 物理循环保留，但正常自动移动不得每轮执行 `outerPosition()` 与 `scaleFactor()` IPC。
- 自动漫游的唯一位置真相是成功 `setLogical(target)` 的逻辑目标；仅原生用户拖拽期间允许 `onMoved` 写缓存。
- 拖拽开始/结束和 DPI 改变均使缓存失效，后续按需合并为一次原生读取。
- 本地 Tauri v2 API 声明：`onMoved` 的载荷为 `PhysicalPosition`，`onScaleChanged` 的载荷为 `ScaleFactorChanged`（含 `scaleFactor`、`size`）；二者只提供监听/取消监听，不承诺事件来源、跨事件顺序或原子位置-DPI 快照。

## Diagnosis

**结论：该模型比事件全时跟踪更稳健，应采用。** 它把缓存写权限按来源划分：应用自动移动由成功命令确认，用户拖拽由事件确认；DPI 或时序不确定时降级为失效缓存再读取。这样不再让旧的 programmatic `onMoved` 晚到后覆盖新目标。

现有实现仍违反此模型：[windows/src/roam/window.ts](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:29) 对每个 `onMoved` 都失效并异步读取“当前” `scaleFactor()`；监听器没有拖拽状态判断（第 52-54 行）。旧 `setPosition` 产生而晚送达的事件因此能覆盖后来的 `setLogical` 目标；移动物理坐标也可能与后取比例不匹配。

`onMoved` 无法标识是 `startDragging` 还是 `setPosition` 所致；`onScaleChanged` 不带位置；官方接口没有两类事件的排序契约。因此“所有事件都是权威位置”的正确性不可证明。

## Drift / contradiction check

- **高风险** [windows/src/roam/window.ts](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:29)：所有 `onMoved` 都能写缓存，违背“只有原生拖拽事件可写”。`cacheGeneration` 不能识别迟到的旧 programmatic 事件。
- **高风险** [windows/src/roam/window.ts](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:32)：move 的 `PhysicalPosition` 与后续独立查询的当前 `scaleFactor()` 无原子配对保证，跨 DPI 时可写入错误逻辑坐标。
- **中风险** [windows/src/roam/window.ts](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:57)：仅在没有 pending move 时才因 scale 失效，隐含假设 move/scale 顺序；该假设不是 Tauri v2 契约。
- **中风险** [windows/src/roam/window.ts](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:110)：generation 防止旧 Promise 回写，但仍允许多个原生命令在飞。若没有单一写入所有者，缓存“最新目标”可与 OS 最终位置分离。

## Recommendation

最小接口应是一个显式状态机，而非事件全时跟踪：

```ts
type PositionCache = {
  mode: "programmatic" | "native-drag";
  logical: Point | null;
  generation: number;
  dpiEpoch: number;
  pendingRead: Promise<Point | null> | null;
};

beginNativeDrag(): void;
endNativeDrag(): void;
getLogical(): Promise<Point | null>;
setLogical(target: Point): Promise<void>;
dispose(): void;
```

1. `setLogical` 先递增 generation 并失效；`await win.setPosition(...)` 成功后，只有该 generation 仍最新且 `mode === "programmatic"` 才缓存 `target`。失败不写缓存，错误仍由调用者处理。
2. `onMoved(physical)` 仅在 `mode === "native-drag"` 时处理。handler 开始记录 generation 与 `dpiEpoch`；读取比例后仅当二者未变且仍在拖拽才提交 `physical / scaleFactor`，否则保持失效，等待按需读。
3. `onScaleChanged` 无条件 `dpiEpoch++`、generation++、清空缓存。可以记录事件自带比例，但不要把它与先前/未知时刻的 move 强行配对。
4. `getLogical` 合并并发的失效读；在 `scaleFactor()` 与 `outerPosition()` 整个读取期间若 generation 或 `dpiEpoch` 改变，丢弃结果，下一次按需重读。
5. `beginNativeDrag()` 必须在 `startDragging()` 前调用，`endNativeDrag()` 放入 `finally`。结束直接失效，不接受迟到 move；下一次计算读取最终原生落点。接入点在 [windows/src/pet-window.ts](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:330) 和 [windows/src/pet-window.ts](C:/sudy/github/DesktopPet/windows/src/pet-window.ts:341)。
6. 保持 `setLogical` 单一顺序所有者。engine 与 physics 已集中走此函数（[windows/src/roam/engine.ts](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:130)）；若未来可并发调用，必须按命令序号串行化，不能依赖 Promise 完成顺序。

该设计只在初始化、拖拽结束、DPI 改变或检测到竞态后做一次合并读取；自动 30 ms 路径直接消费成功命令目标。

## Risks

- **残余高风险**：Tauri 前端 API 的 `scaleFactor()` 与 `outerPosition()` 不是原子快照。generation/epoch 仅能拒绝已观测到的交叉事件；若产品要求严格跨 DPI 原子性，需要 Rust command 在同一原生调用周期返回位置和比例。
- **残余中风险**：`startDragging()` 完成后 OS move 可能仍排队。结束即失效可避免旧事件污染，代价是第一次后续计算一次 IPC。
- **残余中风险**：自动移动和拖拽不得并发发位置命令。当前 engine 在拖拽时停止 `stepMode`（[windows/src/roam/engine.ts](C:/sudy/github/DesktopPet/windows/src/roam/engine.ts:79)），以后新增调用点也必须遵守。
- **残余低风险**：应保存并在窗口销毁时调用两个 `UnlistenFn`；当前没有明确 `dispose` 生命周期接口。

## Need from main agent

无需产品决策。实施前只需确认：所有 `setLogical` 是否已经单一顺序调用。若不是，须先定义“最新请求目标”或“实际完成顺序”何者权威，再串行化。

## Suggested execution prompt

无需 worker handoff；这是架构咨询，主实现者可直接按上面的最小接口调整。