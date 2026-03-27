## Why

当前 mohist 的工作流组件（TaskQueue、StageHandlers、AgentRunner）已各自实现，但缺少将它们连接起来的 WorkflowEngine。任务入队后没有任何 Worker 去执行，Agent 永远不会被 spawn。此外，多个 Issue 并行处理时需要 Git 隔离，当前没有任何分支管理机制。

## What Changes

- 新增 WorkflowEngine：多 Worker 模式的工作流执行引擎，连接 TaskQueue → StageHandlers → AgentRunner
- 新增 WorktreeManager：基于 git worktree 的隔离工作区管理，每个 Issue 在独立目录中执行
- 新增本地合并流程：Issue 完成后在 CLI 端执行 git merge 合并到主分支，清理 worktree
- 替换内存 TaskQueue 为基于 TaskRepo 的持久化队列
- 修复 Task.issueId 映射 bug（当前 rowToTask 写死为 0）
- 新增 CLI 命令：`mo issue diff <number>` 查看变更，`mo issue logs <number>` 查看日志

## Capabilities

### New Capabilities

- `workflow-engine`: 多 Worker 并行工作流执行引擎，负责轮询任务队列、分发执行、处理完成/失败
- `worktree-manager`: git worktree 生命周期管理（创建、移除、列出），每个 Issue 对应一个隔离工作区
- `local-merge`: 本地合并流程，CLI 端执行 git merge 将 worktree 分支合并回主分支并清理

### Modified Capabilities

- `issue-workflow`: 合并流程从 GitHub PR 变更为本地 git merge，approve 时 CLI 端执行合并
- `agent-runner`: Agent cwd 从项目根目录变更为 worktree 路径，日志写入 issue 级别目录
- `server-daemon`: Server 启动时启动 WorkflowEngine，停止时优雅 drain

## Impact

- **新增文件**: `src/workflow/engine.ts`, `src/git/worktree-manager.ts`
- **修改文件**: `src/server/index.ts`（集成 Engine）, `src/agent/runner.ts`（cwd 改为 worktree）, `src/server/task-queue.ts`（删除）, `src/db/task-repo.ts`（修复 issueId 映射、新增 findAndClaim）, `src/types/index.ts`（Task.issueId 替换 issueNumber）, `src/workflow/stage-handlers.ts`（传入 worktree 路径）, `src/api/issues.ts`（用 StateManager.createTask() 替换 TaskQueue、新增 cleanup 路由、issue 详情返回 projectPath）, `src/api/status.ts`（用 TaskRepo 替换 TaskQueue）, `src/cli/commands/issue.ts`（approve 本地 merge、新增 diff/logs 命令）
- **数据库**: tasks 表无需 schema 变更，但需确保 issue_id 正确映射到 Task.issueId
- **文件系统**: `~/.mohist/projects/{name}/worktrees/` 新增 worktree 目录，`~/.mohist/projects/{name}/logs/` 新增日志目录（按项目隔离）
- **CLI**: 新增 `mo issue diff`, `mo issue logs` 命令
- **外部依赖**: 需要 git 命令行工具
