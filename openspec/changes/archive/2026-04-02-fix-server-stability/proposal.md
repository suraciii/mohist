## Why

agentic 测试发现 server 进程在 `mo issue close` 后崩溃（ECONNREFUSED），原因是 agent 后台 promise 的 catch 块抛出未处理的 rejection，Node.js v15+ 默认因此终止进程。同时存在 project 上下文管理不一致、`close` 语义与 stage/status 解耦等问题，影响 M1 基础设施层的可靠性。

## What Changes

- 为 server 进程添加全局 `unhandledRejection` / `uncaughtException` 兜底处理器，防止进程意外退出
- 修复 agent 后台 promise catch 块中 `stateManager.updateIssueStatus()` 自身抛异常导致未处理 rejection 的问题
- `close` handler 增加对运行中 agent 的协调检查，避免竞态
- `start` handler 增加对 `blocked` status 的前置校验（当前只检查 stage）
- 统一 project 上下文管理：`project use` 失败时保留旧上下文（切换失败 ≠ 取消选择），API 无 project 上下文时拒绝创建 issue
- 消除 `ProjectService` 与 `StateManager` 的双重 currentProjectId 状态

## Capabilities

### New Capabilities

- `error-resilience`: server 全局错误处理兜底，防止未捕获异常/rejection 导致进程崩溃

### Modified Capabilities

- `server-daemon`: 增加进程级错误兜底、agent 生命周期协调
- `http-api`: close handler 增加 agent 竞态检查、start handler 增加 blocked status 校验、无 project 上下文时拒绝 issue 创建
- `project-management`: `project use` 失败时保留旧上下文，API 层在无有效 project 上下文时拒绝操作

## Impact

- `packages/cli/src/server/index.ts`: 添加 process 事件监听
- `packages/cli/src/api/issues.ts`: close/start handler 逻辑变更
- `packages/cli/src/api/projects.ts`: use handler 失败时保留旧上下文不变（无需改动）
- `packages/cli/src/services/project-service.ts`: 移除内存 currentProjectId 字段，统一通过 configRepo
- `packages/cli/src/server/state-manager.ts`: 可能需要增加 clearCurrentProject 调用入口
