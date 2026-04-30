## MODIFIED Requirements

### Requirement: Stage 枚举定义

系统 SHALL 使用以下 Stage 枚举值：

```
draft | plan | build | check | done | backlog | explore
```

- `draft`: Issue 创建后的初始状态
- `plan`: 规划阶段（基于明确需求做技术方案）
- `build`: 构建阶段（代码实现）
- `check`: 检查阶段（构建验证、合并就绪检查、AI 代码审查）
- `done`: 完成
- `backlog`: 待办
- `explore`: 探索阶段

Code 中的 `Stage` enum SHALL 为 `Review = 'review'` → `Check = 'check'`，`STAGE_ORDER` SHALL 将 `Stage.Review` 替换为 `Stage.Check`，`STAGE_TRANSITIONS` SHALL 将所有 `Stage.Review` 引用替换为 `Stage.Check`。

#### Scenario: Stage 枚举值校验
- **WHEN** 系统写入 Issue stage 字段
- **THEN** 值必须是 `draft | plan | build | check | done | backlog | explore` 之一
- **AND** 值 `review` SHALL 被拒绝

#### Scenario: DB migration renames review to check
- **WHEN** server starts and detects issues with `stage = 'review'` in the database
- **THEN** system SHALL run migration `UPDATE issues SET stage = 'check' WHERE stage = 'review'`
- **AND** subsequent reads return `stage = 'check'`

### Requirement: Stage 描述和语义

每个 Stage SHALL 有明确的职责边界。

- **PLAN**: 基于明确需求设计方案、分解任务。Tools: read, ask_user, write
- **BUILD**: 执行任务，写代码、跑测试（内循环）。Tools: read, write, bash
- **CHECK**: 执行多步检查套件（Build & Test 验证、Merge Ready 干跑、AI 代码审查），确保代码质量和合并就绪性。Tools: read, bash

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

#### Scenario: CHECK stage 执行检查套件
- **WHEN** Issue 进入 CHECK stage
- **THEN** 系统按序执行 Build & Test → Merge Ready → AI Code Review 检查
- **AND** 每步结果存入 CheckSuiteOutput
- **AND** 全部通过后进入审批门
- **AND** 审批后通过 MergeQueue 合并

### Requirement: CHECK stage 失败后回到 BUILD

Pipeline SHALL 支持 CHECK stage 失败后回到 BUILD stage。当 CHECK stage 的检查套件发现不可自动修复的问题时，Issue 回到 BUILD stage 重新实现。

#### Scenario: CHECK Build & Test 失败回到 BUILD
- **WHEN** CHECK stage 的 Build & Test 检查失败（auto-fix 耗尽）
- **AND** 用户选择 "Back to Build"
- **THEN** Issue stage 从 `check` 变为 `build`
- **AND** BUILD stage 基于 check results 中的 buildLog 重新实现

#### Scenario: CHECK AI Review 失败回到 BUILD
- **WHEN** CHECK stage 的 AI Code Review 检查失败
- **AND** 用户选择 "Back to Build"
- **THEN** Issue stage 从 `check` 变为 `build`
- **AND** BUILD stage 基于 reviewReport 中的修复建议重新实现

#### Scenario: CHECK 通过完成 Issue
- **WHEN** CHECK stage 所有检查通过且用户审批
- **THEN** MergeQueue 处理合并
- **AND** 合并成功后 Issue stage 从 `check` 变为 `done`

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
- **WHEN** CHECK stage 完成（含 MergeQueue 合并成功）
- **THEN** Issue stage 变为 `done`
