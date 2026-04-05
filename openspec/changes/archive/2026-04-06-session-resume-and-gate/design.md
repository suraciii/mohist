## Context

当前 Main Agent 通过 `runMainAgent()` 启动，内部调用 `sessionManager.create(issueId)` 创建新 session，运行 LLM tool loop，结束后 `sessionManager.close(session.id)` 关闭 session。

当 agent 遇到 approval gate 时，它通过 system prompt 指引主动停止（不继续 advance_stage）。此时 session 被关闭。用户通过 `POST /issues/:number/approve` 恢复时，调用 `agentRunner.start()` 创建全新 session——丢失了 plan 输出、工具调用历史等所有上下文。

核心约束：SessionManager 是纯内存的，server 重启丢失所有 session。这是 M1 的已知限制，M4 才做 session 持久化。本次设计在内存 session 前提下工作。

## Goals / Non-Goals

**Goals:**

- approve 后能恢复之前的 session 上下文，build 阶段能看到 plan 输出
- 新增 mo approve CLI 命令
- 修复默认 workflow 与 PRD 的不一致

**Non-Goals:**

- Session 持久化到 SQLite（M4）
- mo attach 实时交互（M2 后续）
- ask_user 阻塞等待用户（M2 后续）
- 多 issue 并发（M4）

## Decisions

### D1: SessionManager 增加 findByIssueId()

**决策**: SessionManager 增加 `findByIssueId(issueId: number): Session | undefined` 方法，通过遍历 sessions Map 查找。

**理由**: 当前只有 create → use → close 生命周期，没有按 issueId 反查的能力。approve 需要找到 issue 对应的已有 session 来恢复。内存 Map 遍历对 M1/M2（单 issue 串行）足够。

**替代方案**: 维护 issueId → sessionId 的反向索引 Map。更高效但对当前规模过度设计，M4 引入持久化时会重写 SessionManager。

### D2: Gate 暂停不 close session，标记 paused

**决策**: 新增 session 状态 `paused`。当 Main Agent 在 approval gate 停止时，不调用 `sessionManager.close()`，改为 `sessionManager.pause(sessionId)`。close 只在 agent 完全结束（done 或 error）时调用。

**理由**: close 会标记 closedAt 并拒绝 appendMessage。如果 gate 时 close，resume 时无法注入用户消息。paused 状态保留 messages 但不参与 active session 查询。

```
Session 状态: active → paused → active → closed
                start    gate    approve    done/error
```

### D3: AgentRunnerService 持有 pausedSessions Map

**决策**: AgentRunnerService 内部维护 `Map<number, Session>`（issueId → paused session）。resume() 时从 map 中取出 session，注入用户消息，运行 agent loop。

**理由**: SessionManager 是通用的内存存储，不知道哪些 session 是 "可恢复的"。AgentRunnerService 作为 agent 生命周期管理者，持有对 paused session 的引用是合理的职责。

**与 D1 的关系澄清**: D1 的 findByIssueId() 用于查找任意 session，而 D3 的 Map 用于明确标记"哪些 issue 当前有 paused session"。这是一种职责分离：SessionManager 管存储，AgentRunnerService 管业务生命周期。如果只用 findByIssueId()，可能找到错误的 session（如之前的已关闭 session）。

**清理策略（补充）**: 
- 正常完成（done）或出错时：从 Map 中删除
- Issue 关闭时：清理关联的 paused session
- Server 启动时：清理所有残留的 paused session（防止重启后残留脏数据）

### D4: resume 注入用户消息而非重置 system prompt

**决策**: resume 时向 session 追加一条 user message（如 `"[System] User approved. Continue to next stage."`），然后调用 `runAgentLoop(same session, ...)` 在已有消息历史上继续。

**理由**: 保持 system prompt 不变（issue 信息、工具列表、编排指导都不变），只需告诉 LLM "用户已确认，继续"。LLM 看到完整历史（plan 输出、advance_stage 结果、comment），能正确理解当前状态并继续执行。

### D5: 默认 workflow check 加 approval: true

**决策**: 修改 workflow-loader.ts 中 DEFAULT_WORKFLOW，check 阶段加 `approval: true`。

**理由**: PRD 定义 PLAN gate_after: human, BUILD gate_after: none, CHECK gate_after: human。当前默认 workflow 把 approval 放在 build 上（语义是"进入 build 前需要审批"= plan gate），但 check 没有 approval = check gate 缺失。修正后：plan 无 approval（start 时直接执行）、build 有 approval（plan 完成后等审批=plan gate）、check 有 approval（build 完成后等审批=check gate）。

注意：这与 pipeline-model spec 中 `plan: gate_after: human` 的默认配置有语义差异——approval 放在下一阶段 entry 上 vs 当前阶段 exit 上。两种模型等价，只是表达方式不同。当前实现用 "approval on next stage" 表达 "gate after current stage"。

**术语映射澄清（补充）**:
```
PRD Intent          →  Code Implementation
─────────────────────────────────────────────
PLAN gate_after: human   →  BUILD stage approval: true
BUILD gate_after: none   →  CHECK stage approval: false  
CHECK gate_after: human  →  DONE stage approval: true (or terminal gate)
```
在 M3 实现 workflow.yaml 可配置时统一术语，当前阶段保持此映射并在代码中注释说明。

### D6: mo approve CLI 直接调 HTTP API

**决策**: 新增 `mo issue approve <number>` 命令，调用 `POST /api/issues/:number/approve`。

**理由**: 与现有 issue 命令保持一致的 thin client 模式（CLI → HTTP API → server）。

## Risks / Trade-offs

**[内存 session 在 server 重启时丢失]** → 已知 M1 限制。approve 后如果 server 重启，无法恢复。M4 通过 session 持久化解决。当前阶段可通过文档说明规避。

**[paused session 内存占用]** → 单 issue 串行模式下只有一个 paused session，内存可忽略。如果后续有多 issue 并发需求，需要 session 超时清理机制。

**[paused session 清理策略（补充）]** → 需要明确的清理时机：
1. 正常完成（done）或出错时：从 AgentRunnerService.pausedSessions Map 中删除
2. Issue 关闭时：清理关联的 paused session（防止僵尸 session）
3. Server 启动时：清理所有残留的 paused session（防止重启后残留脏数据）
4. （可选）超时机制：paused session 保留 24 小时后自动清理

**[默认 workflow approval 位置语义]** → approval 在 "下一个要进入的阶段" 上，而非 "刚完成的阶段" 上。这与 PRD 的 gate_after 语义等价但表达不同。在 M3 实现 workflow.yaml 可配置时统一术语。
