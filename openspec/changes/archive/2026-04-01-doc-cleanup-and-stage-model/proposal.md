## Why

M1 完成后，项目中残留大量描述旧架构（deterministic workflow engine、GitHub Labels、crawlph 命名）的文档和 spec，与新 agent 架构严重不一致。同时，PRD 定义 5 阶段、代码实现 4 阶段、backlog PBI 提议 3 阶段，三者未对齐。在进入 M2 之前，需要统一文档认知和 stage 模型，为后续工作建立清晰基线。

## What Changes

- 删除 `design/` 下 9 个旧设计文档（tech-spec.md, issueflow.md, workflow.md, draft.md, explore.md, plan.md, dev.md, verify.md, CROSS-PLATFORM.md），它们描述的是旧确定性状态机架构或 crawlph/openclaw 时代的设计
- 重写 `design/` 为 3 个新文档：plan.md、build.md、check.md，反映 PLAN → BUILD → CHECK 循环模型
- 删除 `openspec/specs/` 下 11 个描述旧架构的 spec（issue-workflow, workflow-stages, issue-orchestration, ralph-loop, status-poller, state-persistence, progress-reporting, agent-runner, pr-lifecycle, openspec-integration, local-merge）
- 标记 `openspec/specs/workflow-engine` 为 COMPLETED
- 重写 `openspec/specs/server-daemon` 和 `openspec/specs/project-management`，清除旧术语
- 修复 `openspec/specs/local-issue-store` 中的旧 CLI 命名
- 更新 `prd/vision.md`：crawlph → mohist
- 更新 `prd/workflow.md`：crawlph → mohist，5 阶段 → PLAN/BUILD/CHECK 3 stage 循环
- 更新 `prd/user-interaction.md`：crawlph → mohist
- 更新 `prd/diagrams/workflow-overview.md`：crawlph → mohist
- **BREAKING** 更新 `packages/cli/src/types/` 中的 Stage 枚举：`designing | implementing` → `plan | build | check`
- **BREAKING** 更新 DB schema 中 stage 相关字段值
- 更新 `prd/backlog/backlog.md`：关闭 B-001、B-002、B-003，更新 Stage 架构 PBI

## Capabilities

### New Capabilities

- `pipeline-model`: PLAN → BUILD → CHECK 循环 stage 模型，含 Stage/Job/Gate 抽象定义、反馈周期设计、循环机制（check 失败回到 plan）

### Modified Capabilities

- `server-daemon`: 清除 WorkflowEngine/TaskRepo/Worker 等旧术语引用，更新为 agent-runtime 术语
- `project-management`: crawlph 命名和旧 DB 路径更新为 mohist
- `local-issue-store`: 旧 CLI 前缀 `ph` 更新为 `mo`

## Impact

- `design/` 目录：删 9 文件，加 3 文件
- `openspec/specs/`：删 11 目录，改 3 目录
- `prd/`：改 4 文件
- `packages/cli/src/types/`：Stage 枚举值变更
- `packages/cli/src/db/`：migration 更新 stage 字段
- `packages/cli/src/services/`：所有引用 stage 字符串的地方需要更新
- `prd/backlog/backlog.md`：关闭 3 个 backlog item，更新 PBI
