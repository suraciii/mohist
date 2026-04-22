## Context

mohist 通过 ACP (Agent Client Protocol) 与 opencode 通信。核心调用链：

```
mo issue start N
  → AgentRunnerService.startPipeline()
    → WorkflowController.run()
      → runPlanStage() / runPipelineBuildStage() / runPipelineReviewStage()
        → createAcpConnection() 或 runAcpSession()
          → spawn('opencode', ['acp'])
```

当前 `spawn('opencode', ...)` 硬编码命令名，且 spawn 失败后没有任何错误传播机制。两个 `createAcpConnection()` (multi-round) 和 `runAcpSession()` (single-round) 都存在相同问题。

server 以 daemon 模式启动时 (通过 `mo-server` bin)，环境变量中不包含 `~/.opencode/bin`，导致所有 spawn 立即失败。

## Goals / Non-Goals

**Goals:**
- 让 ACP session 能在 daemon 环境中正确启动 opencode
- spawn 失败时能正确传播错误，使 pipeline 能 catch 并回滚 issue 状态
- 防止 pipeline Promise 永久挂起

**Non-Goals:**
- 不处理 issue 僵尸状态的自动恢复（单独的变更）
- 不处理 recoverable issues 的自动 resume（单独的变更）
- 不修改 ACP 协议本身
- 不处理 artifacts 丢失问题

## Decisions

### D1: opencode 路径配置方式

**决策**: 三级优先级 — 环境变量 > config.jsonc > 自动探测

1. `OPENCODE_BIN_PATH` 环境变量
2. `config.jsonc` 中 `opencode.binPath` 字段
3. 自动探测顺序: `~/.opencode/bin/opencode` → `which opencode`

**理由**: 环境变量方便 CI/CD 覆盖；config.jsonc 适合持久化配置；自动探测作为兜底减少用户配置负担。

**备选方案**:
- 只用环境变量 — 对桌面用户不友好
- 只用 config — 灵活性不够

### D2: spawn 错误传播机制

**决策**: 使用 Promise + proc error/exit 事件双重保护，事件驱动而非轮询等待

在 `createAcpConnection()` 和 `runAcpSession()` 中：
1. 创建 `spawnError` 变量和 `procExitedBeforeInit` 标志
2. `proc.on('error')` 时记录错误到 `spawnError`
3. `proc.on('exit')` 时如果 `exitCode !== 0` 且 initialize 未完成，设置 `procExitedBeforeInit` 标志并记录 exit code
4. 在调用 `connection.initialize()` 之前检查这两个标志，如有错误立即 throw
5. initialize 成功后设置 `initialized = true`，后续 exit 事件不再视为错误

**理由**: 
- `proc.on('error')` 捕获 spawn 阶段错误（ENOENT、EACCES）
- `proc.on('exit')` 捕获进程启动后立刻崩溃的情况（参数错误、依赖缺失）
- 事件驱动无需等待，不增加启动延迟
- 两个事件互斥：成功 spawn 后不会触发 error，error 后不会触发 exit

### D3: 配置项在代码中的传递方式

**决策**: 在 `AcpConnectionOptions` 接口中增加 `opencodeBinPath?: string` 字段，由 server 初始化时从配置解析并注入。

**理由**: 最小改动，不需要修改 ACP 协议或 SDK。路径解析集中在 config-loader，下游只使用解析后的值。

## Risks / Trade-offs

- **[自动探测路径变化]** 用户升级 opencode 后安装路径可能变化 → 记录 warning 日志，建议用户在 config 中显式配置
- **[向后兼容]** 新增的 `opencodeBinPath` 字段是可选的，不影响现有配置
- **[DB 操作失败]** catch 块中 `updateIssueStatus` 或 `updateStage` 可能抛异常 → 每个 DB 操作独立 try/catch，确保 `activeAgents.delete` 在 finally 中执行
- **[proc exit 与 error 竞态]** 理论上 error 和 exit 可能都触发 → 实现时优先处理 error（spawn 失败），exit 只在未初始化时检查
