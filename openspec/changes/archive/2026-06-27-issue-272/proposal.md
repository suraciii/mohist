## Why

当 issue 的某个 stage 失败后（尤其是 integrate 阶段的 rebase 冲突），workspace 会留下脏状态——in-progress rebase、未提交改动、detached HEAD 等。`rerun` 重新跑 stage 时，新 task 从脏 workspace 开始，立刻在 git 操作上报错或带着冲突残留执行，自治流程中断，用户不得不手动 `git rebase --abort && git reset --hard` 才能恢复。Runner 目前只在 task 级别做事后校验（`enforceCleanWorktree`）与每次 dispatch 时跑隐式 `runHealthGate`，但二者都是执行面基础设施、对 profile 不可见且不携带诊断输出，缺少一个在 **stage 边界** 显式、幂等、可诊断的 workspace 准备动作。

## What Changes

- 新增 runner action `mohist/workspace-prepare`，在每个 stage 开始时清理 workspace git 状态并对齐预期分支：
  - abort 残留 rebase / merge / cherry-pick（如存在）。
  - 若不在预期分支则 `git checkout <workspace.branch>`。
  - `git reset --hard HEAD` 丢弃未提交改动，`git clean -fd` 清理未跟踪文件。
  - 健康校验：确认 HEAD 在预期分支、工作区干净、无残留 `.git/rebase-merge` / `.git/rebase-apply`。
  - workspace 已干净时快速通过（< 1s，无副作用）。
- 任何步骤失败时返回清晰诊断（失败步骤、当前 HEAD、预期分支、residual-state 探测结果），供 rerun / 用户据此从头来过。
- 在 `mohist/local` 与 `mohist/github-pr` 两个 workflow profile 的每个 stage 的 task 列表 **最前面** 显式注入 `mohist/workspace-prepare` 作为第一个 task。
- workspace-prepare 只在 stage 初始化时执行一次，不拦截每个 task，不干扰 recovery task 注入（`onFailure` / check repair）路径。

## Capabilities

### New Capabilities

- `workspace-prepare`: `mohist/workspace-prepare` runner action 的行为契约——输入（workspace path / branch）、幂等清理语义（abort residual ops、checkout 预期分支、reset --hard、clean -fd、健康校验）、已干净时的 fast-pass、以及失败诊断输出（failureKind + 当前状态描述）。

### Modified Capabilities

（无。`pr-first-workflow` 的 "无隐藏 stage 边界副作用" 约束要求 PR 相关副作用必须显式出现在 task graph 里——把 `mohist/workspace-prepare` 作为显式首个 task 正是遵守而非违反该约束，故其 requirements 不变。`runner-workspace-cleanup` 治理的是 workspace 的终态后驱逐/保留生命周期，与 stage 边界的 git 状态准备属不同关注点，不受影响。）

## Impact

- **Runner**：`packages/runner/src/actions/workspace-prepare.ts`（新 action，复用 `git.ts` wrapper 与 `rebase.ts` 中 abort/reset 辅助逻辑）；`packages/runner/src/actions/registry.ts` 注册 `mohist/workspace-prepare`。
- **Server（profile 定义）**：`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-local.workflow.yaml` 与 `mohist-github-pr.workflow.yaml` 在每个 stage 的 `tasks` 列表头部新增 `mohist/workspace-prepare` task。
- **与既有运行时机制的关系**：`WorkspaceManager.runHealthGate` / `reenterRunBranch`（`workspace.ts`）与 `enforceCleanWorktree`（`executor.ts`）保持不变——前者仍作每次 dispatch 的隐式兜底，后者仍管 task 完成后的 worktree 清洁；workspace-prepare 补的是 stage 边界这一层，是显式、可诊断、对 profile 可见的。具体去重/分层策略留待 design.md。
- **Web**：`packages/web/src/shared/lib/delivery-failure.ts` 视是否需要为 workspace-prepare 失败新增 UI 可识别的 failure kind 而定（可复用现有 `workspace-setup` / `retry-safe`）。
- **测试**：runner 侧新增 `workspace-prepare.spec.ts`（幂等 fast-pass、各 residual-state 清理、失败诊断三组场景）；profile YAML 变更由现有 profile 解析测试覆盖。
- **依赖/构建**：无新增依赖。
