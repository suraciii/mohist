## Context

当前 mohist 有三层嵌套：

```
Pipeline (程序)
  └── MainAgent (LLM orchestrator, runAgentLoop)
        ├── 决定调 execute_stage("plan")
        │     └── PlannerAgent (TypeScript 类, 多次 streamText)
        ├── 决定调 submit_approval
        ├── 决定调 execute_stage("build")
        │     └── RalphExecutor (程序, 循环 runAcpSession)
        └── 决定调 advance_stage("done")
```

问题：
1. MainAgent 在做不需要判断力的事（stage 转换顺序是固定的）
2. PlannerAgent 用一次 streamText 返回全部 JSON，tasks 看不到 specs 内容
3. 程序化 `generateTasksFromSpecs()` 丢失依赖、类型、AFK/HITL 标记
4. 三层嵌套增加了复杂度但没增加能力

## Goals / Non-Goals

**Goals:**
- Pipeline 由程序驱动，按固定顺序执行 stage，不需要 LLM 决策流转
- Plan 阶段通过**复用的 ACP 连接**按 artifact 分轮次生成，每轮只生成一个 artifact，会话上下文连续
- Build 阶段保持 `RalphExecutor` 不变（机械循环，不需要判断力）
- Gate（人类审批）由程序处理，不需要 LLM agent
- Explore 保持为独立能力，与 pipeline 解耦
- 删除 MainAgent、PlannerAgent、ReviewerAgent、所有 MainAgent tools

**Non-Goals:**
- 不改变 Build 阶段的 RalphExecutor 逻辑
- 不复用 openspec CLI
- 不改变 Review 阶段的核心审查逻辑（只是从 ReviewerAgent 类改为复用 ACP 连接）

## Decisions

### Decision 1: 删除 MainAgent，Pipeline 由程序驱动

Stage 转换顺序是固定的（plan → build → review → done），不需要 LLM 来决策。MainAgent 作为一个贯穿整个 workflow 的 LLM session，在做程序该做的事——决定调哪个 tool、什么时候推进 stage。这浪费 token 且不可靠。

```
之前: LLM 驱动 stage 流转
  MainAgent LLM 决定:
  → "plan 完成了" → advance_stage("build")
  → "build 完成了" → execute_stage("review")
  → "review 通过了" → advance_stage("done")

之后: 程序驱动 stage 流转
  Pipeline controller:
  → plan stage 完成 → 检查 gate → 用户批准 → 自动进 build
  → build stage 完成 → 自动进 review
  → review stage 完成 → 检查 gate → 用户批准 → 自动进 done
```

**Alternatives considered:**
- 保留 MainAgent 但简化 prompt → 仍然浪费 token 在不需要判断力的决策上
- 保留 MainAgent 仅用于 gate 交互 → gate 交互是程序化的，不需要 LLM

### Decision 2: Plan 阶段复用 ACP 连接，分轮次生成 artifacts

Pipeline 打开一个 ACP 连接，创建 session，然后按 artifact 顺序分轮次发送 prompt。每轮 prompt 只要求生成一个 artifact。由于 session 上下文连续，agent 在生成 specs 时已经天然知道 proposal 的内容，生成 tasks 时已经知道 proposal/specs/design 的内容。不需要由程序在每次 prompt 之间手动注入之前所有文件的内容。

```
Pipeline:
  conn = createAcpConnection({ cwd: worktreePath })

  // Round 1: proposal
  conn.prompt(buildArtifactPrompt('proposal', issue, changeDir))
  verifyFile(changeDir, 'proposal.md')

  // Round 2: specs (agent 在 session 中已看到 proposal)
  conn.prompt(buildArtifactPrompt('specs', issue, changeDir))
  verifyDir(changeDir, 'specs')

  // Round 3: design (agent 在 session 中已看到 proposal + specs)
  conn.prompt(buildArtifactPrompt('design', issue, changeDir))
  verifyFile(changeDir, 'design.md')

  // Round 4: tasks (agent 在 session 中已看到 proposal + specs + design)
  conn.prompt(buildArtifactPrompt('tasks', issue, changeDir))
  verifyFile(changeDir, 'tasks.json')

  // Round 5: self-review
  conn.prompt(buildSelfReviewPrompt(issue, changeDir))

  conn.close()
```

这比"一条超长 prompt"更可靠，因为：
- 每轮之后 Pipeline 可以验证文件是否存在
- 不需要在 prompt 里塞入所有 artifact 的 instruction，降低单轮长度

**失败策略：推倒重来。** 如果某轮失败或连接中断，pipeline 关闭当前连接，清理 changeDir（保留 `.openspec.yaml`），然后从 Round 1 重新开始。不做断点续传，因为 ACP 连接断开后对话历史丢失，新 session 无法复用前几轮的上下文。Plan stage 的 LLM 成本远低于 build stage，推倒重来的代价可接受。

**Alternatives considered:**
- 一条超长 prompt 让 agent 自己决定顺序 → agent 可能跳步或乱序，事后验证才能发现
- 每次新 `runAcpSession` + 程序手动注入上下文 → 没有真正复用会话上下文，且 prompt 会越来越长
- 断点续传（只重试失败轮次）→ ACP 连接断了后 session history 丢失，实际无法复用前序上下文，复杂度高且收益低

### Decision 3: 新增 AcpConnection 支持多轮 prompt

当前 `runAcpSession()` 是 oneshot 的：spawn → init → newSession → prompt → kill。需要新增 `createAcpConnection()` 返回一个可多次调用 `prompt()` 的对象。

```typescript
interface AcpConnection {
  prompt(text: string): Promise<AcpResult>
  close(): Promise<void>
}
```

实现要点：
- 复用 `ClientSideConnection` 和 `ndJsonStream`
- `prompt()` 调用 `connection.prompt({ sessionId, prompt })`
- `close()` 调用 cleanup + proc.kill
- 超时按单轮计算，而不是整个连接生命周期

### Decision 4: Artifact prompt 从 Markdown 文件组装

`buildArtifactPrompt(artifactType, issue, changeDir)` 从 `src/agents/prompts/artifacts/` 目录加载对应 artifact 的纯 Markdown instruction 文件。prompt 结构：

```
1. Issue 信息 (title, body, number)
2. Change 目录路径
3. 本轮目标: 生成 {artifactType}
4. {artifactType} instruction (从 artifacts/{type}.md 加载的纯 Markdown)
5. 提示: 你可以用 read_file 查看之前已生成的工件
```

文件结构：

```
src/agents/prompts/
├── artifacts/          ← 新目录，替代现有 YAML prompt 文件
│   ├── proposal.md     ← proposal artifact instruction
│   ├── specs.md        ← specs artifact instruction
│   ├── design.md       ← design artifact instruction
│   ├── tasks.md        ← tasks artifact instruction（含 mode/type/output/dependsOn 指导）
│   └── self-review.md  ← self-review instruction
└── review.md           ← review stage prompt（不在 artifacts/ 下，因为它不是 plan stage 的 artifact）
```

不用 YAML 模板引擎，直接 `fs.readFile()` + 拼接。动态内容（issue info, changeDir）在 `buildArtifactPrompt()` 里拼入。

Tasks instruction 增加 mohist 特有字段：`mode` (AFK/HITL)、`type` (WRITE/TEST/MIGRATE/CONFIG/REVIEW)、`output`、`dependsOn`。

### Decision 5: Review 阶段同样复用 ACP 连接

与 plan 阶段同构：打开 ACP 连接，发送 reviewer prompt，agent 在 session 内完成审查。删除 `ReviewerAgent` TypeScript 类。

`buildReviewerPrompt(issue, changeDir)` 包含：

```
1. Issue 信息
2. Change 目录路径
3. 审查标准（6-pass review + 跨切面审计）
4. Review report template
```

### Decision 6: Gate 由程序处理

Pipeline 在 plan 和 review stage 之后暂停，通过 `approvalState` 状态机等用户审批。

```
Gate 状态机:

  Pipeline.run(issue):
    while issue.stage !== done:
      switch issue.stage:
        case plan:
          runPlanStage()
          setApprovalState('awaiting', stage='plan')
          emit 'approval_requested'
          return  // pipeline 退出，等待外部触发 resume

        case build:
          runBuildStage()
          issue.stage = review  // build 后直接进 review，不停

        case review:
          runReviewStage()
          setApprovalState('awaiting', stage='review')
          emit 'approval_requested'
          return  // pipeline 退出

  CLI `mo issue approve`:
    update approvalState.status = 'approved'
    issue.stage = nextStage
    trigger pipeline resume  // 重新调用 Pipeline.run(issue)

  CLI `mo issue reject --message "..."`:
    update approvalState.status = 'rejected'
    addComment(issue, message)
    // plan reject: issue.stage 不变，重跑 plan
    // review reject: issue.stage = build，从 build 重新开始
    trigger pipeline resume  // 重新调用 Pipeline.run(issue)
```

关键变化：没有"resume LLM session"，只有"重新启动 pipeline"。`AgentRunnerService` 维护 `pendingGates` 而非 `pausedSessions`。

Pipeline 启动入口：

```
mo issue start 42
  → POST /issues/42/start (HTTP API)
    → AgentRunnerService.start(issue, ...)
      → executeAgent()
        → Pipeline.run(issue)          ← 替代 runMainAgent()
          → runPlanStage() → gate → return

mo issue approve 42
  → POST /issues/42/approve (HTTP API)
    → AgentRunnerService.resume(issue, ...)
      → Pipeline.run(issue)            ← 重新调用，从 build 开始
        → runBuildStage() → runReviewStage() → gate → return
```

### Decision 7: Explore 作为独立能力

`talks/2026-04-15-workflow-alignment.md` 定义 Explore 是"独立能力，不是 stage"。新架构下 Explore 完全独立于 pipeline：

```
入口 1: mo explore 或 web UI "新建 Issue + Explore"
  → ExploreService.run(issueTitle)
  → runAcpSession({ task: explorePrompt })
  → 产出 proposal.md
  → 创建 Issue，stage=draft/plan

入口 2: mo explore 42 或 web UI "在 Issue #42 下补充 Explore"
  → ExploreService.runOnIssue(issue)
  → runAcpSession({ task: explorePrompt + 已有 proposal })
  → 更新 proposal.md
```

ExploreService 是轻量级的 `runAcpSession` 封装，不需要 LLM orchestrator。

### Decision 8: 脏 artifact 处理策略

如果 plan stage 的 ACP 连接在某轮中断，磁盘上可能残留部分 artifacts。重新运行 plan stage 前，pipeline SHALL 清理 changeDir 下除 `.openspec.yaml` 外的所有文件，确保 clean start。

```
runPlanStage():
  changeDir = getChangeDir(issue.number) || createChangeDir(issue.number, issue.title)
  cleanChangeDir(changeDir)  // 保留 .openspec.yaml
  conn = createAcpConnection(...)
  // 分轮次生成...
  conn.close()
  verifyArtifacts(changeDir)
```

## Risks / Trade-offs

- **[ACP 连接稳定性]** → 多轮 prompt 意味着连接需要保持 10-20 分钟。如果 opencode 进程崩溃，需要捕获错误并重新建立连接（从断点重试当前轮次）。
- **[单轮超时]** → 每轮 prompt 的超时可能需要单独配置（如 10-15 分钟），而不是整个 session 的 30 分钟。
- **[删除代码量大]** → MainAgent、PlannerAgent、ReviewerAgent、agent-loop、8+ 个 tools。但删除的是复杂度，不是能力。
- **[Gate 交互机制重设计]** → 当前 gate 通过 MainAgent 的 session pause/resume 实现。改为 pipeline 的 issue-level state machine 后，`AgentRunnerService` 和 CLI 命令都需要重写。这是最大的工程风险。
- **[ExploreService 新增]** → Explore 从 MainAgent 的一部分变为独立 service，需要把 Explore 相关的 prompt 和交互逻辑抽出来。
