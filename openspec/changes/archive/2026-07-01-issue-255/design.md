## Context

Runner 的工作流执行器 `packages/runner/src/runtime/executor.ts`（1106 行）是 runner 包内与 ACP 适配器并列的最复杂文件。它把 8 个执行关注点捆在一个模块里，其中两个基于 git 探测的不变式系统（分支稳定性 ~180 行、工作树清洁 + 陈旧锁恢复 ~320 行）占了近一半行数与几乎全部圈复杂度（多分支决策树 `detached/nonGit/error/mismatch`、有界 agent 清理循环、`lsof` 探测 + 陈旧 git index 锁恢复状态机）。每次调整工作项调度或执行容错策略都要在这个大文件里反复横跳。

**当前状态（关键事实，已逐行核对源码）：**

- 模块级 mutable-let 注入点集中在 executor.ts:42-56：`cleanupAgentAction` / `git` / `lockHolderProbe` 三个 `let`，对应三个导出的 `setXxxForTest` 桩。**`git` 这个 `let` 被两套不变式共用**——`readCurrentBranch`（分支探测，executor.ts:924）与 `readWorktreeSnapshot` / `recoverStaleIndexLock`（工作树探测 + 锁恢复，executor.ts:667/735）都读同一个 `git`。这是提取时最容易出错的单点。
- `WorktreeProbeError`（executor.ts:779）由 `readWorktreeSnapshot` 抛出（executor.ts:747/759），被 `executeOne` 的外层 try/catch 捕获并转成 `worktreeProbeFailure`（executor.ts:159-161）。这是一个**跨函数的异常边界**——抛出方在工作树清洁路径里，捕获方在编排入口里。
- 纯数据模块 `worktree-cleanup.ts`（99 行，只做 prompt 构造 + 配置读取 + `WorktreeSnapshot` 类型，无 I/O）已经独立存在，执行器通过它拿 `isAgentBackedTask` / `resolveMaxCleanupAttempts` / `buildCleanupWith` / `WorktreeSnapshot`。
- 测试注入面有 4 个 spec 文件直接 import 这三个桩：`executor-branch-stability.spec.ts`、`executor-cleanup.spec.ts`、`workspace-prepare-workflow.spec.ts`、`issue-112-regression.spec.ts`。其中 `executor-cleanup.spec.ts` 与 `executor-branch-stability.spec.ts` 在 `afterEach` 里同时复位全部三个桩。
- recovery（`tryRecovery` 等）已有 `executor-recovery.spec.ts` 覆盖预算递减与 retry-self 展开；为满足复杂度门槛，恢复匹配逻辑可独立为 `runtime/recovery.ts`，执行器保持原管线位置委托调用。
- 唯一外部消费者是 `runtime/host.ts`（import `WorkExecutor`）。无 server/web/cli 依赖，无持久化、无 runner↔server 协议字段变化。

**约束：** 执行语义、容错/重试行为、工作分发契约完全不变；测试注入机制（mutable-let）保持，仅迁移位置；这是行为保持重构，无 breaking change。`design/testing.md` 已把 `setExecutorGitRunnerForTest` 列为 runner 端 git fake 的规范入口。

## Goals / Non-Goals

**Goals:**

- 把分支稳定性不变式提取为单一职责模块 `runtime/branch-stability.ts`，执行器降为单次委托调用。
- 把工作树清洁不变式（有界清理循环 + 陈旧锁恢复 + 证据构造）提取为 `runtime/worktree-enforcement.ts`，执行器降为单次委托调用。
- 让两个不变式模块共用**同一** git 探测注入点（不得拆成两份互不可见的私有 `let git`）。
- 三个测试注入桩迁移到归属模块，签名逐字不变，4 个 spec 仅改 import 路径即可通过。
- `executor.ts` 收敛到编排入口规模（~275 行），三个核心模块各自达到 scc complexity <= 40 且排名在前 20 名之外。
- `executeOne` 收敛为线性管线，每个不变式阶段是一次函数调用。

**Non-Goals:**

- 不改执行语义、任务调度规则、容错/重试策略、runner↔server 契约。
- 不改变 recovery 行为；恢复匹配可在有独立 spec 守护后提取为专门模块。
- 不合并 `worktree-cleanup.ts`（纯数据模块保持独立）。
- 不引入新依赖注入框架、不改 mutable-let 注入范式。
- 不做性能优化、不新增执行阶段或工作项类型。

## Decisions

### Decision 1：git 探测作为独立共享 helper `runtime/git-probe.ts`（~30 行），而非塞进某个不变式模块

两个不变式都依赖同一个 `git` runner（分支探测、工作树探测、锁恢复全走它）。spec 明确要求"二者 SHALL 共用同一共享可注入 helper，SHALL NOT 被拆成两份"。可选方案：

- **(A) 独立 `git-probe.ts`**：持有 `GitRunner` 类型、`let git = defaultGit`、`setExecutorGitRunnerForTest`、re-export `defaultGit`。两个不变式模块以 sibling 身份 import。← **采纳**
- (B) 把 git runner 放进 `branch-stability.ts`，`worktree-enforcement.ts` import 它。被否：让"分支稳定性"模块拥有"通用 git 命令注入"是错误的职责归属，且让两个不变式产生横向耦合，未来动一个会牵连另一个。
- (C) 各模块各持一份 `let git` + 各导出一个 setter。被否：直接违反 spec"共用同一注入点"，且测试只 patch 一份时另一份跑真 git → 真 `git init` 副作用、flaky。

**理由：** git 探测注入本身就是一个单一关注点（"用什么执行 git 命令"），独立成最小模块后，两个不变式保持正交，测试复位只需对一个模块调一次 setter。`GitRunner` 类型随之迁入，executor.ts 不再持有它。

### Decision 2：`WorktreeProbeError` 的捕获下沉进 `enforceCleanWorktree`，executor 外层 catch 不再引用它

当前 `WorktreeProbeError` 在 `readWorktreeSnapshot`（位于工作树清洁路径）抛出，却由 `executeOne` 的外层 try/catch（executor.ts:159）捕获并调 `worktreeProbeFailure`。提取 `enforceCleanWorktree` 整体到 `worktree-enforcement.ts` 后：

- **(A) 把 probe-error → failure 的映射下沉到 `enforceCleanWorktree` 内部**：该函数内部 try/catch `WorktreeProbeError`，直接返回 `worktreeProbeFailure(work, error)`。executor 外层 catch 移除 `instanceof WorktreeProbeError` 分支。← **采纳**
- (B) 保留现状：executor import `WorktreeProbeError` 做 `instanceof`、import `worktreeProbeFailure` 做映射。被否：让编排入口知道不变式模块的私有异常类型，是反向耦合，且 `executeOne` 的线性管线会被一个"只为某个子阶段服务的 catch 分支"污染。

**行为等价性：** `readWorktreeSnapshot` 只在 `enforceCleanWorktree` 内被调用（executor.ts:284/301），外层 try 块内除此之外没有 `WorktreeProbeError` 的其它抛出源（`readCurrentBranch` 把错误收进 `CurrentBranchResult.error`，不抛异常）。因此下沉后，同一探测失败仍然产生同一 `worktreeProbeFailure` 结果，逐行行为一致。`WorktreeProbeError` class 与 `worktreeProbeFailure` 都迁入 `worktree-enforcement.ts`，不再从 executor 导出。

### Decision 3：测试桩按归属迁移，**不**从 executor.ts 做 re-export 兼容垫片

| 桩 | 迁入 | 被哪些 spec import |
|---|---|---|
| `setExecutorGitRunnerForTest`（+ `GitRunner` 类型） | `git-probe.ts` | branch-stability / cleanup / workspace-prepare-workflow |
| `setCleanupAgentActionForTest`（+ `CleanupAgentAction` 类型） | `worktree-enforcement.ts` | branch-stability / cleanup / issue-112-regression |
| `setExecutorLockHolderProbeForTest`（+ `LockHolderProbe` 类型） | `worktree-enforcement.ts` | branch-stability / cleanup |

4 个 spec 文件改 import 路径（一处 import 拆成从两个模块分别 import）。可选方案：

- **(A) 直接改 spec 的 import 路径**：← **采纳**
- (B) 在 executor.ts 里 `export { setExecutorGitRunnerForTest } from "./git-probe.js"` 等做 re-export 兼容。被否：`design/testing.md` 禁止"新旧并存"，re-export 垫片会让人误以为 executor 仍是注入入口，掩盖真实归属，且无法满足 spec"每个桩 SHALL 位于其归属模块并从该模块导出"的可观测意图。桩签名逐字不变（spec 硬要求），所以 spec 端只动 import 来源、不动调用。

### Decision 4：分支稳定性模块的对外契约 = 无状态函数 + ok/violation 判别联合

`branch-stability.ts` 导出：`checkBranchStability`（编排入口唯一委托点）、`readCurrentBranch`、`expectedWorkspaceBranch`、`branchInvariantViolationFailure`、`attachBranchStabilityEvidence`、`branchStabilityToJson`，类型 `BranchStabilityEvidence` / `BranchInvariantViolationEvidence` / `CurrentBranchResult`。`checkBranchStability` 返回 `{ kind: "ok"; evidence } | { kind: "violation"; result }`——与现有 executor.ts:183-186 的返回类型逐字一致，执行器侧调用点（executor.ts:134-137/145-148）只换前缀 `this.checkBranchStability` → 模块函数，签名不动。

`attachBranchStabilityEvidence` 同时被 executor 的非 completed 分支（executor.ts:143）和 worktree 失败分支（executor.ts:152/154）调用，故必须导出供 executor import。这是 executor↔branch-stability 唯一的回流依赖，可接受（编排入口组装证据栈是它的职责）。

### Decision 5：工作树清洁模块导入 `worktree-cleanup.ts` 但不合并，`enforceCleanWorktree` 签名承接现有参数

`worktree-enforcement.ts` 导出：`enforceCleanWorktree`、`runAgentCleanupAttempt`、`readWorktreeSnapshot`、`recoverStaleIndexLock`、`defaultLockHolderProbe`、`resolveStaleIndexLockMs`、`dirtyWorktreeFailure`、`gitIndexLockFailure`、`formatDirtyWorktreeSummary`、`parseFileList`、`WorktreeProbeError`、类型 `DirtyWorktreeEvidence` / `GitIndexLockRecovery` / `WorktreeProbeError`、常量 `DEFAULT_STALE_INDEX_LOCK_MS`。

- 从 `worktree-cleanup.ts` import `isAgentBackedTask` / `resolveMaxCleanupAttempts` / `buildCleanupWith` / `WorktreeSnapshot`（纯数据，保持独立）。
- 从 `git-probe.ts` import `git`。
- `runAgentCleanupAttempt` 仍需要 `baseContext`（executor.ts:317-322）和 `cleanupAgentAction` 注入桩——这两个留在 executor。处理：`enforceCleanWorktree` 多收一个 `contextParts` / `cleanupAction` / `baseContext` 工厂参数（spec 已写明 `enforceCleanWorktree` 入参含 `cleanupAction, contextParts`），由 executor 在委托时传入。这样 `worktree-enforcement.ts` 不反向依赖 executor，`baseContext` 仍归编排入口所有。
- `mergeCleanupCount` 是纯函数、被清理路径与证据构造共用，随 `enforceCleanWorktree` 一起迁入 `worktree-enforcement.ts`。

### Decision 6：执行器收敛后保留的内容与 `executeOne` 线性管线

executor.ts 保留（~275 行）：`WorkExecutor.execute` / `executeOne` / `executeChecks`、`variables` / `prepareWorkspace` / `workspaceFromVariables` / `workspaceRoot` / `resolveWorkDir`、`captureDeclaredOutputs`、以及 `normalize` / `failure` / `baseContext` / `toCheckStatus` / `isCheck` / `resolveWorkspacePath` / `formatUnresolvedError` / `formatCheckUnresolvedError` / `workspaceSetupFailure` 等 helper。checks fan-out/裁决委托 `check-execution.ts`，post-side-effect glue 委托 `artifact-side-effects.ts` / `set-vars-apply.ts` / `output-capture.ts`，recovery 委托 `recovery.ts`，以满足明确的 scc 门槛且保持执行语义不变。

提取后 `executeOne` 管线（每段不变式 = 一次函数调用）：

```
resolve action → variables → render with → resolve workDir
  → checkBranchStability(start)           [branch-stability.ts]
  → action(...)
  → normalize
  → tryRecovery                           [recovery.ts]
  → checkBranchStability(end)             [branch-stability.ts]
  → enforceCleanWorktree(...)             [worktree-enforcement.ts，内部消化 WorktreeProbeError]
  → captureAndUploadArtifactsForWork(...) [artifact-side-effects.ts]
  → captureDeclaredOutputs(...)           [保留]
  → applySetVarsForWork(...)              [set-vars-apply.ts]
```

## Risks / Trade-offs

- **[Risk] git 注入点被误拆成两份 `let git`** → 测试只 patch 一份时，另一份跑真 `git init/commit`，产生磁盘副作用 + flaky。**Mitigation**：Decision 1 强制单一 `git-probe.ts`；CI 跑全套 executor spec 验证两套不变式共享同一注入；`afterEach` 里对 `git-probe` 的复位覆盖两个不变式。
- **[Risk] `WorktreeProbeError` 下沉后异常路径行为偏移** → 下沉若漏掉某个抛出点，探测失败会冒泡成 generic `failure` 而非结构化 `worktreeProbeFailure`。**Mitigation**：Decision 2 已核对 `readWorktreeSnapshot` 是 try 块内唯一抛出源；`executor-cleanup.spec.ts` 的 probe-failure 场景（git status 非 git 仓库以外的失败）作为回归守护。
- **[Risk] `enforceCleanWorktree` 入参膨胀** → 它要承接 `cleanupAction` / `baseContext` 工厂 / `contextParts`，签名变长，易传错顺序。**Mitigation**：保持参数为位置参数与现有调用点对齐（spec 要求签名语义不变），并在 executor 调用点保留与今天一致的实参顺序；类型系统兜底（`RenderedWorkItem` / `JsonObject` 等强类型）。
- **[Risk] 循环 import** → 若任一不变式模块反向 import executor 的 `baseContext`/helper。**Mitigation**：Decision 5 规定 executor 把 `baseContext`/`cleanupAction` 作为参数**传入** `enforceCleanWorktree`，worktree-enforcement 不 import executor；两个不变式模块互不 import，只共同 import `git-probe.ts` 与（仅 worktree）`worktree-cleanup.ts`。无环。
- **[Risk] scc 未达预期改善** → 若 recovery/check/post-side-effect 分支留太多在 executor。**Mitigation**：门槛明确为 `executor.ts` / `branch-stability.ts` / `worktree-enforcement.ts` complexity <= 40 且排名在前 20 名之外；提取后跑 `scc --by-file --sort complexity packages/runner/src` 记录证据。
- **[Trade-off] 4 个 spec 的 import 路径要改** → 测试代码动得比"纯内部重构"多一点。可接受：spec 文案与断言不动，只动 import 来源；这恰好让 spec 的依赖图反映真实模块归属。

## Migration Plan

无持久化数据、无协议字段、无外部消费者变化（仅 `runtime/host.ts` import `WorkExecutor`，其导入路径不变）。因此**无需数据迁移**；部署 = 一次 runner 构建，回滚 = `git revert`。建议按以下顺序提交（每步独立可编译、可测，便于二分定位）：

1. **提取 `git-probe.ts`**：把 `GitRunner` 类型、`let git`、`setExecutorGitRunnerForTest`、`defaultGit` import 从 executor 迁出；executor 改为 `import { git } from "./git-probe.js"`。跑 `executor-branch-stability` + `executor-cleanup` + `workspace-prepare-workflow` spec。
2. **提取 `branch-stability.ts`**：迁移 `checkBranchStability` / `readCurrentBranch` / `expectedWorkspaceBranch` / `branchInvariantViolationFailure` / `attachBranchStabilityEvidence` / `branchStabilityToJson` + 三类证据类型。executor 调用点换前缀。跑 `executor-branch-stability` spec。
3. **提取 `worktree-enforcement.ts`**：迁移清理循环 + 陈旧锁恢复 + 证据构造 + `WorktreeProbeError` + `DEFAULT_STALE_INDEX_LOCK_MS` + `mergeCleanupCount`；按 Decision 2 把 probe-error 捕获下沉进 `enforceCleanWorktree`；按 Decision 5 接收 `cleanupAction`/`baseContext`/`contextParts` 参数；import `worktree-cleanup.ts` 与 `git-probe.ts`。跑 `executor-cleanup` + `issue-112-regression` spec。
4. **提取执行器剩余高分支 helper**：checks 委托 `check-execution.ts`，post-side-effect glue 委托 `artifact-side-effects.ts` / `set-vars-apply.ts`，recovery 委托 `recovery.ts`，并用 `executor-recovery.spec.ts` 守护恢复路径。
5. **更新 4 个 spec 的 import 路径**（Decision 3），改完跑全套 runner 测试。
6. **验证**：`npm run typecheck -w packages/runner`；`npm test -w packages/runner`；`scc --by-file --sort complexity packages/runner/src` 确认 `executor.ts` / `branch-stability.ts` / `worktree-enforcement.ts` 三者 complexity <= 40 且排名在前 20 名之外，且 `tryRecovery` 路径行为一致。

**回滚**：任一步失败直接 revert 该步 commit；因无外部契约变化，revert 不影响已部署的 server/web/cli。

## Open Questions

- **`mergeCleanupCount` 的最终归属**：它既被清理循环（worktree-enforcement 内部）用，又用于构造 dirty-worktree 证据。当前倾向随 `enforceCleanWorktree` 一起进 `worktree-enforcement.ts`（Decision 5）。若提取后发现 executor 侧仍有残留引用，则改为两模块都不会用到的纯函数独立放置——预计不会发生，但留作实现期确认点。
- **`safeParseJson` / `errorMessage` / `isNotFoundError` 的小工具归属**：实现后统一为共享小工具：`safeParseObject` 位于 `core/json.ts`，`errorMessage` / `isNotFoundError` 位于 `core/errors.ts`，分支稳定性、工作树清洁与 recovery 模块按需导入，避免在执行器和不变式模块之间复制实现。
