## Review

**Critical**
- 无。

**Important**
- `windows/src-tauri/src/sys_windows.rs:107` 在 TTL 失效时释放 `WIN_CACHE` 锁，随后才于 `:117` 执行 `EnumWindows`，并在 `:134` 回填缓存。多个请求可同时观察到失效缓存并并发枚举，150ms TTL 因此不能保证注释承诺的“无论宠物数量均约 7 次/秒”。每个枚举会对所有顶层窗口执行 `IsWindowVisible`、`GetWindowRect`、PID 查询和 `GetWindowTextW`（`:53-84`）。每个宠物 webview 也有自己的 500ms 前端缓存（`windows/src/roam/environment.ts:34-64`），12 个活动宠物最多每秒发出 24 个 `list_system_windows` IPC 请求，恰好同时过期时可能触发一批重复 Win32 枚举。最小安全修复：将“检查失效、枚举、写回”做成单飞刷新，例如持有缓存锁完成刷新，或使用 `refreshing + Condvar` 让其余调用等待同一快照。

- `windows/src/settings.ts:48-51` 对每次 `persistDesktopPets` 都立即 invoke 同步；`windows/src/settings.ts:832` 和 `:840` 又把尺寸、漫游速度滑块的每个 `input` 事件接到该路径。`windows/src-tauri/src/lib.rs:331-360` 为每次请求创建一个分离 OS 线程。generation 仅会丢弃尚未开始工作的旧线程，已经通过 `:352-357` 检查的线程仍会执行全量窗口扫描、show/hide 和创建逻辑。同步中每个宠物还会在 `:279` 或 `:298` 读取一次可见性文件。快速拖动滑块可造成线程和磁盘 I/O 突发。最小安全修复：尺寸及漫游配置仅保存并广播 `pets-changed`；原生 sync 仅用于实例增删或 `visible` 变化。作为次选，对 sync invoke 做 trailing debounce/coalescing，并使 Rust 侧只保留一个待处理的最新快照。

- `windows/src-tauri/src/lib.rs:607-700` 的后台线程每 60ms 扫描所有 `pet-` 窗口，包括已由 `set_desktop_pets_visible` 隐藏的窗口（`:554-560`）和单实例 `visible: false` 的窗口（`:298`）。在 12 个宠物上，它每秒尝试约 16.7 次 cursor 查询、扫描全部窗口，并调用约 200 次 `outer_position`（`:619-638`）；每约 1.02 秒还会额外为每个实例取一次位置（`:681-685`）。全局隐藏状态没有减少这些成本。最小安全修复：将全局和实例目标可见性保存在受管内存状态；后台循环跳过不可见实例，并在没有可见宠物时跳过 cursor 查询和窗口坐标读取。

- 活动漫游的 IPC 按实例线性放大。`windows/src/roam/engine.ts:119-131` 每个 30ms 活动 tick 都请求环境、读取当前位置并可能设置位置；`windows/src/roam/window.ts:16-17` 为一次位置读取连续执行 `scaleFactor` 和 `outerPosition`，`:27` 再执行 `setPosition`。12 个持续移动实例的理论上限约为每秒 396 次 scale-factor、396 次 outer-position、最多 396 次 set-position IPC；跟随鼠标模式在 `windows/src/roam/modes.ts:46-48` 还增加最多约 792 次 scale-factor/cursor IPC。实际速率会受 IPC 延迟限制，但会以动画流畅度为代价。最小安全修复：缓存 scale factor 至监视器变化，使用“最后成功写入的位置”避免每 tick 重读位置，并在 `restUntil` 期间先短路、避免在 `windows/src/roam/engine.ts:119-122` 获取环境和位置。

**Minor**
- `windows/src-tauri/src/lib.rs:692-696` 在持有 `PetPositionMap` mutex 时同步 JSON 序列化并写文件。移动时最多约每 1.02 秒发生一次；同时发生的 `saved_pet_position`（`:244-247`）会被磁盘延迟阻塞。最小安全修复：在锁内替换或克隆快照后立即释放锁，在锁外写入文件。

- `windows/src-tauri/src/lib.rs:681-695` 不会从 `last_saved` 或受管 `PetPositionMap` 清除已移除宠物的 ID。随着用户创建并删除不同 ID，`pet-positions.json` 会无限增长，并被每次启动读取、每次位置变更序列化和写入。最小安全修复：根据 `active` 标签同时 `retain` `last_saved` 和共享 position map；仅清理本身也应触发一次持久化。

- 日志在正常点击穿透翻转路径上有 60 条总上限（`windows/src-tauri/src/lib.rs:657-664`），这一点合理；但 `dlog` 每次仍同步执行目录创建、打开、追加写入（`:106-118`），没有轮转或错误去重。持续 `setPosition` 错误会在 `windows/src/roam/engine.ts:131` 每个活动 tick 继续发起 `log_debug`，并因 `:132` 返回 active 而维持高频循环。最小安全修复：对同类错误做时间窗口去重/计数汇总，并限制或轮转 `debug.log`。

**Correct**
- `windows/src/pet-window.ts:378-382` 以四舍五入后的 hit-rect 签名去重，因此 500ms render 定时器不会在几何未变时发送 `set_hit_rect` IPC。
- `windows/src-tauri/src/lib.rs:619-655` 每 tick 只读取一次 cursor、只获取一次 hit-rect mutex 快照，并且只在 ignore 状态变化时调用 `set_ignore_cursor_events`。
- `windows/src-tauri/src/lib.rs:335-342` 强制 12 个实例上限、ID 长度及字符集约束，限制了单次同步和 hit-rect map 的正常规模。
- 原生点击穿透后台线程只在 setup 时创建一次（`windows/src-tauri/src/lib.rs:607`），不是每个宠物各创建一个常驻线程；需要解决的是其隐藏窗口工作量，而非线程数量本身。

**Validation gaps**
- 未在真实 Windows 桌面上以 1、6、12 个实例采集 CPU、线程数、`EnumWindows` 调用数、IPC 数量、工作集或磁盘写入量。
- 没有覆盖并发缓存失效/单飞语义、滑块同步合并、隐藏宠物跳过轮询、位置 map 清理的自动化测试。
- 现有前端测试及 `cargo check` 验证可构建，但不能证明 Tauri/Win32 在实际多窗口和 DPI 环境中的运行时性能。