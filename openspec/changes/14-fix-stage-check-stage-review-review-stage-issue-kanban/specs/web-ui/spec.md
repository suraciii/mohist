## ADDED Requirements

### Requirement: 前端 Stage enum 与后端完全对齐

前端 `Stage` enum SHALL 包含后端定义的全部 6 个 stage 值：`Draft | Explore | Plan | Build | Review | Done`，且每个值的字符串标识符 SHALL 与后端 `types/index.ts` 中的定义完全一致。

#### Scenario: Stage enum 包含全部后端阶段
- **WHEN** 检查前端 `Stage` enum 定义
- **THEN** 包含 `Draft = "draft"`, `Explore = "explore"`, `Plan = "plan"`, `Build = "build"`, `Review = "review"`, `Done = "done"`
- **AND** 不包含后端不存在的 stage 值（如 `Check = "check"`）

### Requirement: Kanban 显示所有 stage 的 issue

Kanban 看板 SHALL 为后端定义的每个 stage 提供对应的列，确保任何 stage 的 issue 都不会因前端缺少对应列而被静默丢弃。

#### Scenario: Review stage 的 issue 显示在 Kanban
- **WHEN** 后端存在 `stage: review` 的 issue
- **THEN** 该 issue 出现在 Kanban 的 "Review" 列中
- **AND** 列标题显示为 "Review"

#### Scenario: Explore stage 的 issue 显示在 Kanban
- **WHEN** 后端存在 `stage: explore` 的 issue
- **THEN** 该 issue 出现在 Kanban 的 "Explore" 列中
- **AND** 列标题显示为 "Explore"
