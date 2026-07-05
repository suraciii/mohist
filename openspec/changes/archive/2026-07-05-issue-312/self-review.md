# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` 与 `design.md` 描述守护套件规模时写「35 个 spec 文件」，而 issue 正文 AC4 与实际目录均为「34 个 spec 文件」——`Specs/Workflow/Grain/` 下共 35 个 `.cs` 文件，但其中 `BacklogFixture.cs` 是共享 fixture 而非 spec，真正 spec 文件为 34。issue 是 spec 先行的源点，proposal/design 的「35」与 issue 不一致。
  Verification: 已将 `proposal.md` 两处「35 个 spec 文件」与 `design.md` 一处「35 个 spec 文件」改为「34」；其余描述（~8k 行、具体 spec 类名引用）逐字未动；所有被引用的 spec 类名（`StageLockSpecs`、`TaskOutputCaptureSpecs` 等）均在实际目录中存在。
  Status: resolved

## Blocking Items

无。

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: `T-002`（WorkflowStageInitializer）`dependsOn` 为空。`design.md` D5 定义的落地顺序为「协调器 → 初始化器 → outcome」，但 T-002 的 notes 已明确「与 T-001 无耦合，可并行或在其后落地」——初始化器只突变 `run.InitializeStage` 并合并事件，不触 session health、不经 SaveRunAsync，与协调器确实无硬依赖。因此空 `dependsOn` 在技术上成立，且与 D5「顺序不可调换」的硬约束（仅指 T-003 依赖 T-001）不冲突。
  SuggestedAction: 实现期可按 D5 风险升序先落 T-001 再落 T-002（便于单步回溯），但无需在 tasks.json 强加依赖——这会不必要地串行化两个本可并行的切片。保持现状即可，留作实现期节奏参考。
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: issue AC5「WorkflowGrain scc Complexity 显著下降，脱离 server grain 前列」仅在 `T-003` acceptance 末条出现，未在任何 spec requirement 中固化为可验证场景。这是合理的——复杂度下降是重构的度量型副产物而非行为契约，由 spec 套件守护行为等价、由 scc 度量守护复杂度，二者职责分离。
  SuggestedAction: 无需改动。实现期在 T-003 完成后跑一次 scc 核对 grain 行数/复杂度下降即可，作为 issue 验收的人工检查点。
  Status: follow-up

## 核验摘要

- **alignment**：proposal 三条「What Changes」与 issue 的诊断/拆分方案/AC 逐条对应；三个 capability（stage-lock-coordination / outcome-processing / stage-initialization）与 issue 三个抽取簇一一映射；Non-Goals（不改 `IWorkflowGrain` 接口、不改持久化 schema、不迁 `GetAssignedRunnerIdAsync`、不碰 `On()` 死脚手架、不做性能优化）在 proposal/design/tasks 中一致表述。
- **completeness**：三簇 helper 方法（stage-lock 4 个、outcome 9 个、init 1 个）在 proposal/spec/tasks 三处列名完全一致且无遗漏；关键不变量（无新增 async 让出点、保存→发布顺序、ETag 冲突 `DeactivateOnIdle()` 重载、`StageStarted ⟹ Initialized`）在三份 spec 中均有对应 Requirement + Scenario；边缘情形（null 资源短路、projectId 缺失抛 `InvalidOperationException`、级联 StageStarted、repair 限额、CheckUnrepaired 门控）均已覆盖。
- **consistency**：命名（`WorkflowStageLockCoordinator` / `WorkflowStageInitializer` / `WorkflowOutcomeProcessor`）跨所有文档一致；tasks 的 `spec` 字段指向的三个 spec 文件均存在；design 的 D1–D5 决策与 spec 的硬约束吻合；spec 文件计数已修复为与 issue 一致的 34。
- **feasibility**：三任务粒度适当——每个任务是一个「抽取一簇 + 全量 spec 守护」的完整可验证切片，无单独的「定义接口/注册 DI/添加测试」过细任务，测试以既有 spec 套件守护方式内嵌于各任务 acceptance；无循环依赖。
- **dependency_completeness**：`T-001`（priority 1，`dependsOn: []`）为先决；`T-002`（priority 2，`dependsOn: []`，与 T-001 无耦合可并行）；`T-003`（priority 3，`dependsOn: ["T-001"]`，因 `ClearExecutableStateAsync` 先释放当前 stage 锁）；所有 `dependsOn` 指向存在且 priority 更低的 ID，无环。

<promise>PASS</promise>
