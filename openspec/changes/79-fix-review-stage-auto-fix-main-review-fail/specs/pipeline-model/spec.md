## MODIFIED Requirements

### Requirement: CHECK stage 失败后回到 PLAN

Pipeline SHALL 支持 PLAN → BUILD → CHECK 循环。当 CHECK stage 发现问题时，Issue 回到 PLAN stage 制定修复计划。当 CHECK stage (review) 的 Result 为 FAIL 且 auto-fix 循环耗尽时，系统 SHALL 升级回 BUILD stage 进行修复实现。

#### Scenario: CHECK 发现问题回到 PLAN
- **WHEN** CHECK stage 的审查产出问题列表
- **AND** 问题列表不为空
- **THEN** Issue stage 从 `check` 变为 `plan`
- **AND** PLAN stage 基于审查报告制定修复计划

#### Scenario: CHECK 通过完成 Issue
- **WHEN** CHECK stage 审查通过
- **THEN** Issue stage 从 `check` 变为 `done`
- **AND** Issue status 保持 `active`

#### Scenario: Review auto-fix PASS proceeds to approval
- **WHEN** CHECK stage review produces Result: PASS (either initially or after auto-fix)
- **THEN** the stage returns `requiresApproval: true`
- **AND** waits for user approval before marking done

#### Scenario: Review auto-fix exhaustion escalates to BUILD
- **WHEN** CHECK stage review auto-fix loop exhausts maximum attempts
- **THEN** StageResult `escalateToStage` is set to `'build'`
- **AND** the pipeline returns to BUILD stage with the review report as context
- **AND** a `no-auto-fix` checkpoint is recorded for the review stage

#### Scenario: Review after escalation skips auto-fix
- **WHEN** CHECK stage runs after build escalation
- **AND** the `no-auto-fix` checkpoint exists
- **THEN** review Result: FAIL does NOT trigger auto-fix
- **AND** proceeds directly to `requiresApproval: true`

### Requirement: Stage 描述和语义

每个 Stage SHALL 有明确的职责边界。

- **PLAN**: 基于明确需求设计方案、分解任务。Tools: read, ask_user, write
- **BUILD**: 执行任务，写代码、跑测试（内循环）。Tools: read, write, bash
- **CHECK**: 跑测试套件、代码审查、对比需求。Tools: read, bash。CHECK stage 内部包含多轮 rounds：R0 (review) → R1 (self-check) → parse Result → [optional: R2 (auto-fix) → R3 (re-verify)] × N。

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

#### Scenario: CHECK stage internal rounds
- **WHEN** CHECK stage executes
- **THEN** it runs R0 (review agent) followed by R1 (self-check)
- **AND** parses the Result from review.md
- **AND** if FAIL, enters auto-fix loop: R2 (auto-fix) → R3 (re-verify), up to 2 attempts
- **AND** if PASS or loop exhausted, proceeds to approval gate
