## Why

`mo issue start` 在 `git fetch origin` 因网络抖动失败时（如 gnutls_handshake 错误），整个流程崩溃，但 issue 的 stage 已被改为 `Plan`，导致 issue 卡在不一致的状态——没有 worktree，没有 agent，却不再是 `Draft`，用户无法重新 start。

## What Changes

- **smartFetch 增加重试和降级**：`git fetch origin` 失败时重试 2-3 次（指数退避），全部失败后静默降级，使用本地已有的 remote-tracking 分支继续创建 worktree
- **start 流程调整操作顺序**：先创建 worktree（可能失败），成功后再 transition stage，确保任何失败都不会留下不一致状态
- **start 流程 catch 块增加 rollback**：如果 stage 已改但后续步骤失败，rollback stage 回 `Draft`

## Capabilities

### New Capabilities

（无新能力）

### Modified Capabilities

- `worktree-manager`：smartFetch 增加重试和优雅降级行为
- `http-api`：`POST /issues/:number/start` 的操作顺序和错误恢复逻辑

## Impact

- `packages/cli/src/git/worktree-manager.ts`：smartFetch 函数重写
- `packages/cli/src/api/issues.ts`：`POST /:number/start` handler 重构
- `packages/cli/src/api/propose.ts`：无需改动（已是正确顺序），但 worktreeManager 改动会间接影响这里
