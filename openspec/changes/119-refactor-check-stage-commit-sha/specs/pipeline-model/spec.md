## MODIFIED Requirements

### Requirement: Stage 描述和语义

每个 Stage SHALL 有明确的职责边界。

- **PLAN**: 基于明确需求设计方案、分解任务。Tools: read, ask_user, write
- **BUILD**: 执行任务，写代码、跑测试（内循环）。Tools: read, write, bash
- **CHECK**: 跑测试套件、代码审查，支持 auto-fix 循环重跑。检查项仅包含代码质量检查（build-test、ai-review），不包含合并状态检查（merge-ready）。Tools: read, bash

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

#### Scenario: CHECK stage 循环重跑
- **WHEN** Issue 进入 CHECK stage
- **THEN** 系统记录 worktree HEAD SHA 作为 snapshotSha
- **AND** 按顺序执行 build-test → ai-review
- **AND** 任何检查失败且 auto-fix 成功时从头重跑（最多 3 次）
- **AND** 全部通过后进入 awaiting-approval 状态

### Requirement: CHECK stage 失败后回到 PLAN

Pipeline SHALL 支持 PLAN → BUILD → CHECK 循环。当 CHECK stage 检查循环达到最大重试次数仍失败时，Issue 回到 PLAN stage 制定修复计划。

#### Scenario: CHECK 达到最大重试次数后回到 PLAN
- **WHEN** CHECK stage 的检查循环执行 3 次仍有检查失败
- **THEN** Issue stage 从 `check` 变为 `plan`
- **AND** PLAN stage 基于检查失败报告制定修复计划

#### Scenario: CHECK 通过完成 Issue
- **WHEN** CHECK stage 所有检查通过
- **THEN** 进入 awaiting-approval 状态
- **AND** 用户审批后 Issue stage 从 `check` 变为 `done`
- **AND** Issue status 保持 `active`
