## Context

Plan stage 使用 multi-round ACP 连接，依次生成 proposal → specs → design → tasks → self-review 共 5 个产物。当前 `design.md` prompt 模板（`src/agents/prompts/artifacts/design.md`）包含 "If the change is small and straightforward, you may skip this file and note why" 的许可语，但 `workflow-controller.ts` 的 verify() 对所有产物一视同仁地检查文件存在性。Agent 遵循 prompt 跳过 design → verify 失败 → pipeline 回滚到 draft/blocked。

Server 重启后，`AgentRunnerService.detectRecoverableIssues()` 找到 active 非 draft 的 issue 并记录日志，但无任何后续动作。这些 issue 永远停留在 plan/build/review + active 状态。

## Goals / Non-Goals

**Goals:**
- Plan stage 5 个产物全部必须生成，agent 不可自行跳过
- Server 重启后，orphaned active issues 被自动标记为 blocked 并回滚到 draft
- 改动最小化，不改变整体架构

**Non-Goals:**
- 不实现 heartbeat/watchdog 机制（后续独立变更）
- 不修改 workflow-controller 的 verify 逻辑（当前行为正确）
- 不增加 `mo issue start` 对非 draft issue 的恢复能力（UX 改进另做）

## Decisions

### D1: 修改 prompt 模板——强制生成 design.md

**选择**: 修改 `design.md` prompt 移除 skip 许可，改为强制指令。

**理由**: 
1. **Pipeline 模型要求全部通过**: mohist 使用顺序 rounds + verify 的 pipeline 模型，不支持 OpenSpec 式的"部分完成 + blocked"状态。任何 round 失败都会回滚到 draft。
2. **产物间有依赖**: tasks.json 依赖 design.md 的决策信息，self-review 需要审查 design 的合理性。让 design 可选会在下游引入条件分支。
3. **参考 OpenSpec 的教训**: OpenSpec 的 schema 也允许跳过 design（`When to include design.md (create only if any apply)`），但 OpenSpec 使用 ArtifactGraph 依赖管理——如果 design 不存在，tasks 会被 blocked，用户可以决定如何处理。mohist 没有这种灵活性，所以强制生成是最简单的规则。

**Prompt 风格参考 OpenSpec**: OpenSpec 将 template（纯结构）和 instruction（指令）分离，template 只包含 section headers，没有条件判断。我们借鉴这种风格，将 design.md prompt 改为明确的强制指令，减少 agent 困惑。

**替代方案**: 在 workflow-controller 中将 design 标记为可选 round → 增加复杂度，且 tasks prompt 可能缺少 design 上下文。

### D2: 在 AgentRunnerService 中封装恢复逻辑

**选择**: 将 orphaned issues 的恢复逻辑封装在 `AgentRunnerService.recoverIssues()` 方法中，由 `server/index.ts` 在启动时调用。

**理由**:
1. **职责内聚**: 检测逻辑（`detectRecoverableIssues`）和恢复逻辑应该在同一个类中
2. **server/index.ts 保持简洁**: 不需要在 server 启动代码中直接操作 issueRepo
3. **与现有失败处理一致**: `recoverIssues()` 内部使用与 pipeline 失败时相同的 blocked + draft 回滚模式

**替代方案**: 在 `server/index.ts` 中直接遍历 `recoverableIssues` 并调用 `issueRepo` 方法 → 导致 server 启动代码臃肿，且重复了 pipeline 失败时的回滚逻辑。

### D3: Server 启动时调用 AgentRunnerService.recoverIssues()

**选择**: 启动时调用 `agentRunner.recoverIssues()`，将 recoverable issues 的 status 设为 blocked、stage 回滚到 draft。

**理由**: 这与 pipeline 失败时的现有行为一致（`agent-runner-service.ts:262-279`），用户可以通过 `mo issue resume` + `mo issue start` 重试。不做自动 resume 是因为 agent 进程状态已丢失，无法安全恢复到中断点。

**替代方案**: 自动 resume → 不安全，worktree 状态可能不一致。只标记 blocked 不回滚 stage → 用户无法用 `mo issue start` 重试（需要 draft stage）。

## Risks / Trade-offs

- **Prompt 修改可能导致 design.md 质量下降** → 对于简单变更，agent 可能生成低质量的占位 design。缓解：借鉴 OpenSpec 的 template 风格，prompt 中使用明确的 MUST 语言，并给出"简单变更如何简化"的具体指导（保留 Context 和 Decisions 两个核心 section）。
- **Server 启动回滚可能影响正在运行的 agent** → 极低风险，因为回滚只在 server 重启时执行，此时不可能有 agent 在运行。
- **与 OpenSpec 设计理念的差异** → OpenSpec 允许跳过 design（依赖图会自然 block 后续 artifact），而 mohist 强制生成。这是架构差异导致的合理妥协：pipeline 模型 vs 图模型。未来若迁移到 ArtifactGraph，可以重新考虑此决策。
