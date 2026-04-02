## Context

当前 mo-server 运行在 Node.js v20 上，没有注册 `process.on('unhandledRejection')` 或 `process.on('uncaughtException')`。Node.js v15+ 默认对未处理的 promise rejection 终止进程。

agent 后台执行采用 fire-and-forget 模式（`api/issues.ts:329-353`），catch 块内调用 `stateManager.updateIssueStatus()` 如果自身抛异常（如 SQLite 锁竞争），会产生未处理的 rejection 导致进程崩溃。

Project 上下文存在双重管理：`StateManager` 通过 `configRepo`（SQLite）管理，`ProjectService` 同时维护内存字段，两者可能不同步。`project use` 失败时不清除旧上下文。

## Goals / Non-Goals

**Goals:**
- Server 进程不会因未捕获异常/rejection 意外退出
- Agent 生命周期与 issue 操作（close/start）协调，避免竞态
- Project 上下文管理统一，语义清晰：无有效上下文时拒绝需要 project 的操作
- 现有测试 `verify-m1-infra` 全部通过

**Non-Goals:**
- 不引入新的 IssueStatus 枚举值（Closed 等），保持现有 Active/Paused/Blocked 语义
- 不修改 stage 状态机转换规则
- 不重构 agent runner 的整体架构
- 不处理 agent 运行时的超时/取消机制

## Decisions

### D1: 全局错误处理器采用 log-and-continue 策略

**选择**: 注册 `unhandledRejection` 和 `uncaughtException`，记录错误日志但不退出进程。

**替代方案**:
- (A) 仅修复已知崩溃点，不加全局处理器 → 容易遗漏其他路径
- (B) 崩溃后自动重启 server → 增加复杂度，丢失运行时状态

**理由**: M1 阶段 agent 是单线程顺序执行的，未处理 rejection 多数是 SQLite 锁竞争或状态更新的非致命错误。记录日志并继续是最安全的兜底策略。

### D2: Agent catch 块内嵌套 try-catch

**选择**: `api/issues.ts:348` 的 catch 块中对 `stateManager.updateIssueStatus()` 调用增加 try-catch。

**理由**: 这是已知的崩溃触发点。全局处理器是兜底，具体位置仍应显式处理。

### D3: Close handler 检查运行中 agent，等待优雅结束

**选择**: `close` handler 检查 `activeAgentPromise`，如果目标 issue 有 agent 在运行，返回 409 Conflict 并提示用户先停止 agent 或等待完成。

**替代方案**:
- (A) 强制 kill agent 再 close → agent 可能有未保存的工作
- (B) 忽略 agent 直接 close → 竞态导致数据不一致

### D4: Start handler 增加 blocked status 校验

**选择**: `POST /api/issues/:number/start` 增加 `status !== Blocked` 的前置检查，blocked issue 不允许 start。

**理由**: 当前只检查 `stage !== Draft`，blocked issue 如果 stage 恰好是 draft（或被外部修改），可以绕过预期约束。

### D5: Project use 失败时保留旧上下文（不清除）

**选择**: `project use <nonexistent>` 失败时返回 404 但不改变当前上下文。

**替代方案**:
- (A) 失败时清除上下文 → 用户误操作后会丢失当前 project，所有操作中断
- (B) 失败时清除 + 支持显式 unset → 增加复杂度

**理由**: "切换失败"不等于"取消选择"。CLI 已显示错误信息，用户知道切换未生效。如需清除，后续可加 `project unset` 命令。

### D6: 无 project 上下文时，需要 project 的 API 返回错误

**选择**: `POST /api/issues`（创建 issue）和 `POST /api/issues/:number/start` 在 `getCurrentProjectId()` 返回 null 时返回 400。

**理由**: 当前行为是使用残留的 project 上下文静默创建 issue，不符合最小意外原则。

### D7: 移除 ProjectService 的内存 currentProjectId

**选择**: 删除 `ProjectService` 中的 `private currentProjectId` 字段，`getCurrent()` 改为每次从 `configRepo` 读取。

**理由**: 消除双重状态，单一数据源。`configRepo` 基于 SQLite，读取开销可忽略（server 单进程，无跨进程一致性问题）。

## Risks / Trade-offs

- **[全局错误处理器可能掩盖 bug]** → handler 中打印完整 stack trace 和上下文信息，便于排查。后续可接入结构化日志。
- **[Close 被阻塞时用户体验下降]** → 返回清晰的错误信息告诉用户原因和解决方案（等待或手动干预）。
- **[D5 可能导致用户困惑]** → API 响应中包含当前 active project 信息，帮助用户理解状态。
