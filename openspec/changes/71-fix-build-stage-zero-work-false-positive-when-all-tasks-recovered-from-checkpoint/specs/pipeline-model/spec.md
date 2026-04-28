## MODIFIED Requirements

### Requirement: Stage 描述和语义

每个 Stage SHALL 有明确的职责边界。

- **PLAN**: 基于明确需求设计方案、分解任务。Tools: read, ask_user, write
- **BUILD**: 执行任务，写代码、跑测试（内循环）。Tools: read, write, bash
- **CHECK**: 跑测试套件、代码审查、对比需求。Tools: read, bash

BUILD stage SHALL distinguish between "genuine zero-work" (no tasks were executed and none were recovered) and "full checkpoint recovery" (all tasks were previously completed and recovered from checkpoint). The `zero_work` guard SHALL only trigger for genuine zero-work.

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

#### Scenario: BUILD stage zero_work guard for genuine zero-work
- **WHEN** the build stage completes with `completed === 0` AND `total > 0`
- **AND** no checkpoint recovery was in progress (skipTaskIds is empty or does not cover all tasks)
- **THEN** the system SHALL emit `build_stage_failed` with reason `zero_work`
- **AND** the issue SHALL be marked as failed

#### Scenario: BUILD stage all tasks recovered from checkpoint
- **WHEN** the build stage completes with all tasks recovered from checkpoint
- **AND** skipTaskIds covered the full task set
- **THEN** the system SHALL NOT emit `zero_work`
- **AND** the system SHALL treat the stage as successfully completed
- **AND** proceed to commit build changes and advance to the review stage
