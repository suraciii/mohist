# OpenSpec Capability: pipeline-model

### Requirement: 两种交互模式

系统 SHALL 支持两种交互模式：Explore Mode（Pipeline 外）和 Pipeline Mode（Pipeline 内）。

- **Explore Mode**: 用户与 mohist 自由对话，梳理需求、澄清模糊点、做取舍。产出清晰的 Issue 或 Change Proposal。不在 Pipeline 内，没有 Stage 约束。
- **Pipeline Mode**: `mo issue start` 后进入 PLAN → BUILD → CHECK 循环。需求已通过 Explore Mode 明确，Pipeline 专注于执行。

#### Scenario: 需求梳理在 Pipeline 外完成
- **WHEN** 用户有模糊想法
- **THEN** 用户通过 Explore Mode 与 mohist 对话
- **AND** 对话产出清晰的 Issue（含明确需求描述）
- **AND** Issue 以 `draft` 状态保存

#### Scenario: Pipeline 执行中遇到需求问题
- **WHEN** Agent 在 Pipeline 内遇到**小问题**（信息缺失、歧义）
- **THEN** Agent 通过 ask_user 问具体问题
- **AND** 用户回答后 Pipeline 继续推进
- **WHEN** Agent 在 Pipeline 内遇到**大问题**（需求矛盾、方向错误）
- **THEN** Agent 标记 Issue 为 `blocked`
- **AND** Pipeline 暂停，用户回到 Explore Mode 重新梳理

### Requirement: Pipeline 由有序 Stage 组成

Pipeline SHALL 由 3 个有序 Stage 组成：PLAN → BUILD → CHECK。Stage 之间串行执行，不可跳过或乱序。

#### Scenario: Issue 进入 pipeline
- **WHEN** Issue 被启动（`mo issue start <id>`）
- **THEN** Issue stage 从 `draft` 变为 `plan`
- **AND** PLAN stage 开始执行

#### Scenario: Stage 顺序推进
- **WHEN** PLAN stage 完成
- **THEN** Issue stage 变为 `build`
- **WHEN** BUILD stage 完成
- **THEN** Issue stage 变为 `check`
- **WHEN** CHECK stage 完成
- **THEN** Issue stage 变为 `done`

### Requirement: CHECK stage 失败后回到 PLAN

Pipeline SHALL 支持 PLAN → BUILD → CHECK 循环。当 CHECK stage 发现问题时，Issue 回到 PLAN stage 制定修复计划。

#### Scenario: CHECK 发现问题回到 PLAN
- **WHEN** CHECK stage 的审查产出问题列表
- **AND** 问题列表不为空
- **THEN** Issue stage 从 `check` 变为 `plan`
- **AND** PLAN stage 基于审查报告制定修复计划

#### Scenario: CHECK 通过完成 Issue
- **WHEN** CHECK stage 审查通过
- **THEN** Issue stage 从 `check` 变为 `done`
- **AND** Issue status 保持 `active`

### Requirement: Stage 包含可并行的 Job

每个 Stage SHALL 包含一个或多个 Job。同一 Stage 内的 Job 并行执行。Job 可声明对同 Stage 内其他 Job 的依赖（`needs`）。

#### Scenario: 单 Job Stage (M1/M2)
- **WHEN** Stage 配置为单个 Job
- **THEN** 该 Job 直接执行
- **AND** Job 完成即 Stage 完成

#### Scenario: 并行 Job 执行
- **WHEN** Stage 配置为多个无依赖 Job
- **THEN** 所有 Job 同时启动
- **AND** 所有 Job 完成后 Stage 才算完成

#### Scenario: Job 依赖
- **WHEN** Job B 声明 `needs: [Job A]`
- **THEN** Job A 完成后 Job B 才启动
- **AND** Job A 和无依赖的其他 Job 并行执行

### Requirement: Gate 是 Stage 属性

Stage SHALL 支持 `gate_after` 属性，值为 `none` 或 `human`。当 `gate_after` 为 `human` 时，Stage 完成后暂停等待用户确认。

#### Scenario: Human gate 暂停
- **WHEN** PLAN stage 配置 `gate_after: human`
- **AND** PLAN stage 所有 Job 完成
- **THEN** Pipeline 暂停
- **AND** Issue status 变为 `waiting_gate`
- **AND** 等待用户确认后推进到 BUILD

#### Scenario: 无 gate 自动推进
- **WHEN** BUILD stage 配置 `gate_after: none`
- **AND** BUILD stage 完成
- **THEN** Pipeline 自动推进到 CHECK stage

### Requirement: Stage 枚举定义

系统 SHALL 使用以下 Stage 枚举值：

```
draft | plan | build | check | done
```

- `draft`: Issue 创建后的初始状态
- `plan`: 规划阶段（基于明确需求做技术方案）
- `build`: 构建阶段（代码实现）
- `check`: 检查阶段（测试、审查）
- `done`: 完成

#### Scenario: Stage 枚举值校验
- **WHEN** 系统写入 Issue stage 字段
- **THEN** 值必须是 `draft | plan | build | check | done` 之一
- **AND** 其他值 SHALL 被拒绝

### Requirement: Stage 描述和语义

每个 Stage SHALL 有明确的职责边界。

- **PLAN**: 基于明确需求设计方案、分解任务。Tools: read, ask_user, write
- **BUILD**: 执行任务，写代码、跑测试（内循环）。Tools: read, write, bash
- **CHECK**: 跑测试套件、代码审查、对比需求。Tools: read, bash

#### Scenario: PLAN stage 首次执行
- **WHEN** Issue 首次进入 PLAN stage
- **THEN** Agent 探索代码库理解技术上下文
- **AND** 基于明确需求设计方案
- **AND** 分解任务并输出计划

#### Scenario: PLAN stage 修复执行
- **WHEN** Issue 从 CHECK 回到 PLAN stage
- **THEN** Agent 分析 CHECK 的审查报告
- **AND** 制定修复计划

#### Scenario: BUILD stage 内循环
- **WHEN** Agent 在 BUILD stage 执行任务
- **THEN** Agent 自主循环：写代码 → 跑测试 → 修复 → 重跑
- **AND** 直到所有任务完成或遇到无法解决的问题

### Requirement: 默认 Pipeline 配置

系统 SHALL 提供内置默认 Pipeline 配置。默认配置中 build 和 check 阶段均配置 approval（表示 plan gate 和 check gate）。

```yaml
stages:
  - stage: plan
    prompt: '分析 issue #{issue.number}: {issue.title}，探索 codebase，产出实现计划'
    approval: false
  - stage: build
    prompt: '按 plan 阶段的计划实现 {issue.title}。计划摘要：{plan.output}'
    approval: true
  - stage: check
    prompt: '检查 {issue.title} 的实现：运行测试、lint、typecheck，报告问题'
    approval: true
```

approval 语义：当阶段 S 配置 `approval: true` 时，表示进入 S 前需要用户审批（等价于上一阶段的 gate_after: human）。

#### Scenario: 无 workflow.yaml 时使用默认
- **WHEN** 项目没有 workflow.yaml 配置
- **THEN** 使用内置默认 3 stage pipeline
- **AND** build 和 check 阶段均有 approval: true

#### Scenario: plan gate（build approval）
- **WHEN** plan 阶段执行完成
- **AND** build 阶段配置 approval: true
- **THEN** agent 停止，等待用户审批
- **AND** 用户审批后进入 build

#### Scenario: check gate（check approval）
- **WHEN** build 阶段执行完成
- **AND** check 阶段配置 approval: true
- **THEN** agent 停止，等待用户审批
- **AND** 用户审批后进入 check

### Requirement: REQ-PM-001 Stage task check boundaries are explicit

Pipeline stages SHALL follow clear responsibility boundaries: stages orchestrate, tasks execute, and checks verify. Only tasks SHALL be allowed to change code, write durable workflow artifacts, run coder sessions, run side-effecting commands, or repair failed check findings.

#### Scenario: Stage runs task check loop
- **WHEN** a stage executes
- **THEN** it SHALL run its tasks
- **AND** it SHALL run its checks
- **AND** if a check fails with a configured fix task, it SHALL run that fix task and re-run the failed check
- **AND** if the check still fails after max attempts, it SHALL stop in the current stage

#### Scenario: Check does not execute repair
- **WHEN** a failed check identifies a repairable problem
- **THEN** the repair SHALL be represented as a task in the same stage history
- **AND** the check SHALL remain a read-only verifier

### Requirement: REQ-PM-002 No fallback chain for first fix policy

The first check failure policy implementation SHALL NOT introduce fallback-to-plan, fallback-to-build, fallback ask-user, nested reaction chains, or multi-stage failure policies. When fix attempts are exhausted, the stage SHALL remain failed or paused with visible evidence.

#### Scenario: Exhausted fix attempts do not change stage
- **WHEN** a check fails after all configured fix attempts
- **THEN** the issue SHALL remain in the current stage state for user or later workflow recovery
- **AND** the failed check result and fix task result SHALL remain visible

### Requirement: REQ-PM-003 CHECK defers recoverable OpenSpec sync conflicts

CHECK SHALL NOT hard-block issue progression solely because OpenSpec sync preview detects a recoverable delta classification conflict such as `missing_source` for a requirement written under `MODIFIED Requirements`. CHECK MAY record read-only preview evidence, but durable updates to `openspec/specs/` SHALL remain an INTEGRATE responsibility.

#### Scenario: Missing source preview does not block CHECK
- **WHEN** CHECK runs OpenSpec sync preview for a change delta
- **AND** the preview reports `missing_source` for a `MODIFIED` requirement that may be resolved during integration
- **THEN** CHECK SHALL NOT fail solely because of that preview conflict
- **AND** CHECK SHALL NOT write to `openspec/specs/`
- **AND** the preview evidence, if collected, SHALL remain visible as advisory output

#### Scenario: Non-OpenSpec CHECK gates still block
- **WHEN** CHECK runs health, merge readiness, AI review, or user approval checks
- **THEN** those checks SHALL retain their existing blocking semantics

### Requirement: REQ-PM-004 Integrate spec sync failure remains local

When `integrate:spec-sync` fails, the workflow SHALL keep the issue at INTEGRATE or an interrupted/blocked-at-INTEGRATE state with visible failure evidence. The workflow SHALL NOT automatically fall back to PLAN, BUILD, or CHECK, and SHALL NOT automatically rerun the entire pipeline.

#### Scenario: Spec sync failure stops at INTEGRATE
- **WHEN** `integrate:spec-sync` fails due to sync resolution or validation
- **THEN** the issue SHALL remain associated with INTEGRATE failure state
- **AND** `integrate:archive-change`, `integrate:merge`, and `final-health` SHALL NOT run
- **AND** the failure output SHALL identify the failing step as `integrate:spec-sync`

### Requirement: CHECK stage exposes review and merge decisions

The CHECK stage SHALL present one initial user-visible task, `ai-review`, followed by the user-visible checks `review-passed`, `merge-ready`, and `user-approval`. Internal health gates, integration preview evidence, review artifact retries, and implementation-specific validation SHALL NOT be exposed as separate user-facing CHECK-stage checks.

#### Scenario: Check stage starts with ai-review task

- **WHEN** a default CHECK stage starts
- **THEN** the initial user-visible task SHALL be `ai-review`
- **AND** `ai-review` SHALL be represented as task history, not as a check result

#### Scenario: Check stage visible checks are simplified

- **WHEN** CHECK-stage results are presented to users
- **THEN** the visible automated checks SHALL be `review-passed` and `merge-ready`
- **AND** the visible approval point SHALL be `user-approval`
- **AND** users SHALL NOT need to interpret `health:check`, `merge-readiness`, `integration-health-gate-preview`, or `ai-review` as check names

#### Scenario: Internal evidence stays internal

- **WHEN** CHECK-stage execution gathers health, integration-preview, artifact-retry, or repair evidence
- **THEN** that evidence MAY appear in task output, logs, or diagnostic details
- **AND** it SHALL NOT create additional user-visible check-stage decision points

### Requirement: Collected check evidence remains visible through repair

Pipeline stage execution SHALL preserve the complete initial check evidence for a phase even when a later repair task is attempted. Repair handling may change the current effective result, but it SHALL NOT reduce the user's visibility back to only the first discovered failure.

#### Scenario: Repairable failure still shows full initial diagnosis

- **WHEN** a phase initially reports multiple failing non-approval checks
- **AND** the earliest repairable failure triggers a fix task
- **THEN** the phase history SHALL still show the full initial collected result set
- **AND** the fix task plus recheck results SHALL be visible alongside that baseline evidence

#### Scenario: Later checks rerun after successful repair

- **WHEN** a fix task makes the targeted failing check pass on recheck
- **THEN** the workflow SHALL continue running later checks from that point using the repaired state
- **AND** it SHALL preserve the existing semantic that downstream checks are not skipped forever after an earlier repair succeeds

### Requirement: Exhausted or unrepairable failures remain local with full evidence

When collected phase failures cannot be repaired or remain failing after allowed attempts, the workflow SHALL stay in the current stage with complete evidence visible. It SHALL NOT fall back to another stage or collapse the visible diagnosis back to the first failure only.

#### Scenario: Failure without policy remains local

- **WHEN** a collected non-approval check result is `fail` or `error`
- **AND** no `CheckFailurePolicy` exists for that check
- **THEN** the workflow SHALL keep the issue in the current stage state
- **AND** the collected phase evidence SHALL remain visible to the user

#### Scenario: Exhausted repair attempts preserve evidence

- **WHEN** a collected failed or errored non-approval check has a fix policy
- **AND** the check still does not pass after the configured max attempts
- **THEN** the workflow SHALL keep the failed check results and fix task results visible
- **AND** it SHALL NOT automatically fall back to plan, build, or another escalation path

