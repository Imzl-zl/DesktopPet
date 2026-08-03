## Review

- Critical: 无。
- Important: [windows/src/roam/window.ts:25](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:25) 到 [windows/src/roam/window.ts:36](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:36) 的跨 DPI 事件顺序仍有竞态。`onMoved` 立即以当前 `cachedScaleFactor` 把物理坐标写入逻辑坐标缓存；若跨显示器时 `onMoved` 先于 `onScaleChanged` 投递，则这里仍是旧 DPI 比例，`currentLogicalPos()` 在缩放事件到达前会直接返回错误缓存（[window.ts:54](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:54)-[window.ts:56](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:56)），而不会重新读取原生位置。现有竞态用例只覆盖 `onScaleChanged` 再 `onMoved` 的顺序（[windows/src/roam/window.test.ts:113](C:/sudy/github/DesktopPet/windows/src/roam/window.test.ts:113)-[windows/src/roam/window.test.ts:118](C:/sudy/github/DesktopPet/windows/src/roam/window.test.ts:118)），不能证明该反向顺序安全。这与“防止跨 DPI 事件竞态”的目标不符，修复并补充反向顺序测试前不建议 ready。
- Minor: 无。

已确认的正确点：
- [windows/src/roam/window.ts:25](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:25)-[window.ts:49](C:/sudy/github/DesktopPet/windows/src/roam/window.ts:49) 使用 Tauri v2 的 `onMoved` / `onScaleChanged`，并在任一监听注册失败时调用所有已成功取得的 unlisten 函数；`trackingStarted` 仅在两项均成功时设置，后续调用会重试。对应单测覆盖注册失败与部分注册清理（[windows/src/roam/window.test.ts:121](C:/sudy/github/DesktopPet/windows/src/roam/window.test.ts:121)-[window.test.ts:158](C:/sudy/github/DesktopPet/windows/src/roam/window.test.ts:158)）。
- [windows/src-tauri/src/lib.rs:629](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:629)-[lib.rs:637](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:637) 先以 `is_visible().unwrap_or(true)` 建立可见窗口集合：已知隐藏窗口不会触发 cursor 查询；visibility 查询失败则按可见处理，保留交互安全路径。
- 点击穿透几何读取仅遍历 `visible_wins`（[windows/src-tauri/src/lib.rs:646](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:646)-[lib.rs:675](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:675)），每秒位置持久化同样仅遍历 `visible_wins`（[windows/src-tauri/src/lib.rs:690](C:/sudy/github/DesktopPet/windows/src-tauri/src/lib.rs:707)）。性能契约也明确断言这两点（[windows/src/performance-contract.test.ts:51](C:/sudy/github/DesktopPet/windows/src/performance-contract.test.ts:51)-[performance-contract.test.ts:65](C:/sudy/github/DesktopPet/windows/src/performance-contract.test.ts:65)）。

结论：Not ready，须解决上述 Important 跨 DPI 缓存竞态。

```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "发现并以 windows/src/roam/window.ts:25-36、54-56 及 window.test.ts:113-118 定位了一项 Important 跨 DPI 事件顺序竞态；其余目标行为的证据已逐项列出。"
    }
  ],
  "changedFiles": [
    "windows/src/roam/window.ts",
    "windows/src/roam/window.test.ts",
    "windows/src-tauri/src/lib.rs",
    "windows/src/performance-contract.test.ts"
  ],
  "testsAddedOrUpdated": [
    "windows/src/roam/window.test.ts",
    "windows/src/performance-contract.test.ts"
  ],
  "commandsRun": [
    {
      "command": "cd windows && npm test -- --run src/roam/window.test.ts src/performance-contract.test.ts",
      "result": "passed",
      "summary": "2 个测试文件、16 项测试全部通过。"
    },
    {
      "command": "git diff --check -- windows/src/roam/window.ts windows/src/roam/window.test.ts windows/src-tauri/src/lib.rs windows/src/performance-contract.test.ts",
      "result": "passed",
      "summary": "指定范围内未发现空白符错误。"
    }
  ],
  "validationOutput": [
    "Vitest v4.1.10：Test Files 2 passed，Tests 16 passed。",
    "已核对安装的 @tauri-apps/api 2.11.0 类型声明：onMoved 与 onScaleChanged 均为官方 WebviewWindow API。"
  ],
  "residualRisks": [
    "跨 DPI 切换时若 onMoved 在 onScaleChanged 前投递，缓存会使用旧 scale factor，直到缩放事件再到达；该间隙内 roam/physics 可能取得错误逻辑坐标。"
  ],
  "noStagedFiles": true,
  "diffSummary": "审查范围内新增窗口位置事件缓存、监听失败清理重试、已知隐藏宠物的点击穿透与位置持久化查询短路，以及相应契约测试。",
  "reviewFindings": [
    "important: windows/src/roam/window.ts:25-36,54-56 - onMoved 先于 onScaleChanged 时以旧 DPI 比例缓存坐标；window.test.ts:113-118 仅覆盖相反顺序。",
    "no critical findings",
    "no minor findings"
  ],
  "manualNotes": "仅审查用户指定的四个文件；未评论工作区其他既有未提交改动，也未修改实现代码。"
}
```
