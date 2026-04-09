## Why

当前的 Mohist workflow 是简单的线性阶段推进（plan → build → check），缺乏自动化审查和迭代优化机制。用户期望的是一个更智能的 workflow：在设计和实现阶段自动进行多维度审查，发现问题自动修复，只有在关键决策点才需要用户介入。这将大幅提升开发效率，减少人工审查负担。

## What Changes

- 重新设计 workflow 阶段模型：Explore → Plan (内循环) → Build (顺序执行) → Review (内循环) → Done
- 实现 Plan 阶段的多维度 Agent 审查机制（完整性、一致性、可行性、风险）
- 实现代码审查的多维度评估（正确性、复杂性、测试覆盖、安全性）
- 构建内循环控制器：审查失败时自动优化/修复，直到通过或达到最大迭代次数
- 设计灵活的审查通过标准（Agent 自主判断，非硬编码规则）
- 实现对话式用户审查交互（在关键审批点通过对话界面与用户交互）
- 建立变更产出物管理：设计方案、specs、任务规划统一存储在 `.mohist/changes/` 目录下，由 Git 管理

## Capabilities

### New Capabilities
- `workflow-engine`: 核心 workflow 执行引擎，管理阶段转换和循环控制
- `multi-agent-review`: 多 Agent 并行审查系统，支持多维度的审查任务分发和结果聚合
- `loop-controller`: 内循环控制器，管理 Plan 和 Review 阶段的迭代优化
- `change-artifacts-manager`: 变更产出物管理，统一处理 proposal、design、specs、prd.json 的存储和版本控制
- `explore-trigger`: Explore 阶段到 Plan 阶段的触发机制

### Modified Capabilities

## Impact

- 修改 `packages/cli/src/types/index.ts` 中的 Stage 枚举定义
- 新增 `packages/cli/src/workflow/` 目录下的 workflow 控制器实现
- 修改 `packages/cli/src/agents/main-agent.ts` 以支持新的 workflow 模式
- 新增 `packages/cli/src/review/` 目录存放审查相关的 Agent 实现
- 新增 `.mohist/changes/` 目录结构用于存储变更产出物
- 影响现有的 `advance_stage` 工具和阶段转换逻辑
