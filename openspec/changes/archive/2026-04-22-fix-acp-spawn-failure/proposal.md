## Why

mohist 的 agent runner 通过 `spawn('opencode', ['acp'])` 启动 opencode ACP 子进程来执行设计、构建、审查等工作流阶段。当前存在两个 P0 级缺陷导致整个工作流完全无法运行：

1. **opencode 二进制文件不在 PATH 中**：daemon 模式启动的 server 进程环境中没有 `~/.opencode/bin` 路径，spawn 立即失败 (ENOENT)，所有 ACP session 无法创建。
2. **spawn 失败后 Promise 永远挂起**：`proc.on('error')` 只记日志不传播错误，导致 `connection.initialize()` 永远不 resolve，pipeline 永远挂起，issue 进入僵尸状态（DB 显示 active 但无进程运行，且无法重启或审批）。

目前已确认 5 个 issue 中有 4 个因此卡死，工作流端到端完全不可用。

## What Changes

- 增加 opencode 可执行文件路径的配置化支持（config.jsonc + 环境变量 + 自动探测），并传递到 ACP session
- 修复 `createAcpConnection()` 和 `runAcpSession()` 中 spawn 失败时的错误传播：proc `error` 和 `exit` 事件必须 reject 初始化 Promise，确保上层 pipeline 能捕获错误并正确回滚 issue 状态
- 修复 agent runner catch 块中 DB 操作抛异常导致 issue 进入僵尸状态的问题
- 更新 `config-schema.ts` 支持 `opencode.binPath` 字段

## Capabilities

### New Capabilities
- `opencode-path-config`: opencode 可执行文件路径的配置、探测与使用
- `acp-spawn-error-handling`: ACP session spawn 失败时的错误传播与资源清理

### Modified Capabilities

## Impact

- `src/agent-runtime/acp-session.ts` — spawn 调用改为可配置路径，proc error/exit 事件增加错误传播
- `src/services/agent-runner-service.ts` — catch 块中 DB 操作增加 try/catch 保护，防止僵尸状态
- `src/config/config-loader.ts` — 增加 `resolveOpencodeBinPath()` 函数
- `src/config/config-schema.ts` — 增加 `opencode.binPath` 字段
- `src/server/index.ts` — 解析 opencode 路径并注入到 acpOptions
- `src/api/issues.ts` — acpOptions 传递 opencodeBinPath
