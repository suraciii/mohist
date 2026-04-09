## Why

当前的 Mohist workflow 是简单的线性阶段推进（plan → build → check），缺乏自动化审查和迭代优化机制。用户期望的是一个更智能的 workflow：在设计和实现阶段自动进行多维度审查，发现问题自动修复，只有在关键决策点才需要用户介入。这将大幅提升开发效率，减少人工审查负担。

## What Changes

- 重新设计 workflow 阶段模型：Explore → Plan → Build → Review → Done
- 引入 3 个核心 Agent：Planner（设计）、Coder（实现）、Reviewer（审查）
- **Plan 阶段**：Planner Agent 生成设计方案，具体的审查维度通过 Prompt 自定义
- **Build 阶段**：顺序执行 prd.json 中的 tasks，每个 task 调用 Coder Agent
- **Review 阶段**：Reviewer Agent 审查代码质量，具体的审查维度通过 Prompt 自定义
- 移除复杂的内循环编排层，Agent 自主决定是否需要迭代
- 建立变更产出物管理：设计方案、specs、任务规划统一存储在 `.mohist/changes/` 目录下

## Stage Migration Mapping

| 旧 Stage | 新 Stage | 说明 |
|---------|---------|------|
| Draft | **Explore** | 新增探索阶段，用于需求澄清和方案预研 |
| Plan | **Plan** | Planner Agent 生成设计，用户审批 |
| Build | **Build** | 顺序执行 tasks，Coder Agent 实现 |
| Check | **Review** | Reviewer Agent 审查，用户审批 |
| Review | **Review** | 合并为代码审查阶段 |
| Done | **Done** | 保持不变 |

## Capabilities

### New Capabilities

- `workflow-engine`: 核心 workflow 执行引擎，管理阶段转换
- `planner-agent`: 设计阶段 Agent，通过 Prompt 自定义设计规范和审查标准
- `coder-agent`: 实现阶段 Agent，执行 prd.json 中的具体任务
- `reviewer-agent`: 审查阶段 Agent，通过 Prompt 自定义审查维度
- `change-artifacts-manager`: 变更产出物管理

### Modified Capabilities

- `main-agent`: 简化为 WorkflowController 的调用者，不再管理复杂的编排逻辑

## Impact

- 修改 `packages/cli/src/types/index.ts` 中的 Stage 枚举定义
- 新增 `packages/cli/src/workflow/` 目录下的 workflow 控制器实现
- 修改 `packages/cli/src/agents/main-agent.ts` 以支持新的 workflow 模式
- 新增 `.mohist/changes/` 目录结构用于存储变更产出物
- 影响现有的 `advance_stage` 工具和阶段转换逻辑

## Success Metrics

- Plan 阶段设计方案质量：用户审批通过率 > 90%
- Build 阶段任务完成率：首次执行成功率 > 70%
- Review 阶段代码质量：用户审批通过率 > 85%
- 产出物管理：所有变更文档 100% 纳入 Git 版本控制
