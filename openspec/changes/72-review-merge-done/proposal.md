## Why

审批 Review 后 API 立即设 `stage=Done`，mergeBack 是 `agent_completed` 事件中的异步操作。导致 Done 列的 issue 可能 merge 失败、冲突解决时在 Done/Build 间反复横跳、Done 列里的 issue 还需要后续操作。mergeBack 应在 Review 阶段内完成，Done 应是真正的终端状态。

## What Changes

- Review handler 增加已审批分支：当 `approvalState.status === 'approved'` 且 `stage === Review` 时，跳过 review agent 直接执行 mergeBack
- 审批 API（`POST /api/issues/:number/approve`）不再设 `nextStage = Done`，issue 停留在 Review 阶段
- `agent_completed` 事件处理移除 mergeBack 逻辑（merge 改为 pipeline 内同步操作）
- Resolving 状态（`mergeState=Resolving`）时跳过审批后执行 mergeBack 而非直接 Done
- mergeBack 成功后才 `setMergeState(Merged)` + `stage=Done`

## Capabilities

### New Capabilities

- `review-merge-flow`: Review 阶段两段式流程——前半段 review agent 审查设 approval gate，后半段审批通过后执行 mergeBack，成功才进 Done

### Modified Capabilities

- `pipeline-model`: CHECK stage（对应 Review）完成后的 stage 转换语义变更——approval 后不直接 Done，需先 mergeBack 成功
- `http-api`: approve 端点对 Review 阶段的行为变更——不再设 nextStage=Done，改为触发 pipeline resume 让 mergeBack 在 pipeline 内完成

## Impact

- **代码**: `workflow-controller.ts`（Review handler 增加已审批分支）、`api/issues.ts`（approve handler 移除 Done 转换）、`server/index.ts`（移除 `agent_completed` 中的 merge 逻辑）
- **行为**: Done 列只包含 merge 成功的 issue，不再有 merge 失败或冲突中的 issue
- **风险**: mergeBack 从异步变为 pipeline 内同步，需确保 pipeline 恢复路径（server 重启后）能正确识别"已审批待 merge"状态
