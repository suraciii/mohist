### Requirement: 分支稳定性不变式位于独立单一职责模块

分支稳定性不变式（git 探测 + 证据构造 + 失败映射）SHALL 驻留在独立的单一职责模块 `packages/runner/src/runtime/branch-stability.ts` 中。执行器 SHALL 通过对该模块的单次函数调用委托检查，而不在自身内部内联实现。模块 SHALL 以无状态函数形式提供 `checkBranchStability`、`readCurrentBranch`、`expectedWorkspaceBranch`、`branchInvariantViolationFailure`、`attachBranchStabilityEvidence`、`branchStabilityToJson`，以及类型 `BranchStabilityEvidence`、`BranchInvariantViolationEvidence`、`CurrentBranchResult`。函数 SHALL 接收 `(work, workDir, expectedBranch, boundary, signal)` 并返回 ok+证据 或 违规+失败结果。

#### Scenario: 分支稳定性实现不在执行器内联

- **WHEN** 检查分支稳定性不变式（`checkBranchStability`、`readCurrentBranch`、`expectedWorkspaceBranch`、`branchInvariantViolationFailure`、`attachBranchStabilityEvidence`、`branchStabilityToJson` 及其类型）所在位置
- **THEN** 这些实现 SHALL 全部位于 `branch-stability.ts`
- **AND** 执行器 SHALL NOT 在自身内部内联其实现，SHALL 仅通过导入该模块的导出符号委托调用

#### Scenario: 边界检查降为单次委托调用

- **WHEN** 检查 `executeOne` 管线中 start 与 end 边界的分支稳定性检查
- **THEN** 每处边界检查 SHALL 为一次对 `branch-stability.ts` 导出函数的委托调用
- **AND** 执行器 SHALL NOT 在边界处内联多分支决策（detached / nonGit / error / mismatch）或 git 探测细节

#### Scenario: 无状态函数契约

- **WHEN** 检查 `branch-stability.ts` 模块中函数的签名与状态
- **THEN** 函数 SHALL 为无状态函数，输入 `(work, workDir, expectedBranch, boundary, signal)`
- **AND** SHALL 返回 ok+证据 或 violation+失败结果
- **AND** SHALL NOT 持有跨调用复用的实例状态

### Requirement: 工作树清洁不变式位于独立单一职责模块

工作树清洁不变式（有界 agent 清理循环 + 陈旧 git index 锁恢复状态机 + 证据/失败构造）SHALL 驻留在独立的单一职责模块 `packages/runner/src/runtime/worktree-enforcement.ts` 中。模块 SHALL 提供 `enforceCleanWorktree`、`runAgentCleanupAttempt`、`readWorktreeSnapshot`、`recoverStaleIndexLock`、`defaultLockHolderProbe`、`resolveStaleIndexLockMs`、`dirtyWorktreeFailure`、`gitIndexLockFailure`、`formatDirtyWorktreeSummary`、`worktreeProbeFailure`、`parseFileList`，类型 `DirtyWorktreeEvidence`、`GitIndexLockRecovery`、`WorktreeProbeError`，以及常量 `DEFAULT_STALE_INDEX_LOCK_MS`。`enforceCleanWorktree` SHALL 接收 `(work, workDir, result, renderedWith, variables, signal, cleanupAction, contextParts)`。已有的纯数据模块 `worktree-cleanup.ts` SHALL 保持独立（不执行 I/O，仅提供 prompt 构造、配置读取与 `WorktreeSnapshot`）；`worktree-enforcement.ts` SHALL 导入它但 SHALL NOT 与之合并。

#### Scenario: 工作树清洁实现不在执行器内联

- **WHEN** 检查工作树清洁不变式（清理循环、陈旧锁恢复、工作树探测、证据/失败构造及上述全部符号）所在位置
- **THEN** 这些实现 SHALL 全部位于 `worktree-enforcement.ts`
- **AND** 执行器 SHALL NOT 在自身内部内联清理循环、锁恢复状态机或 lsof 探测

#### Scenario: 陈旧锁恢复状态机随模块迁移

- **WHEN** 检查陈旧 git index 锁恢复（`recoverStaleIndexLock`、`defaultLockHolderProbe`、`resolveStaleIndexLockMs`、`DEFAULT_STALE_INDEX_LOCK_MS`）所在位置
- **THEN** 这些实现 SHALL 全部位于 `worktree-enforcement.ts`
- **AND** 锁恢复的预算/超时语义 SHALL 与拆分前一致

#### Scenario: 执行器对工作树清洁降为单次委托调用

- **WHEN** 检查 `executeOne` 管线中任务完成后的工作树清洁检查
- **THEN** 该检查 SHALL 为一次对 `worktree-enforcement.ts` 导出的 `enforceCleanWorktree` 的委托调用
- **AND** 执行器 SHALL NOT 在自身内部内联其循环与证据构造细节

#### Scenario: 与纯数据工作树模块保持分离

- **WHEN** 检查 `worktree-cleanup.ts` 与 `worktree-enforcement.ts` 的职责边界
- **THEN** `worktree-cleanup.ts` SHALL 仅持有纯数据（prompt 构造、配置读取、`WorktreeSnapshot`）且 SHALL NOT 执行 I/O
- **AND** `worktree-enforcement.ts` SHALL 导入 `worktree-cleanup.ts`
- **AND** 二者 SHALL NOT 合并为一个模块

### Requirement: 共享 git 探测注入点迁移且注入模式不变

两个不变式模块共用的单一 `git` runner SHALL 作为共享可注入 helper 提取（位于其中一模块或独立 helper）。测试注入桩 `setExecutorGitRunnerForTest`、`setCleanupAgentActionForTest`、`setExecutorLockHolderProbeForTest` SHALL 迁移到各自归属的模块（git runner 与分支探测归 `branch-stability.ts` / 共享 helper；cleanup action 与 lock holder probe 归 `worktree-enforcement.ts`）。现有的 mutable-let 注入模式 SHALL 保持不变——SHALL 仅改变导入位置，SHALL NOT 改变注入机制或函数签名。

#### Scenario: git runner 作为共享注入点

- **WHEN** 检查两个不变式模块获取 `git` runner 的方式
- **THEN** 二者 SHALL 共用同一共享可注入 helper
- **AND** 该 helper SHALL NOT 被拆成两份互不可见的私有实现

#### Scenario: 测试注入桩迁移到归属模块且签名不变

- **WHEN** 在迁移后检查 `setExecutorGitRunnerForTest`、`setCleanupAgentActionForTest`、`setExecutorLockHolderProbeForTest` 的定义位置与签名
- **THEN** 每个桩 SHALL 位于其归属模块并从该模块导出
- **AND** 每个桩的函数签名 SHALL 与拆分前逐字一致

#### Scenario: mutable-let 注入机制保持

- **WHEN** 检查迁移后的测试注入机制
- **THEN** SHALL 继续采用 mutable-let 注入模式（位置变化，机制不变）
- **AND** SHALL NOT 引入新的依赖注入框架或注入范式

### Requirement: 执行器入口聚焦编排与阶段串联

`WorkExecutor.execute` / `executeOne` / `executeChecks` SHALL 聚焦编排与阶段串联。`executeOne` SHALL 收敛为线性管线：解析 action → 装配 variables → render with → 解析 workDir → `checkBranchStability(start)` → 执行 action → `normalize` → `tryRecovery` → `checkBranchStability(end)` → `enforceCleanWorktree` → `captureAndUploadArtifacts` → `captureDeclaredOutputs` → `applySetVars`，其中每个不变式各为一次函数调用。任务失败恢复（`tryRecovery` / `matchesWhen` / `readRecoveryConfig` / `readAddTasks` / `decrementRecoveryBudget`）SHALL 保留在编排入口侧。后置副作用编排（`captureAndUploadArtifacts` / `applySetVars` / `captureDeclaredOutputs`）SHALL 保持为对已提取的 `artifact-capture.ts` / `set-vars.ts` / `output-capture.ts` 的薄层。工作区解析（`prepareWorkspace` / `workspaceFromVariables` / `workspaceRoot` / `resolveWorkDir`）与 helper（`normalize` / `failure` / `baseContext` / `toCheckStatus` / `isCheck` / `resolveWorkspacePath` 等）SHALL 保留在执行器内。

#### Scenario: executeOne 为线性阶段管线

- **WHEN** 检查 `executeOne` 的方法体
- **THEN** 它 SHALL 呈现上述线性阶段顺序
- **AND** 每个不变式阶段 SHALL 为一次对已提取模块的函数调用
- **AND** SHALL NOT 在阶段之间内联不变式细节

#### Scenario: 任务失败恢复保留在编排入口侧

- **WHEN** 检查 `tryRecovery` 及其辅助函数（`matchesWhen` / `readRecoveryConfig` / `readAddTasks` / `decrementRecoveryBudget`）所在位置
- **THEN** 这些实现 SHALL 保留在执行器（编排入口）内
- **AND** SHALL NOT 被提取为独立模块（因其尚无独立 spec）

#### Scenario: 后置副作用为薄编排层

- **WHEN** 检查 `captureAndUploadArtifacts` / `applySetVars` / `captureDeclaredOutputs` 的实现
- **THEN** 它们 SHALL 保持为对已提取的 `artifact-capture.ts` / `set-vars.ts` / `output-capture.ts` 的薄层编排
- **AND** SHALL NOT 重新内联其底层细节

### Requirement: 执行、容错与调度行为逐字保持不变

本次拆分 SHALL NOT 改变工作流的执行语义、任务调度规则或容错/重试策略，SHALL NOT 改变 runner 与 server 的工作分发契约。所有现有按关注点组织的执行器 spec（分支稳定性、工作树清洁、artifact、工作区边界、checks 裁决）SHALL 不加修改地通过。recovery 路径（`tryRecovery` 失败 → `completed`+`addTasks`、预算递减、retry-self 展开）SHALL 与拆分前行为一致。

#### Scenario: 现有执行器 spec 不加修改通过

- **WHEN** 在拆分后运行 `packages/runner/tests/` 下的分支稳定性、工作树清洁、artifact、工作区边界与 checks 裁决相关 spec
- **THEN** 所有 spec SHALL 通过
- **AND** 无任何 spec SHALL 被弱化、跳过或改写以适配结构改动

#### Scenario: 任务失败恢复路径行为一致

- **WHEN** 对比拆分前后 `tryRecovery` 的行为（失败输出按 `recovery` 配置匹配、预算递减、retry-self 展开为 `completed`+`addTasks`）
- **THEN** 拆分后该路径 SHALL 与拆分前逐行为一致

#### Scenario: 工作分发契约与调度规则不变

- **WHEN** 检查拆分前后 runner↔server 的工作分发契约、任务调度规则与重试策略
- **THEN** 二者 SHALL 完全一致
- **AND** SHALL NOT 出现新增或删除的执行阶段或工作项类型

### Requirement: 各模块脱离 runner 包复杂度前列

拆分后 `executor.ts` 与两个新模块（`branch-stability.ts`、`worktree-enforcement.ts`）SHALL 各自脱离 runner 包圈复杂度（scc）前列。`executor.ts` SHALL 从约 1106 行收敛至编排入口规模，SHALL NOT 仍居 runner 包复杂度前列。

#### Scenario: 执行器收敛并脱离复杂度前列

- **WHEN** 在拆分后用 scc 对 `packages/runner/src/` 按单文件圈复杂度排序
- **THEN** `executor.ts` SHALL 不在前排（脱离 runner 包复杂度前列）
- **AND** 其行数 SHALL 较拆分前的 ~1106 行显著下降至编排入口规模

#### Scenario: 两个新模块各自脱离复杂度前列

- **WHEN** 在拆分后用 scc 对 `packages/runner/src/` 按单文件圈复杂度排序
- **THEN** `branch-stability.ts` 与 `worktree-enforcement.ts` SHALL 各自脱离 runner 包复杂度前列
- **AND** 各自 SHALL 聚焦单一执行关注点（前者：分支稳定性断言；后者：工作树清洁与陈旧锁恢复）
