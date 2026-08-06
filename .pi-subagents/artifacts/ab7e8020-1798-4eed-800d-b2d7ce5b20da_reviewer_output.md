审查完成。以下是完整发现。

---

# 渲染与窗口模块审查报告

## 审查范围
- `DesktopPet.App/Rendering/*`（SpriteLoader / SpriteFrameBitmapSourceCache / PixelBuffer / ReusablePixelBuffer / PlaceholderPet）
- `DesktopPet.App/Windows/*`（PetWindow / FloatingBallWindow / DanmakuWindow / BubbleView / XamlIslandHost / PetWindowManager / SpritePreviewWindow / WelcomeWindow）
- `DesktopPet.Infra/Hotkey/HotkeyManager.cs`、`DesktopPet.Core/Rendering/*`（OverlayRenderer / PetRenderer / SpriteSheet）
- 交叉核查：`App.xaml.cs`（hotkey/Danmaku 接线）、`ModeService.cs`、`DanmakuEngine.cs`、`QuickBubbleController.cs`、`AppSettings.cs`

## Correct（做得好的地方）
- **WndProc 钩子生命周期管理正确**：`PetWindow.cs:474-477` 在 OnLoaded AddHook、OnClosed RemoveHook；`FloatingBallWindow`、`App.xaml.cs:HotkeyHook` 同样对称，且符合「钩子弱引用」的官方语义。
- **拖拽不抢焦点**：`NativeMethods.MoveWindow` 用 `SetWindowPos(SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE)`，正确。
- **WriteableBitmap 用法正确**：`PetWindow.cs:689-695` 的 `WritePixels(Int32Rect, byte[], stride, 0)` 签名与 stride 均正确；RGBA→BGRA 就地交换在写位图前完成，R/B 错位防护到位。
- **Timer 生命周期**：三个 DispatcherTimer 在 OnClosed 全部 Stop；`AnimationEnabled`/`SetDesktopInteractionSuspended`/`IsVisibleChanged` 的启停逻辑一致。
- **SpriteFrameBitmapSourceCache**：8MB LRU 有界、帧冻结、按实例作 key（record 对 byte[] 字段为引用相等，与调用方传同一实例一致）——设计正确。
- **HotkeyManager**：私有 id 范围（0xC000+）、幂等重注册、Dispose 全注销，无问题。

---

## Critical

### C1. SpritePreviewWindow.cs:46-48 — 用「编码后的文件字节」当原始像素缓冲构建预览图
```csharp
var source = BitmapSource.Create(
    sheet.SourceWidth, sheet.SourceHeight, 96, 96, PixelFormats.Bgra32, null,
    RgbaToBgra(_sourceBytes), sheet.SourceWidth * 4);   // _sourceBytes 是 PNG/WebP 文件字节！
```
- **问题**：`PetWindow.cs:257/264/272` 传入的 `bytes` 是 `File.ReadAllBytesAsync` 读出的**压缩编码文件**，不是解码后的原始 RGBA。`RgbaToBgra` 只做 R/B 交换不做解码。
- **为何是问题**：a) 常规 PNG/WebP 文件体积 < `SourceWidth×SourceHeight×4` 时，`BitmapSource.Create` 校验缓冲不足会抛 `ArgumentException`；该异常发生在 `async void OnDrop`（PetWindow.cs:243）里且**无全局 UnhandledException 兜底**（已 grep 确认），直接进程崩溃；b) 文件较大时预览显示的是 PNG 头部字节解释出的垃圾像素。即：拖入精灵导入的预览功能实际不可用/可崩溃。
- **修复建议**：在 `SpriteSheet.Decode` 内保留解码后的源图 RGBA（或新增 `SourceRgba` 属性），预览图用该解码数据构建；`_sourceBytes` 只用于 `ImportPayload` 落盘。

---

## Important

### I1. DanmakuWindow.cs:54,110-123,132-134 — DanmakuEngine 跨线程无同步访问
- **问题**：`_engine.Tick`（Win2D 渲染线程，line 112）与 `_engine.Enqueue`（UI 线程，`AiCoordinator` 经 `OnUiThread` 调用，line 132）并发访问同一个非线程安全的 `DanmakuEngine`（`_active` List、`_pool` Stack、`_trackTailX`、`_random`，`DanmakuEngine.cs` 无任何锁）。
- **为何是问题**：`List.RemoveAt`/`Add` 与 `Stack.Pop`/`Push` 并发交错会导致列表损坏、越界异常或条目错乱，且是偶发难复现的渲染崩溃。
- **修复建议**：`ShowDanmaku` 入口加锁，或将 `Enqueue` 投递到 render 线程消费的队列（如 `ConcurrentQueue` + 渲染帧内 drain），保持引擎单线程访问。

### I2. DanmakuWindow.cs:108,134,139-143 — Paused 置 false 后永不恢复 + Win2D 资源未释放
- **问题**：`_canvas.Paused` 只在初始 `true` 和 `ShowDanmaku` 置 `false`（line 134），**没有任何路径再置回 true**；`OnClosed` 只 `_island.DetachAndDispose(); _canvas = null;`（139-143），未调用 `CanvasAnimatedControl.Dispose()`/`RemoveFromVisualTree()`，`CanvasTextFormat`（IDisposable）也未释放。
- **为何是问题**：a) 违反注释「无弹幕时暂停渲染循环（CPU 归零）」——进入弹幕模式后哪怕一条弹幕都没有，Win2D 也以 60fps 永久空转（GPU/CPU 占用）；b) 每次模式切换（`ModeService.CloseDanmakuWindow` 建即毁）都泄漏一个 render loop + 交换链 + 文本格式资源。
- **修复建议**：`Draw`/`Update` 中检测 `_engine.Active.Count == 0` 时置 `Paused = true`；`OnClosed` 中 `_canvas.RemoveFromVisualTree(); _canvas.Dispose(); _textFormat.Dispose();`。

### I3. SpriteLoader.cs:23,102-106 — 已解码精灵缓存无上限
- **问题**：`_sheetCache` 是普通 `Dictionary`，`Cache()` 只增不减，无 LRU/容量上限。每次导入自定义精灵都会永久持有完整解码帧（RGBA + 掩码）。
- **为何是问题**：长期会话中（尤其用户多次导入自定义精灵）内存单调增长；SpriteSheet 可能数 MB，与 `SpriteFrameBitmapSourceCache` 的 8MB LRU 形成对照，属于遗漏的缓存边界。
- **修复建议**：给 `_sheetCache` 加容量上限（如 N 个 slug 或按字节计费）做最简 LRU 淘汰，或与帧源缓存共用预算。

### I4. PetWindow.cs:591,605-609,645 — 拖拽中鼠标捕获丢失会永久卡死 `_pressed`
- **问题**：拖拽期间若捕获被系统抢占（Alt-Tab、系统对话框、其他程序 `ReleaseCapture`），`WM_LBUTTONUP` 不会再送达本窗口，`OnRawLeftUp` 永不执行 → `_pressed` 恒为 true。
- **为何是问题**：此后 `OnRawLeftDown` 在 line 605 直接 `return` 且 WndProcHook 仍 `handled = true`，宠物对点击完全无响应，直到重启应用；无 `WM_CAPTURECHANGED`/`WM_CANCELMODE` 兜底。
- **修复建议**：在 WndProcHook 处理 `WM_CAPTURECHANGED (0x0215)` 与 `WM_CANCELMODE (0x001F)`，统一走 `OnRawLeftUp` 的收尾路径（清理 `_pressed/_dragging`、释放动作行）。

---

## Minor

### M1. PetWindow.cs:943-949 — `OnMouseLeftButtonDown/Up` 覆写是死代码
- WndProcHook 对所有左键消息置 `handled=true`，WPF 事件系统不会再触发这两个方法（注释自相矛盾：写着「WPF 事件仅用于兜底点击判定」）。建议删除，避免误导后续维护者。

### M2. PetWindow.cs:151-155,195 — 窗口尺寸硬编码 260×320，且 `PetInstance.Size` 字段完全未用
- `Size = 100`（PetWindowManager 多处赋值）在 App 层无任何读取（grep 确认零引用），是第二真值；`PetSizePercent` 只在固定缓冲内缩放精灵。建议删除死字段或让 `Size` 参与窗口/缓冲计算。

### M3. PetWindow.cs:640-641 — 拖拽延迟采样列表无界增长
- `_processingLatencyMs`/`_endToEndLatencyMs` 只在 bench 模式填值但从不截断；长拖拽可累积数万条 double。建议加最大样本数（如 4096）或环形缓冲。

### M4. PetWindow.cs:154 — 构造期取 DPI，缓冲尺寸固定，跨 DPI 显示器会模糊
- `_dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip` 在 Show 之前执行，且 `_bufferWidth/Height` 构造后不变；宠物拖到不同 DPI 的副屏时位图被非整数拉伸。建议在 `WM_DPICHANGED`/`DpiChanged` 事件里重建缓冲，或接受现状并记录已知限制。

### M5. FloatingBallWindow.cs:28,60-61 — 球体/窗口尺寸硬编码且与注释不符
- 注释称「48px 视觉球体」，实际 `BallSize = 56`、窗口 80×80，无设置项对接。尺寸变更需改源码。

### M6. DanmakuWindow.cs:27-33,54 — 弹幕视觉参数全部硬编码、无设置项对接
- `FontSize = 30`、字体 "Microsoft YaHei UI"、`trackCount:10`、`minSpeed:220/maxSpeed:420/minGap:220`。输出模式可被用户切换，但这些参数无 `AppSettings` 钩子。至少应集中为常量或接入设置。

### M7. DanmakuWindow.cs:40-41,66-68 — 全屏几何假设虚拟屏原点为 (0,0)
- `Left=0/Top=0` + `VirtualScreenWidth/Height`：当主屏不是虚拟屏左上角（有显示器在其左侧/上方）时，弹幕层会漏掉负坐标方向的显示器。建议用 `VirtualScreenLeft/Top` 定位。

### M8. PetWindowManager.cs:354-362 — `Shutdown()` 不关闭设置窗口
- 只关浮球和宠物窗；若设置窗开着退出，依赖 App 全局退出兜底。建议 `_settingsWindow?.Close()`。

### M9. PetWindow.cs:264 — `OnDrop` 中 `SpriteSheet.Decode` 在 UI 线程同步执行
- 大图/WebP 解码可致 UI 卡顿；建议 `await Task.Run` 解码后再建预览。

### M10. SpriteLoader.cs:157-166 — 失败日志无界追加
- `desktoppet-sprite.log` 每次失败 `AppendAllText`，长期离线运行日志无限增长；建议截断或限额。

---

## 总体结论
窗口/渲染骨架（钩子生命周期、Timer 清理、WriteableBitmap 直写、LRU 帧缓存）质量较高，但存在 1 个会导致崩溃的功能缺陷（精灵导入预览），以及弹幕层的跨线程竞态与资源/CPU 泄漏、精灵缓存无上限、拖拽捕获丢失卡死等 4 个需优先修复的重要问题。建议按 C1 → I1/I2 → I3/I4 顺序修复后再进入验收。