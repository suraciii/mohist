## Why

`mohist/pr` 把 PR 创建压在 integrate 最后一刻，整个 run 在集成前没有可见的 GitHub 集成容器；同时 `merge-pull-request` 直接调用 `gh pr merge`，不尊重 GitHub PR checks，pending 时会提前 merge、failed 时仍尝试合并。用户希望 PR 在 plan 批准后尽早出现，作为整个 run 的集成载体，后续 stage 改动通过显式 task 推送到同一 PR；集成以 checks-gated merge 收尾。现在做是因为 prerequisite #190 已落地 `create/merge-pull-request` action 与 `mohist/pr` profile 骨架，剩下的是把 PR 前移并补齐 checks 门禁。

## What Changes

- 重排 `mohist/pr` profile 为 PR-first 形态：plan 批准后通过显式 `mohist/create-pull-request` task 创建/更新 PR；build、check 等 stage 在需要同步远端时，于该 stage task 尾部声明显式 update PR task（同一 head/base，复用 open PR）；integrate 收尾只剩 `mohist/merge-pull-request`。
- `mohist/create-pull-request` 输出稳定 PR 身份，profile 通过 `setVars` 写入 `vars.github.pr.number` / `vars.github.pr.url`，供后续 update/merge task 引用。
- `mohist/merge-pull-request` 在真正 merge 前等待 GitHub PR checks：`pending` 持续等待不提前 merge；`passed/skipped` 后执行 `gh pr merge --squash`；`failed/cancelled/action_required` 以 action-owned JSON failure 失败，output 至少含 `errorCode: pr-checks-failed`、`prNumber`、`prUrl`、人读 `message`。
- merge 成功后确认 PR `state=MERGED` 才视为集成完成。
- `pr-checks-failed` 当前不触发 auto-fix recovery；保留普通 task failure，用户修复后 retry/rerun。**BREAKING**（内部）：`mergeOrConfirmPr` 不再无条件直接 merge，新增 checks 等待前置阶段。
- 保留现有 `base-moved` recovery：`rebase -> create-pull-request -> merge-pull-request`，复用同一 workflow branch 与 open PR。
- 不新增 stage hook、隐藏 stage boundary side effect、workflow finalize task；不把 PR checks 建模为 stage-level check；不改变 workflow engine 对具体 action error code 的无感边界。

## Capabilities

### New Capabilities

- `pr-first-workflow`: `mohist/pr` profile 的 PR-first 执行契约——PR 在 plan 批准后由显式 task 创建并作为整个 run 的集成容器，stage 尾部按需显式 update PR，integrate 由 `merge-pull-request` 收尾；`merge-pull-request` 在 merge 前等待 GitHub PR checks（pending 等待 / passed-skipped 合并 / failed-cancelled-action_required 失败），`pr-checks-failed` 为不自动修复的普通 task failure；PR 身份经 `setVars` 投影为 `vars.github.pr.number` / `vars.github.pr.url`；`base-moved` 经 `rebase -> create-pull-request -> merge-pull-request` 恢复。

### Modified Capabilities

无。既有 spec 均不约束 `mohist/pr` profile 的执行形态或 PR action 的 checks 行为，本次从零建立该契约。

## Impact

- **Profile**：`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-pr.workflow.yaml`——重排 task graph，PR 创建前移、stage 尾部加 update PR task、integrate 收敛为 merge。
- **Runner action**：`packages/runner/src/actions/pull-request.ts` 与 `publish-via-pr.ts` 的 `mergeOrConfirmPr`——新增 PR checks 等待前置与 `pr-checks-failed` 失败路径。
- **Profile 测试**：`packages/server/tests/.../MohistPrIssueWorkflowProfileSpecs.cs`、`MohistDefaultWorkflowProfileSpecs.cs`——更新对 PR-first task 顺序与 integrate 收尾的断言。
- **Runner 测试**：`packages/runner/tests/pull-request.spec.ts`——新增 checks pending 等待、passed/skipped 合并、failed/cancelled/action_required 失败用例。
- **文档**：`design/workflow/builtin-workflows.md`、`design/workflow/actions.md`、`docs/workflow-profiles.md`——把 PR-first 与 checks-gated merge 从"目标态"改为"现行态"。
- **Web**：`packages/web` 中识别 `create/merge-pull-request` 的交付指示器逻辑无需语义变更（仍按 `uses`/`kind` 识别），需回归验证 PR 前移后指示器在 build/check stage 仍正确显示。
- 无对外 HTTP API、数据模型或存储格式变化；不接入 GitHub Actions/CI，不同步 GitHub issue，不删除远端 head branch。
