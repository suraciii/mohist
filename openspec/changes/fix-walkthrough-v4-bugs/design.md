## Context

E2E walkthrough v4 在容器中走完整 pipeline，发现 4 个严重 bug 全部阻塞了 build→review 转换。根因已通过源码分析精确定位：

1. **ralph-executor.ts:525** — `failed++` 在 auto-skip 决策前递增，auto-skip 后 `failed` 已无法回退
2. **agent-runner-service.ts:328** — 失败路径硬编码 `updateStage(issue.id, Stage.Draft)`，破坏单向 workflow
3. **ralph-executor.ts:430-440** — ACP session runner 调用未传 timeout，workflow-loader.ts:52 定义的 timeout 从未被消费
4. **issues.ts:665-666** — skip-to-review 只设 stage/status，不创建审批门禁也不启动 agent

## Goals / Non-Goals

**Goals:**
- Build 阶段完成后，tasks.json 全 passes=True 时正确报告成功
- Stage 单向推进，失败时保持当前 stage + status=blocked
- Stage timeout 从 workflow 配置正确传递到 ACP session
- skip-to-review 创建完整审批门禁
- reopen 帮助文本准确反映实际行为

**Non-Goals:**
- 不改变 workflow 阶段定义（draft/plan/build/review/done）
- 不解决 agent 不提交 git 的问题（独立问题）
- 不解决 volume 权限问题（容器环境问题）

## Decisions

### D1: failed 计数器修复 — 移除无条件递增

**Decision**: 移除 line 525 的无条件 `failed++`，改为只在最终 abort 路径递增。auto-skip、user-skip、retry 均不递增 failed。

**Rationale**: 当前代码在 line 525 无条件递增 failed，但后续有三种可能：
1. auto-skip（line 559-562）— 不应计入失败
2. user-skip（line 539-542）— 不应计入失败  
3. retry（line 543）— 可能最终成功，不应计入失败
4. abort（line 546-557）— 这才是真正的失败

修复方式：完全移除 line 525 的 `failed++`，将递增移到 abort 分支（line 546 之后）。同时修复 retry 场景下的 taskResults 去重：retry 成功时，之前 push 的 `failed` entry 需要被替换为 `completed`。

**Alternative**: 用 `failed--` 在 skip 时回退。更危险，如果有其他代码路径读取了中间值会出问题。

### D2: Stage 保持 — 不回退 + Reopen 自动 Resume

**Decision**: 
1. 失败路径中移除 `updateStage(issue.id, Stage.Draft)`，只设 status=blocked
2. Reopen 保持当前 stage 并自动调用 `resumePipeline`，无需 `hasPendingGate` 检查

**Rationale**: 单向 workflow 中 stage 表示进度，回退会丢失进度信息。但保持 stage 后必须解决"如何重试"的问题 — `mo issue start` 拒绝非 draft issue。最简洁的方案是 reopen 直接 resume：
- `resumePipeline` → `executePipeline` → `WorkflowController.run` 会根据 `issue.stage` 自动定位
- `findNextPendingTask` 会自动跳过已完成 task
- 无需用户手动 start

**Alternative A**: 让 `start` 命令支持非 draft。但用户可能意外重新启动已有产物的 stage。
**Alternative B**: 新增 `mo issue retry` 命令。增加 API 复杂度。
**Alternative C**: 保持 reopen reset-to-draft 不变。破坏单向 workflow 语义。

### D3: Stage timeout 传递

**Decision**: 在 `_acpSessionRunner` 调用时传入 timeout 参数，从 workflow 配置中获取 stage 级别的总 timeout，按剩余 task 数平均分配。

**Rationale**: workflow-loader.ts 已定义 `timeout: 1800`（30 分钟）但从未使用。最简方案是将 stage timeout 除以 task 数作为每个 task 的 timeout，传入 ACP session。

**Alternative**: 使用全局 stage timeout 包装整个 ralphLoop。更正确但改动更大，适合后续优化。

### D4: skip-to-review 创建审批门禁

**Decision**: skip-to-review 端点中设置 approvalState(status: 'awaiting') 并发送 approval_requested 事件。pendingGate 注册是可选的（approve 端点有 fallback 检查 approvalState）。

**Rationale**: 正常 review 流程（workflow-controller.ts:328-338）包含这些步骤。approve 端点（line 734-743）对缺少 pendingGate 有 fallback：检查 `approvalState.status === 'awaiting'`。所以只要设置 approvalState 即可。

### D5: Orphan recovery 保持 stage 并恢复 pipeline

**Decision**: orphan recovery（server 启动时恢复残留 issue）改为保持当前 stage + status=blocked，不再回退到 draft。同时恢复 pendingGate（已有逻辑，awaiting approvalState 时恢复）。

**Rationale**: 与 D2 一致，server 重启后也应保持进度。对于非 awaiting 状态的 orphan（如 build 失败后重启），改为 blocked + 当前 stage，让用户可以 reopen resume。

## Risks / Trade-offs

- [Risk] 修改 failed 计数器可能影响其他依赖该计数器的逻辑 → Mitigation: 仔细检查 taskResults 数组的使用者，确保 skipped 状态被正确处理。同时修复 retry 成功时的 taskResults 去重
- [Risk] Reopen 自动 resume 可能在 worktree 不存在时失败 → Mitigation: worktreeManager.getPath 在 reopen 前已检查，resumePipeline 会重新创建缺失的 worktree
- [Risk] Timeout 固定分配（stageTimeout / totalTasks）可能导致长 task 被提前终止 → Mitigation: 设置 floor（如 5 分钟最小值），避免极端情况
- [Risk] Orphan recovery 改为 blocked + currentStage 后，用户可能不知道 issue 已经失败 → Mitigation: approvalState 保留 error 信息，issue show 会显示
