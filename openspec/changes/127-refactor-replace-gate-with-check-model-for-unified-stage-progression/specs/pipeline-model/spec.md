## MODIFIED Requirements

### Requirement: Pipeline 由有序 Stage 组成

Pipeline SHALL 由 4 个有序 Stage 组成：PLAN → BUILD → CHECK → DONE。Stage 之间串行执行，不可跳过或乱序。每个 Stage 统一使用 Task + Check + Reaction 模型推进，推进条件为所有 checks 通过。

#### Scenario: Issue 进入 pipeline

- **WHEN** Issue 被启动（`mo issue start <id>`）
- **THEN** Issue stage 从 `draft` 变为 `plan`
- **AND** PLAN stage 开始执行

#### Scenario: Stage 顺序推进

- **WHEN** PLAN stage 所有 checks 通过
- **THEN** Issue stage 变为 `build`
- **WHEN** BUILD stage 所有 checks 通过
- **THEN** Issue stage 变为 `check`
- **WHEN** CHECK stage 所有 checks 通过
- **THEN** Issue stage 变为 `done`

### Requirement: CHECK stage 失败后回到 PLAN

Pipeline SHALL 支持 PLAN → BUILD → CHECK 循环。当 CHECK stage 的 check 失败时，根据该 check 的 reaction 策略决定回退目标：`escalate` reaction 可回退到 BUILD 或 PLAN。

#### Scenario: CHECK 发现 build-test 失败回退到 BUILD

- **WHEN** CHECK stage 的 `build-test-passed` check 失败
- **AND** 其 reaction 为 `auto-fix`（最多 2 次自动修复）
- **AND** 自动修复后仍失败
- **THEN** reaction 升级为 `escalate`，目标为 BUILD stage
- **AND** Issue stage 从 `check` 变为 `build`

#### Scenario: CHECK 发现 ai-review 失败回退到 PLAN

- **WHEN** CHECK stage 的 `ai-review-passed` check 失败
- **AND** 其 reaction 为 `escalate`，目标为 PLAN
- **THEN** Issue stage 从 `check` 变为 `plan`
- **AND** PLAN stage 基于审查报告制定修复计划

#### Scenario: CHECK 通过完成 Issue

- **WHEN** CHECK stage 所有 checks 通过（包括 `user-approval`）
- **THEN** Issue stage 从 `check` 变为 `done`
- **AND** Issue status 保持 `active`

## REMOVED Requirements

### Requirement: Stage 包含可并行的 Job

**Reason**: Replaced by unified Task + Check + Reaction model. Each stage declares a Task list (serial) and Check list (serial), not parallel Jobs.

**Migration**: Job concept removed entirely. Each stage's work is expressed as a serial Task list. No parallel execution.

### Requirement: Gate 是 Stage 属性

**Reason**: Replaced by Check-based stage progression. `user-approval` is now a check item in the checks list, not a `gate_after` stage property. All stages advance when all their checks pass.

**Migration**: Stages that previously had `gate_after: human` now include a `user-approval` check in their checks list. Stages that had `gate_after: none` simply do not include `user-approval` in their checks.

### Requirement: 默认 Pipeline 配置

**Reason**: Replaced by declarative Task + Check + Reaction model per stage. Each stage's behavior is defined by its Task list, Check list (including `user-approval` where applicable), and Reaction strategies — not by a YAML configuration with `approval` fields.

**Migration**: Pipeline configuration is now expressed in code as stage runner definitions. Plan stage includes `user-approval` check. Build stage does not. Check stage includes `user-approval` check.
