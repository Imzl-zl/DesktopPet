Inherited decisions:
- 本轮只做独立静态审查，不修改文件。
- 验收重点是日志边界与恢复、并发导出、脱敏、恢复出厂重启竞态，以及迁移文件错误可见性。
- 真实 Win32/进程/文件共享行为不能由单元测试替代。

Diagnosis:
- 日志单条 UTF-8 字节上限、轮转失败后的流重开、导出时的 `FileShare.ReadWrite | FileShare.Delete`、重启启动失败提示、父进程提前退出/PID 复用处理、部署名硬编码移除、迁移 JSON 日志与用户警告均已落实。
- 仍有一个 Important 脱敏缺口：`AuthorizationRegex` 只完整识别 `Bearer`、`Basic`、`Token` 三种 scheme。对 `Authorization: ApiKey short-secret`，它仅把 `ApiKey` 替换为 `[REDACTED]`，后面的 `short-secret` 会保留；该短值也不会被高熵规则兜底。

Drift / contradiction check:
- 当前实现仍未完全满足“所有 Authorization 凭据在落盘前必须脱敏”的既定安全约束。
- 其余审查项未发现与继承决策冲突。

Recommendation:
- Important: [SecretRedactor.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.Infra/Diagnostics/SecretRedactor.cs) 的 Authorization 规则应按字段边界遮蔽整个 Authorization 值，不能依赖已知 scheme 白名单或高熵兜底。
- 增加至少 `Authorization: ApiKey short-secret`、未知 scheme、JSON Authorization 值的回归测试。
- 修复后再运行 `DiagnosticsTests`；无需改动其他已通过的收尾逻辑。

Risks:
- 仍需真实 Windows 验证：日志轮转与 ZIP 导出并发共享、恢复出厂后的父子进程交接、自动重启失败提示、PID 复用路径、迁移警告窗口。
- 静态审查不能证明 `Process.Start` 成功后子进程一定完成启动；当前实现仅可靠报告同步启动失败。

Need from main agent:
- 无需产品决策；这是既定脱敏不变量下的定向修复。

Suggested execution prompt:
- 无需额外 worker handoff；主执行者可直接收紧 Authorization 脱敏规则并补测试。