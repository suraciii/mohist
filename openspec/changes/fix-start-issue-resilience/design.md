## Context

当前 `mo issue start` 的调用链：

```
POST /issues/:number/start
  → issues.ts:308   issueService.transitionToStage(Plan)  ← 先改状态
  → issues.ts:314   worktreeManager.create()               ← 后操作（可能失败）
    → worktree-manager.ts:50   smartFetch() → execFileAsync('git', ['fetch', 'origin'])
      → 网络抖动 → gnutls_handshake() failed → 异常冒泡
  → issues.ts:369   catch 块返回 500，但 stage 已经是 Plan，无法重新 start
```

`propose.ts` 的顺序是正确的（先 worktree 后 transition），说明这是 `issues.ts` 的实现疏忽。

`smartFetch` 本身也有三个弱点：无 try-catch、无重试、不降级。

## Goals / Non-Goals

**Goals:**

- `mo issue start` 在 `git fetch` 网络失败时能优雅降级（使用本地 remote-tracking ref 继续创建 worktree）
- `mo issue start` 的任何异常都不会导致 issue 卡在中间状态
- `smartFetch` 对瞬时网络问题有重试能力（3 次，指数退避）

**Non-Goals:**

- 不引入新的日志基础设施（用现有的 console.warn / error）
- 不改动 `propose.ts`（已经是正确的顺序）
- 不改动 `agentRunner.start()` 的异步执行模型
- 不解决 "git 未安装" 或 "project 不存在" 等前置校验问题
- 不修复 `skip-to-review` handler 中的类似模式（本次只修复 `POST /:number/start`）
- 不处理 `agentRunner.start()` 返回的 `error` 字段（现有 handler 忽略该字段，但本次不覆盖）

## Decisions

### Decision 1: smartFetch 重试 + 降级

**选择**: smartFetch 失败后静默继续，不抛异常

**理由**: `git fetch origin` 的目的是刷新 `origin/main` 引用，让新 worktree 基于最新代码。但即使 fetch 失败，本地已有的 `origin/main`（来自之前的 fetch）仍然可用。worktree create 后续会检查 `origin/main` 是否存在，如果连本地都没有，会给出清晰的 "Branch not found" 错误。

**重试策略**: 3 次尝试（首次 + 2 次重试），间隔 1s → 2s 指数退避。足以覆盖大多数瞬时网络抖动。

**降级行为**: 3 次都失败后，打印 `console.warn` 并继续。不写 fetch cache（下次 start 会再次尝试 fetch）。

**替代方案**:
- 直接抛异常让调用方处理 → 太复杂，每个调用点都要处理
- 只重试不降级 → 重试也可能全部失败，最终还是崩溃

### Decision 2: start handler 调整操作顺序

**选择**: 对齐 `propose.ts` 的模式——先操作后改状态

**新顺序**:
```
1. worktreeManager.create()     ← 可能抛异常，此时 stage=Draft，安全
2. transitionToStage(Plan)      ← 到这里说明 worktree 已就绪
3. agentRunner.start()          ← 异步，startResult.started 或 queued
4. catch: 如果 stage 已改为 Plan，rollback 回 Draft
```

**理由**: 这个模式已被 `propose.ts` 验证过。如果 worktree 创建失败，stage 还没改，用户可以直接重试 `mo issue start`。

### Decision 3: catch 块 rollback stage

**选择**: 在 catch 块中检查并 rollback stage

**实现**: catch 块中，如果检测到 stage 已经被改为 `Plan`，调用 `issueService.transitionToStage(issue.id, Stage.Draft)`。

**理由**: 调整顺序后，worktree 创建失败不会导致不一致。但 `agentRunner` 未配置的检查如果仍在 transition 之后，会留下同样的问题。另外，`agentRunner.start()` 本身不抛异常，而是返回结果对象，因此 catch 块实际上主要覆盖两类剩余风险：
1. `worktreeManager.create()` 失败（如网络或分支不存在）
2. `!agentRunner` 等前置检查被错误地放在了 transition 之后

**不 rollback worktree**: 如果 worktree 创建成功但后续失败，保留 worktree。理由：worktree 是幂等的（`exists()` 检查），下次 start 会复用。清理 worktree 可能丢失用户数据。

## Risks / Trade-offs

**[Risk] fetch 降级后基于过时的 origin/main 创建 worktree** → 缓解：这等价于离线开发。用户可以后续 `git pull` 更新。文档中说明。但如果仓库非常活跃，agent 可能基于明显过时的上下文做决策，导致 token 浪费。当前方案只打印 server 端 warn，不会通知 CLI 用户。

**[Risk] rollback stage 失败（如 SQLite 锁）** → 缓解：rollback 失败时 log error 但不抛新异常，避免吞掉原始错误。

**[Risk] worktree 存在但 stage 被 rollback 到 Draft** → 缓解：worktreeManager.create() 开头检查 `exists()` 并直接返回，不会有副作用。

**[Risk] `skip-to-review` handler 存在相同模式** → 缓解：`skip-to-review` 逻辑更简单（不启动 agent），风险较低，本次不修复。
