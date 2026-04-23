## Context

E2E walkthrough（`talks/2026-04-22-e2e-walkthrough.md`）发现 Build stage 完全无法运行。Plan stage 正常（4/4 产物生成），design approve 成功，但 Build stage 立即失败。

根因链条：
1. Plan stage 的 self-review agent 使用 write_file 工具修改 tasks.json，将 `passes` 字段从 `false` 改为 `true`
2. Build stage 的 `RalphExecutor` 调用 `findNextPendingTask`，该函数过滤 `t.passes` 为 truthy 的任务（`ralph-executor.ts:249`）
3. 所有任务被跳过，返回 `completed=0, total=3` → `runPipelineBuildStage` 检测到 `completed===0` → 返回 `success: false`
4. `executePipeline` 的错误处理将 issue 回滚到 `draft/blocked`（`agent-runner-service.ts:331-340`）
5. 错误消息只在 `log.error` 中输出，没有持久化到 issue，API 和 CLI 无法获取

当前可观测性缺口：
- `mo issue logs` 读磁盘文件（`~/.mohist/projects/<slug>/logs/issue-<n>/`），不读 workflow_log 表，build stage 事件不在文件中
- issue 表无 error 字段，blocked 状态不携带原因
- `mo issue show` 不显示错误信息
- API `/issues/:number/logs` 读 workflow_log 表，但只返回原始事件流，不聚合关键事件

受影响文件：
- `ralph-executor.ts` — findNextPendingTask 跳过 passes=true 的任务、writeTasksFile 持久化被篡改的值
- `agent-runner-service.ts` — 错误不持久化
- `api/issues.ts` — 无错误信息暴露
- `cli/commands/issue.ts` — show/logs 不显示关键信息
- `acp-session.ts` — spawn 使用 `opencodeBinPath || 'opencode'` 回退到 PATH

## Goals / Non-Goals

**Goals:**
- Build stage 能正常执行任务（不受 plan stage self-review 修改 passes 的影响）
- Pipeline 失败时用户能通过 API 和 CLI 看到错误原因
- `mo issue logs` 能显示 pipeline 关键事件（build/task start/complete/fail）
- ACP session spawn 使用绝对路径

**Non-Goals:**
- 不阻止 self-review agent 修改 tasks.json（防御性方案更可靠）
- 不修改 self-review prompt（让 agent 不改是脆弱的）
- 不重构 `mo issue logs` 为实时流（保持读取 workflow_log 表的简单方式）
- 不改变 workflow_log 表结构

## Decisions

### D1: Build stage 入口处重置所有任务 passes 为 false

在 `ralph-executor.ts` 的 `runRalphLoop` 入口处（读取 tasks 之后、执行之前），将所有任务的 `passes` 重置为 `false`，并写回 tasks.json。

**替代方案**:
- A) 修改 self-review prompt 禁止修改 tasks.json — 脆弱，agent 可能不遵守
- B) 只读不写，用内存副本执行 — 但 writeTasksFile 在执行中会写回被篡改的值
- C) 检查 tasks.json 是否被外部修改，触发警告 — 过于复杂

**理由**: 防御性重置是最简单可靠的方案。Build stage 的职责是执行任务，所有任务都应该从 pending 状态开始。写入 tasks.json 确保后续的 writeTasksFile 调用也基于正确的初始状态。

### D2: Pipeline 失败消息存入 approvalState（复用现有字段）

在 `agent-runner-service.ts` 的 `executePipeline` 错误处理中，将错误消息写入 `approvalState`（status='error'），而不是新增 error 字段到 issues 表。

**替代方案**:
- A) 新增 `last_error` 列到 issues 表 — 需要 migration、DTO 变更、API 变更
- B) 存入 comments — 语义不匹配
- C) 只依赖 log 文件 — log 文件可能不可达

**理由**: `approvalState` 已经是 JSON 字段，API 已返回它，CLI 可直接读取。复用它避免 schema migration。新增 `status: 'error'` 状态与现有的 `awaiting/approved/rejected` 并列。

### D3: `mo issue logs` 从 workflow_log 表读取 pipeline 关键事件

在 `cli/commands/issue.ts` 的 logs 命令中，先调用 `/issues/:number/logs?eventType=build_started,built_completed,build_failed,task_started,task_completed,task_failed` 获取 pipeline 事件，然后显示。

**替代方案**:
- A) 保留只读文件方式 — build stage 事件不在文件中
- B) 新增专门的 pipeline-status API — 过度设计
- C) 让 build stage 也将事件写入文件 — 需要改变 ralph-executor 架构

**理由**: 数据已经在 workflow_log 表中，只需要 CLI 去读取。API 端点已支持 eventType 过滤。最小改动实现最大可观测性提升。

### D4: ACP spawn 使用绝对路径

在 `acp-session.ts` 中，如果 `opencodeBinPath` 未提供，调用 `resolveOpencodeBinPath()` 获取绝对路径，而不是 fallback 到 `'opencode'`（依赖 PATH）。

**替代方案**:
- A) 在 server 启动时检测 opencode 是否可达 — 只是检测，不解决问题
- B) 修改 systemd/启动脚本确保 PATH — 环境特定，不可靠

**理由**: `resolveOpencodeBinPath` 已存在于 config-loader，返回绝对路径。用它消除对 PATH 的依赖。

## Risks / Trade-offs

- **[风险] 重置 passes 会丢失真正已完成的任务状态** → 实际上 Build stage 应该执行所有任务。如果一个任务的产物已存在，agent 会快速跳过（代码已存在则不需要重写）。这是安全的行为。
- **[风险] 复用 approvalState 存错误可能混淆审批逻辑** → 只在 `executePipeline` 的 catch 分支中使用 `status: 'error'`，正常的审批流程不受影响。`hasPendingGate` 检查的是 `pendingGates` Map，不读 approvalState 的 status。
- **[风险] workflow_log FK 失败导致事件丢失** → 这是独立问题（之前的 fix-walkthrough-bugs 已部分解决），重置 passes 后 build 能正常执行，FK 问题可能仍然存在但不再是阻断性的。

## Review Findings

### 2026-04-23 方案审查

审查中发现的以下问题已在实施前修复：

1. **ApprovalStatus 类型缺失 'error'** (`types/index.ts:40`)
   - T-002 计划写入 `status: 'error'` 到 approvalState，但 `ApprovalStatus` 类型定义不包含 `'error'`
   - **已修复**: 在 `ApprovalStatus` 中加入 `'error'`
   - 影响: 所有读 approvalState.status 的地方自动兼容（TypeScript 联合类型）

2. **T-001 重置逻辑过于激进**
   - 原方案无条件重置所有 passes=false，会破坏未来可能的 resume 场景（已完成的任务进度丢失）
   - **已修复**: 改为只在检测到异常状态（所有任务 passes=true）时才重置
   - 这保留了防御性，同时不破坏正常的增量执行

3. **T-004 fallback 设计可能掩盖文件日志**
   - 原方案是"互斥 fallback"：有 pipeline 事件就不显示文件日志
   - **已修复**: 改为"叠加显示"：先显示 pipeline 事件（结构化概览），再显示文件日志（详细输出）
   - 两者提供不同维度的信息，同时显示更有价值

## Migration Plan

无需 migration。所有改动是代码层面的，不涉及数据库 schema 变更。

## Open Questions

无。
