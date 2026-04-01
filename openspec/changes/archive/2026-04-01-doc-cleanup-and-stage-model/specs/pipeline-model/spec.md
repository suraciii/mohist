## ADDED Requirements

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
- **BUILD**: 执行任务、写代码、跑测试（内循环）。Tools: read, write, bash
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

系统 SHALL 提供内置默认 Pipeline 配置：

```yaml
stages:
  - name: plan
    gate_after: human
  - name: build
    gate_after: none
  - name: check
    gate_after: human
```

#### Scenario: 无 workflow.yaml 时使用默认
- **WHEN** 项目没有 workflow.yaml 配置
- **THEN** 使用内置默认 3 stage pipeline
