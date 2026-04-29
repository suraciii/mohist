## Context

当前 Review 阶段审批后，approve API 直接设 `stage=Done`，mergeBack 在 `server/index.ts` 的 `agent_completed` 事件中异步执行。这导致：
1. Done 列中 issue 可能 merge 失败
2. 冲突解决时 issue 在 Done→Build 间反复横跳
3. Done 不是真正的终端状态

关键代码路径：
- `api/issues.ts:886-888` — approve handler 设 `nextStage = Done`
- `workflow-controller.ts:360-394` — Review handler 无已审批分支，Resolving 时直接跳到 Done
- `server/index.ts:161-243` — `agent_completed` 事件执行 mergeBack + 冲突解决
- `WorkflowController` 不持有 `worktreeManager` 引用，无法自行 merge

## Goals / Non-Goals

**Goals:**
- Review 阶段两段式：review agent → approval gate → mergeBack → Done
- Done = merge 成功，真正的终端状态
- mergeBack 失败→冲突解决→重试 mergeBack 的路径保持在 pipeline 内

**Non-Goals:**
- 不改变 Stage 枚举或新增 stage
- 不改变冲突解决的 agent prompt 或重试次数上限（3次）
- 不改变 worktree 管理本身的实现

## Decisions

### D1: WorkflowController 注入 mergeBack 能力

`WorkflowController` 新增可选 `mergeBackFn` 回调参数。`AgentRunnerService.executePipeline` 在创建 controller 时注入绑定了 project 上下文的 mergeBack 函数。

**理由**: WorkflowController 当前无 WorktreeManager 引用，直接传入整个 manager 会引入过多依赖。回调方式让 controller 只需关心 mergeBack 的成功/失败结果，project path、baseBranch 等上下文由注入方绑定。

**Alternatives considered:**
- 传入整个 WorktreeManager + project 信息 → controller 需要了解 git 概念，职责膨胀
- 通过 EventBus 发事件触发 merge → 异步，难以在 pipeline while 循环中同步等待结果
- mergeBack 放在 AgentRunnerService 层而非 controller → controller 无法控制 merge 后的 stage 转换

### D2: Review handler 三分支结构

Review handler `case Stage.Review` 改为三分支：

```
1. approvalState.status === 'approved' → 执行 mergeBack → 成功则 Done，失败则冲突解决路径
2. mergeState === Resolving → 跳过 review agent 和审批 → 直接执行 mergeBack
3. 默认 → 执行 review agent → 设 approval gate → return
```

**理由**: 分支 1 和 2 的区别在于：正常审批后的 mergeBack 失败需触发冲突解决（reverse merge + agent）；Resolving 后的 mergeBack 重试只需再次尝试合并或 Blocked。两者都跳过 review agent 和审批，但失败处理不同。

### D3: approve API 不设 nextStage

`api/issues.ts` approve handler 中，Review 阶段审批只设 `approvalState.status = 'approved'`，不设 `nextStage = Done`，不调用 `updateStage`。issue 保持在 Review 阶段。`resumePipeline` 调用后 WorkflowController 的 while 循环再次进入 `case Stage.Review`，命中已审批分支。

**理由**: 最小化 approve handler 的职责——只改 approvalState，stage 转换由 pipeline 自主控制。

### D4: agent_completed 事件移除 merge 逻辑

`server/index.ts` 的 `agent_completed` handler 移除全部 mergeBack 相关代码。mergeBack 在 pipeline 内由 WorkflowController 执行。冲突解决（reverse merge + re-run pipeline）也在 controller 内触发。

**理由**: 消除 mergeBack 作为"pipeline 外异步操作"的不一致性。所有与 stage 转换相关的操作统一在 controller 内。

### D5: 冲突解决路径复用现有机制

mergeBack 失败后的冲突解决路径复用现有逻辑（`mergeMasterInWorktree` + 回退到 Build + `mergeState=Resolving`），但从 controller 内触发。具体方式：controller 调用 `mergeBackFn`，失败时调用新增的 `onMergeConflict` 回调，由 AgentRunnerService 执行 reverse merge + 重新 startPipeline。

**理由**: 冲突解决涉及 agent 重启、worktree 操作等重逻辑，不适合全部塞进 controller。回调方式让 controller 保持轻量。

## Risks / Trade-offs

- **[Risk] Pipeline 恢复（server 重启）后"已审批待 merge"状态丢失** → Mitigation: `approvalState.status = 'approved'` 持久化在 DB 中，server 重启后 `recoverIssues()` 检测到 `stage=Review + approvalState.status=approved` 时直接 resume pipeline，controller 进入已审批分支执行 mergeBack
- **[Risk] mergeBack 在 pipeline 同步执行可能阻塞 agent slot** → Mitigation: mergeBack 是 git 操作（通常 <5s），远短于 agent 运行时间，影响可忽略
- **[Risk] 回调注入增加了 controller 和 runner 之间的耦合** → Mitigation: 耦合仅限于 mergeBack 函数签名（`(issueNumber: number) => Promise<{success, message}>`），controller 不知道 WorktreeManager 的存在

## Migration Plan

1. 先改 `WorkflowControllerOptions` 新增 `mergeBackFn` 和 `onMergeConflictFn`（可选，向后兼容）
2. 改 `WorkflowController.run()` 的 Review handler 为三分支
3. 改 `api/issues.ts` approve handler 移除 `nextStage = Done`
4. 改 `AgentRunnerService.executePipeline` 注入 merge 回调
5. 最后移除 `server/index.ts` 中 `agent_completed` 的 merge 逻辑
6. 测试：正常审批→merge→Done、审批→merge 失败→冲突解决→重试→Done、server 重启恢复

无需数据库 migration，`approvalState` 字段已支持 `approved` 状态。

## Open Questions

无。
