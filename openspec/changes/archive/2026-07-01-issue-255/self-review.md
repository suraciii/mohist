# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `design.md` 的 Open Questions 把 executor 侧 `safeParseJson` 的保留理由归给 `captureAndUploadArtifacts`（声称现状确实用，executor.ts:808/832/971），但逐行核对源码后这三处分别位于 `dirtyWorktreeFailure`(808) / `gitIndexLockFailure`(832) / `attachBranchStabilityEvidence`(971)——全部是**迁出** executor 的不变式函数，而非保留的 `captureAndUploadArtifacts`。`captureAndUploadArtifacts` 实际用的是内联 `error instanceof Error ? error.message : String(error)`，不调 `safeParseJson`。executor 侧真正保留 `safeParseJson` 的是 `tryRecovery`（executor.ts:1017），保留 `errorMessage` 的是 `executeOne` 外层 catch。原结论"保留在 executor"方向正确，但前提归因错误，会误导实现者。已更正 Open Question 文案：把保留理由改为 `tryRecovery` / `executeOne` catch，并补充 `isNotFoundError` 仅工作树清洁路径使用、随 `enforceCleanWorktree` 迁入。
  Verification: `grep safeParseJson packages/runner/src/runtime/executor.ts` 确认 5 处命中：808/832/971（迁出）、884（定义）、1017（`tryRecovery`，保留）。`captureAndUploadArtifacts`(426-523) 内无 `safeParseJson` 调用。修正后结论与 T-001 notes（"keep in executor, have branch-stability import or self-supply"）一致。
  Status: resolved

## Blocking Items

_(none)_

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: completeness
  Evidence: T-001 实际覆盖两条 spec 需求——"分支稳定性不变式位于独立单一职责模块"（主引用）与"共享 git 探测注入点迁移且注入模式不变"（创建 `git-probe.ts` 并迁移 `setExecutorGitRunnerForTest`）。但 T-001 的 `spec` 字段只引用了前者。任务描述里确实写明了 git-probe.ts 的创建与桩迁移，行为无缺漏，仅 traceability 字段不全。
  SuggestedAction: 实现期可在 T-001 的 `spec` 字段补上 `specs/runner-executor-structure/spec.md#共享-git-探测注入点迁移且注入模式不变` 以提升可追踪性；不阻塞执行。
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: design Decision 5 称 `enforceCleanWorktree` 收三个新参 `cleanupAction` / `baseContext` 工厂 / `contextParts`，但 spec 与 proposal 的契约只列出 `(work, workDir, result, renderedWith, variables, signal, cleanupAction, contextParts)`——即 `baseContext` 工厂未作为独立位置参数出现在 spec 契约里。`runAgentCleanupAttempt`(executor.ts:308-335) 需要 `baseContext(work, variables, signal, sessionManager, acpConnection, connection)` 才能构造清理上下文。
  SuggestedAction: 实现期明确 `contextParts` 是否承载 `sessionManager`/`acpConnection`/`connection`（或一个预构建的 baseContext 工厂）。spec 的参数列表是行为契约的下限，可由实现补充；不阻塞。
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-003 标注 `type: "REVIEW"`，但其描述包含写操作——"Clean up any residual imports/dead code"、"If executor.ts still exceeds orchestration-entry scale ... move any remaining evidence/failure constructors that belong to an invariant into its module"。若工作流 REVIEW 模式限制文件写入，这些收尾改动将无法落地。
  SuggestedAction: 实现期确认 REVIEW 模式是否允许编辑；若不允许，将 T-003 改为 `type: "WRITE"`，或把写操作前移到 T-001/T-002。不阻塞，因任务内容已完整描述。
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: feasibility
  Evidence: proposal/design 给出 executor 收敛到 ~350 行的预算，但 1106 − 分支稳定性(~172) − 工作树清洁(~291) ≈ 640 行保留（含 recovery ~115 + 后置副作用 ~130 + 编排入口 ~80 + 工作区解析 ~80 + helper ~50 + 共享小工具），实际可能落在 500–650 区间。spec 的**绑定**判据是"scc 脱离 runner 包复杂度前列"（定性、可测），行数预算仅是次级指标，故不阻塞。
  SuggestedAction: 实现阶段以 `scc packages/runner/src/` 排名为准绳；若三模块均已脱离复杂度前列即视为达标，不必拘泥于 ~350 行字面值。
  Status: follow-up

## Summary

逐行核对 `executor.ts`(1106 行) 与 `worktree-cleanup.ts`(99 行) 后确认：design.md 的关键事实（三个 mutable-let 注入点 executor.ts:42-56、`git` 共用于 `readCurrentBranch`(924) + `readWorktreeSnapshot`(735) + `recoverStaleIndexLock`(667)、`WorktreeProbeError` 抛出点 747/759 与捕获点 159-161、4 个 spec 文件导入三个桩、仅 `runtime/host.ts` 导入 `WorkExecutor`、`worktree-cleanup.ts` 的四个纯数据导出）全部属实。issue 的 6 条 Acceptance Criteria 与 5 条 Non-Goals 被 spec 的 6 条 Requirement 与 3 个 task 完整覆盖；任务粒度合理（3 个完整功能切面，无"定义接口/注册DI/创建文件"式过细任务，无独立测试任务）；依赖链 T-001 → T-002 → T-003 无环且 `dependsOn` 指向存在且更低优先级。proposal Capabilities（`runner-executor-structure`）、spec、tasks 三者命名与符号清单一致。仅 1 处 design Open Question 的事实归因错误已修复，其余 4 项为非阻塞 follow-up。

<promise>PASS</promise>
