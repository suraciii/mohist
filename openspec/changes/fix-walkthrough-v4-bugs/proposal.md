## Why

E2E walkthrough v4 发现 4 个严重 bug 阻塞完整 pipeline。Build 阶段完成时 task 状态误判（tasks.json 全 passes=True 但报 "1 failed task"），stage 非法回退到 draft，agent 超时未生效（单进程 40+ 分钟），以及 `resume --skip-to-review` 导致死锁。这些 bug 导致 issue 无法到达 done 状态。

## What Changes

- 修复 ralph-executor 中 `failed` 计数器在 auto-skip 前递增导致 build 误判失败的逻辑
- 修复 build 失败后 stage 回退到 Draft 的问题（保持当前 stage）
- 将 workflow 中已定义但未传递的 stage timeout 传入 ACP session runner
- 修复 skip-to-review 不创建审批门禁导致死锁的问题
- 修复 reopen 命令帮助文本和 pending gate 丢失问题

## Capabilities

### New Capabilities

- `build-stage-completion`: build 阶段完成时正确判定 task 成功/失败的逻辑，包括 auto-skip 场景

### Modified Capabilities

- `workflow-stage-management`: stage 回退策略改为保持当前 stage 而非回退到 draft
- `agent-timeout`: stage timeout 从 workflow 配置传递到 ACP session runner

## Impact

- `packages/cli/src/openspec/ralph-executor.ts` — failed 计数器逻辑
- `packages/cli/src/workflow/workflow-controller.ts` — result.success 判定
- `packages/cli/src/services/agent-runner-service.ts` — stage 回退逻辑、orphan recovery
- `packages/cli/src/openspec/ralph-executor.ts` — timeout 传递
- `packages/cli/src/agent-runtime/acp-session.ts` — timeout 接受外部值
- `packages/cli/src/api/issues.ts` — skip-to-review 补全审批门禁
- `packages/cli/src/cli/commands/issue.ts` — reopen 帮助文本
