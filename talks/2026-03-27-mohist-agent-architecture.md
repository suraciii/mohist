# Mohist Agent Architecture Exploration

Date: 2026-03-27

## Core Insight: Mohist Is an AI Agent, Not a Deterministic Engine

### Mental Model Evolution

The conversation went through 5+ major pivots:

1. **Initial idea**: Make the pipeline configurable → User clarified: not about configurability, about **intelligence**
2. **Then**: Agent-driven workflow where opencode reads workflow YAML → User corrected: **Mohist should be the one reading the workflow, not opencode**
3. **Then**: Mohist as deterministic engine + opencode reports outcomes via MCP → User corrected: **Mohist has its own LLM, it's an agent itself**, not a dumb engine
4. **Then**: Mohist directly reads code → User corrected: **Mohist doesn't read code directly**, it delegates to specialized agents (explore agent, review agent, etc.)
5. **Then**: Mohist as opencode sub-agent → User corrected: **Mohist is independent**, same level as opencode, with its own embedded LLM calls

### Final Architecture

```
Mohist Agent (LLM + Tool Loop, independent process)
├── Reads workflow.yaml
├── Understands current stage & context
├── Makes decisions (advance, block, retry)
├── Spawns task agents via spawn_agent() tool
│   ├── explore agent (opencode --agent explore)
│   ├── design agent (opencode --agent build or custom)
│   ├── coding agent (opencode --agent build)
│   ├── review agent (opencode --agent general, read-only)
│   └── merge agent (could be opencode or script)
├── Interacts with humans (ask_user, block for approval)
└── Manages state in SQLite
```

## Mohist Is Like Openclaw, Not Like a Task Queue

### Key Realization

Mohist follows the **openclaw pattern**: a long-running server with per-entity (issue) agent sessions that have their own LLM loop, can be paused at gates, and resumed after server restart.

```
Openclaw pattern:
  Server (long-running)
    ├── Main Agent (LLM loop, per session)
    │     ├── receives user message → LLM decides
    │     ├── spawns subagent → waits for auto-announce
    │     ├── LLM analyzes result → continues conversation
    │     └── session can pause/resume
    ├── Session Store (memory + metadata persistence)
    └── Subagent Registry (tracks subagent lifecycle)

Mohist pattern (same shape):
  Server (long-running)
    ├── Mohist Agent (LLM loop, per issue)
    │     ├── reads workflow + issue → LLM decides
    │     ├── spawns opencode → waits for exit → reads output
    │     ├── LLM evaluates result → advance/retry/wait for human
    │     └── session can pause (gate) / resume (server restart)
    └── SQLite (issue state, session messages, execution records)
```

### Mapping Table

| Openclaw | Mohist |
|----------|--------|
| A chat session | An issue's workflow session |
| User message triggers agent | Issue creation / human approve triggers agent |
| Subagent (same LLM, different prompt) | opencode subprocess (different tool) |
| Auto-announce back to parent session | Synchronous wait for exit, read stdout |
| Channel (Telegram/Slack) | CLI / HTTP API |
| Session metadata persistence | SQLite persistence |

## Openclaw Architecture Analysis

### Three-Layer Separation

```
┌─────────────────────────────────────────────┐
│  ACP Server (long-running, HTTP interface)   │
│  ├── Session Store (memory, idle TTL)        │
│  └── Control Plane Manager (singleton)       │
│       ├── Runtime Cache (cache active runs)  │
│       ├── Actor Queue (serialize per session)│
│       └── Active Turn Map (running turns)    │
├─────────────────────────────────────────────┤
│  ACP Runtime (pluggable backend)             │
│  ├── ensureSession() — create/resume        │
│  ├── runTurn() — execute one turn            │
│  ├── cancel() — cancel current turn          │
│  └── close() — close session                 │
├─────────────────────────────────────────────┤
│  Subagent Registry (lifecycle management)     │
│  ├── register/track/resume/cleanup           │
│  ├── event-driven (lifecycle events)         │
│  ├── persist to disk (survive restart)       │
│  └── orphan recovery                         │
└─────────────────────────────────────────────┘
```

### Session Store Design (`src/acp/session.ts`)

- Pure **in-memory** `Map<string, AcpSession>`
- Idle TTL (default 24h): periodic cleanup of expired sessions
- Capacity limit (default 5000): evict oldest idle session
- Each session holds an `AbortController` for cancellation
- **Not persisted**: sessions lost on restart (but metadata can rebuild state)

### Session Manager (Control Plane, `src/acp/control-plane/manager.core.ts`)

- **Actor Queue**: serializes all operations for the same sessionKey
- **Runtime Cache + lazy rebuild**: cache runtime handles, health-check via `getStatus()`
- **Turn lifecycle**: `runTurn() → setSessionState("running") → runtime.runTurn() → setSessionState("idle")`
- Timeout control: configurable per-session
- AbortController dual-layer: caller signal + internal signal
- **Oneshot mode**: auto-close after completion
- **Persistent mode**: runtime stays alive, reuse handle

### Subagent Spawning (`src/agents/subagent-spawn.ts`)

Flow:
1. Validate (depth limit, concurrency limit, agentId whitelist)
2. Create `childSessionKey: "agent:{id}:subagent:{uuid}"`
3. Patch session metadata (depth, model, capabilities)
4. Build system prompt (includes requester context)
5. `callGateway("agent")` to start agent run
6. `registerSubagentRun()` → register in memory + persist
7. Return accepted, wait for auto-announce

### Auto-announce Pattern

- Subagent does **NOT return results to caller directly**
- Completion is pushed via **lifecycle events** to parent session
- Parent session's LLM receives results as "user messages"
- `SUBAGENT_SPAWN_ACCEPTED_NOTE`: "Wait for completion events to arrive as user messages"

### Recovery After Restart

```
initSubagentRegistry() →
  1. restoreSubagentRunsFromDisk() — restore from file
  2. reconcileOrphanedRestoredRuns() — handle orphans
  3. resumeSubagentRun(runId) — resume each
     → not finished: re-wait for completion
     → finished but not announced: trigger announce
```

## Openclaw vs Mohist: Key Differences

Mohist is **much simpler** than openclaw:

| Aspect | Openclaw | Mohist |
|--------|----------|--------|
| Subagent results | Async auto-announce | Sync wait for exit |
| Subagent tracking | Subagent Registry (complex) | Simple: spawn → wait → done |
| Runtime reuse | Runtime Cache | No reuse, new process each time |
| Session model | Multi-user chat session | Per-issue workflow session |
| Serialization | Actor Queue per sessionKey | Serial per issue (simpler) |
| LLM calls | Via `@mariozechner/pi-coding-agent` library | Via Vercel AI SDK `streamText()` |

## Openclaw vs Mohist: What to Borrow

### 3 Core Patterns

1. **Agent Loop Pattern**: async generator with streaming events, tool calling cycle
2. **Intermittent Session Pattern**: server restart → restore from DB → resume where left off
3. **Abort + Timeout Pattern**: dual-layer AbortController, grace period cleanup

## OpenCode Architecture Analysis (Reference)

### Project Structure

OpenCode is a Bun-based monorepo using Turborepo. Core is `packages/opencode/src/` with 43 subdirectories.

Key tech stack:
- Runtime: Bun
- AI SDK: Vercel AI SDK v5 (`ai` package)
- Effect: Effect.js v4 (services, DI)
- DB: SQLite via Drizzle ORM
- HTTP: Hono
- Schema: Zod v4

### Agent Loop (Two-Level)

```
SessionPrompt.loop()              ← outer: runs until LLM stops
  ├── load messages (SQLite)
  ├── handle subtasks (child sessions)
  ├── handle compaction (context overflow)
  └── SessionProcessor.process()  ← inner: single LLM stream
        └── AI SDK streamText()   ← innermost: tool loop (built-in)
              ├── LLM returns tool_call → auto execute → feed back
              └── LLM returns text → done
```

The AI SDK's `streamText()` handles the tool-calling cycle internally.

### LLM Provider Abstraction

Uses Vercel AI SDK v5 as unified abstraction. Supports 18+ providers:
- Anthropic, OpenAI, Google, Google Vertex, Amazon Bedrock, Azure, xAI, Mistral, Groq, etc.

Configuration via `opencode.json`: `model: "anthropic/claude-sonnet-4"`

### Tool System

Tools defined via `Tool.define()` with Zod schema validation. Available tools:
bash, read, write, edit, multiedit, ls, glob, grep, task (subagent), todowrite, webfetch, websearch, skill, etc.

### Session Management

- SQLite (Drizzle ORM) + Event Sourcing
- Messages have roles (user/assistant) and Parts (text, tool, reasoning, step-start, snapshot, etc.)
- Compaction: when tokens approach context limit, auto-summarize old messages
- Session resumption: load messages from DB → rebuild context → continue loop

## Design Decisions

### 1. LLM: Independent (like opencode/openclaw)

Use **Vercel AI SDK v5** directly. This gives:
- Automatic support for all mainstream providers
- `streamText()` built-in tool calling cycle
- Same configuration pattern as opencode

### 2. Interactive Stages (User Interaction)

Three modes, prioritize CLI first:

| Mode | Implementation | Use Case |
|------|---------------|----------|
| CLI interaction | `mo attach 42` → stdin/stdout | Local dev (first) |
| Issue Comments | Mohist adds comments, user replies | GitHub integration (later) |
| HTTP SSE | WebSocket/SSE real-time | Web UI (later) |

Underlying: `ask_user()` tool → write to SQLite → CLI polls → user types → write back → agent loop resumes.

### 3. Multi-Issue Concurrency

**Per-issue agent session** (like openclaw's per-session model):
- Each active issue gets its own agent session with its own LLM loop
- No worker pool needed
- Current WorkflowEngine + TaskRepo + multi-worker model all deleted

```
Mohist Server
├── Issue #42 → Session { agent loop, abortController }
├── Issue #43 → Session { agent loop, abortController }
├── Issue #44 → (gate, waiting for human) → no session
└── Issue #45 → (pending, queued)
```

### 4. workflow.yaml: Per-Project

Located at `.mohist/workflow.yaml` in project root (same level as `.opencode/`).

```yaml
name: default
version: "1.0"

stages:
  - id: explore
    name: "探索"
    description: |
      探索 issue 的需求，理解代码库上下文，提出澄清问题。
    agent:
      type: opencode
      agent_type: explore
      timeout: 600
    outputs:
      - issue comment: 初步理解和问题列表
    entry_condition:
      - issue 被创建

  - id: refine
    name: "细化"
    description: |
      基于探索结果，和用户对话细化需求。
    type: interactive
    entry_condition:
      - 探索阶段完成
      - 用户说 "可以规划了" 或需求中有 >= 2 个 checkbox task

  - id: design
    name: "设计"
    description: |
      基于细化后的需求，生成设计文档。
    agent:
      type: opencode
      agent_type: build
      timeout: 1800
    outputs:
      - file: openspec/changes/issue-{N}/design.md
    entry_condition:
      - 细化阶段确认

  - id: dev
    name: "开发"
    description: |
      按照设计文档实现代码。
    gate: true
    agent:
      type: opencode
      agent_type: build
      timeout: 3600
    outputs:
      - file: 代码变更 (commits)
      - pr: draft PR
    entry_condition:
      - 设计完成
      - 人类确认

  - id: review
    name: "审查"
    description: |
      自动审查代码质量，等待人类审查。
    agent:
      type: opencode
      agent_type: general
      timeout: 600
      read_only: true
    outputs:
      - issue comment: 审查结果
    entry_condition:
      - 开发完成

  - id: done
    name: "完成"
    description: |
      合并 PR，关闭 Issue。
    agent:
      type: script
      script: "gh pr merge {PR_NUMBER}"
    entry_condition:
      - 审查通过
      - 人类确认合并
```

Key design: workflow.yaml is read by Mohist's **LLM**, not by code logic. Conditions are natural language, LLM judges whether they're met.

### 5. Error Recovery (LLM-Driven)

Since Mohist has an LLM, recovery is intelligent (not fixed rules):

```
Subagent fails
  ↓
Mohist Agent Loop receives { exitCode: 1, stderr: "..." }
  ↓
LLM analyzes failure cause (reads stderr + worktree state)
  ↓
LLM decides:
  ├── "transient error" → retry (maybe modify prompt)
  ├── "unclear requirements" → ask_user("subagent failed because...")
  ├── "design has issues" → rollback to design stage
  └── "unrecoverable" → mark issue as blocked, notify user
```

## Current Mohist Codebase Assessment

### What Can Be Reused (~60%)

- `db/` — SQLite + better-sqlite3 + repos (database, migrations, issue-repo, project-repo, config-repo, comment-repo, label-repo)
- `git/` — WorktreeManager
- `server/` — HTTP server (Express)
- `api/` — REST routes
- `cli/` — Commander CLI
- `providers/` — Issue source interface (local provider)
- `utils/` — slugify

### What Needs Replacement (~40%)

| Current | Replace With |
|---------|-------------|
| `types/index.ts` — Stage enum (6 hardcoded) | Dynamic stages from workflow.yaml |
| `workflow/issue-workflow.ts` — Hardcoded state transitions | workflow.yaml + LLM judgment |
| `workflow/stage-handlers.ts` — 2 hardcoded handlers | Mohist Agent dynamic decisions |
| `workflow/engine.ts` — Multi-worker polling engine | SessionManager (per-issue sessions) |
| `agent/prompts.ts` — 3 static prompt templates | LLM dynamically builds prompts |
| `agent/runner.ts` — spawn logic (keep spawn mechanism, change caller) | Called as a tool by Mohist Agent |
| `services/workflow-service.ts` — Workflow orchestration | Replaced by Mohist Agent |
| `Task` type + `TaskRepo` | Simplified to `Execution` records |

### What Needs To Be Added

| New Component | Reference |
|--------------|-----------|
| `MohistAgent` (core agent loop) | openclaw `runEmbeddedPiAgent()` + AI SDK `streamText()` |
| `ToolRegistry` + tool definitions | opencode `tool/registry.ts` + `tool/tool.ts` |
| `SessionManager` (per-issue) | openclaw `src/acp/control-plane/manager.core.ts` (simplified) |
| `WorkflowLoader` (parse workflow.yaml) | New, follow opencode config loading pattern |
| `LLMProvider` (provider config) | AI SDK native (`@ai-sdk/anthropic`, `@ai-sdk/openai`, etc.) |
| `MessageStore` (SQLite messages) | Simplified version of opencode's message model |

## Mohist Tools Design

```
Mohist Agent Tools
├── spawn_agent(agent_type, prompt, timeout, cwd)
│   └── spawn opencode subprocess, wait for exit, return stdout/stderr/exit_code
│
├── read_file(path)
│   └── read file in worktree
│
├── list_files(path, pattern?)
│   └── list files in worktree
│
├── ask_user(question)
│   └── send question to user, block until reply
│   └── user replies via API, Mohist agent loop resumes
│
├── advance_stage(stage_id)
│   └── update issue stage, save to SQLite
│
├── add_comment(body)
│   └── add comment to issue
│
├── get_issue()
│   └── read current issue state
│
└── get_workflow()
    └── read workflow.yaml definition
```

## Context Passing Between Stages

Context passes through **worktree files + issue comments**, not memory or DB fields:

```
explore → refine → design → dev → review
  ↑           ↑         ↑       ↑       ↑
  comments    comments  files   commits  diff
```

Each subagent is short-lived. Context is rebuilt from filesystem each time.

## Updated Mohist Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  Mohist Server (long-running process)                            │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Mohist Agent Core                                         │  │
│  │                                                            │  │
│  │  ┌──────────────┐    ┌──────────────────────────────────┐  │  │
│  │  │ LLM Layer     │    │ Tool Layer                       │  │  │
│  │  │ (Vercel AI    │    │                                  │  │  │
│  │  │  SDK v5)      │←──│  spawn_agent → opencode subprocess│  │  │
│  │  │               │    │  ask_user → wait for human reply │  │  │
│  │  │ streamText()  │    │  advance_stage → update SQLite   │  │  │
│  │  │ + maxSteps    │    │  read/list files                 │  │  │
│  │  │ + tool loop   │    │  add_comment → issue comment     │  │  │
│  │  │               │    │  get_issue / get_workflow        │  │  │
│  │  └──────────────┘    └──────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Session Manager (per-issue)                                │  │
│  │                                                            │  │
│  │  Issue #42 → AgentSession { messages[], state, abort }     │  │
│  │  Issue #43 → AgentSession { messages[], state, abort }     │  │
│  │  Issue #44 → (gate, not started)                           │  │
│  │                                                            │  │
│  │  Server restart → restore from SQLite → re-evaluate → cont │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Storage (SQLite)                                          │  │
│  │                                                            │  │
│  │  issues table     → issue state (title, body, stage, ...)  │  │
│  │  sessions table   → agent session (messages, state, ...)   │  │
│  │  projects table   → project config (path, ...)             │  │
│  │  executions table → subagent execution records              │  │
│  └────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

## Two-Layer Agent Architecture

### Key Insight: Main Agent + Job Agents

Mohist has a two-layer agent architecture:

```
┌──────────────────────────────────────────────────────────────┐
│  Mohist Server                                               │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  Main Agent (长 session, per issue)                    │  │
│  │                                                        │  │
│  │  职责: 流程编排 (Orchestrator)                         │  │
│  │  ├── 读 workflow.yaml                                 │  │
│  │  ├── 知道当前在哪个阶段                                │  │
│  │  ├── 决定下一步做什么                                  │  │
│  │  ├── 构建子 agent 的 prompt                            │  │
│  │  ├── 评估子 agent 的产出                               │  │
│  │  ├── 决定推进/重试/阻塞                                 │  │
│  │  └── 和人类交互                                        │  │
│  │                                                        │  │
│  │  特点: 不直接操作代码                                  │  │
│  │  模型: 可用便宜/快的模型 (haiku/sonnet)                 │  │
│  │  工具: spawn_agent, ask_user, advance_stage, ...       │  │
│  │  Session: 持久化到 SQLite, server 重启可恢复           │  │
│  │  生命周期:                                             │  │
│  │    issue 创建 → session 启动                           │  │
│  │    gate → session 暂停 (保存到 DB)                     │  │
│  │    人类操作 → session 恢复                              │  │
│  │    issue 完成 → session 结束                            │  │
│  └───────────────────────┬────────────────────────────────┘  │
│                          │                                   │
│              spawn (每次一个, 同步等退出)                     │
│                          │                                   │
│          ┌───────────────┼───────────────┐                   │
│          ▼               ▼               ▼                   │
│     ┌─────────┐    ┌─────────┐    ┌─────────┐              │
│     │ Job 1   │    │ Job 2   │    │ Job 3   │  ...         │
│     │ Explore │    │ Design  │    │  Code   │              │
│     │         │    │         │    │         │              │
│     │opencode │    │opencode │    │opencode │              │
│     │子进程   │    │子进程   │    │子进程   │              │
│     │         │    │         │    │         │              │
│     │ 独立    │    │ 独立    │    │ 独立    │              │
│     │ session │    │ session │    │ session │              │
│     └─────────┘    └─────────┘    └─────────┘              │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

### Main Agent Session: B (Long Session)

Main Agent uses a persistent session (like openclaw's main session):
- Has full conversation history within an issue's lifecycle
- Can handle multi-turn interaction (gate pause/resume)
- Doesn't need opencode's code tools (read/edit/write), only orchestration tools
- Persisted to SQLite, survives server restart

### Three-Layer Responsibility Split

```
┌─── Human defines (workflow.yaml) ────────────────────────────┐
│                                                             │
│  "有什么阶段、什么顺序、用什么工具、                         │
│   期望什么产出、哪些需要人类确认"                            │
│                                                             │
├─── Code executes (Mohist Server) ───────────────────────────┤
│                                                             │
│  "读取 workflow.yaml，按类型执行：                           │
│   agent → spawn 子代理                                      │
│   gate → 暂停/恢复 session                                  │
│   interactive → 进入对话模式                                 │
│   收到外部事件 → 注入 session → 唤醒 loop"                  │
│                                                             │
├─── LLM decides (Main Agent) ────────────────────────────────┤
│                                                             │
│  "判断当前该做什么：                                         │
│   构建子代理 prompt、评估产出质量、                          │
│   决定是否推进、决定是否重试、                               │
│   决定怎么和人类沟通"                                        │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Revised workflow.yaml (No Events, Two Audiences)

```yaml
stages:
  - id: explore
    name: "探索"
    type: agent
    agent:
      type: opencode
      agent_type: explore
      timeout: 600
    expects:
      - "初步理解 issue 需求"
      - "列出需要澄清的问题（如果有）"

  - id: refine
    name: "细化"
    type: interactive
    expects:
      - "需求清晰，包含可执行的 checkbox 任务"

  - id: design
    name: "设计"
    type: agent
    agent:
      type: opencode
      agent_type: build
      timeout: 1800
    expects:
      - "design.md 包含问题分析和方案描述"
    gate_after: true

  - id: dev
    name: "开发"
    type: agent
    agent:
      type: opencode
      agent_type: build
      timeout: 3600
    expects:
      - "代码实现完成"
      - "测试通过"
      - "Draft PR 已创建"

  - id: review
    name: "审查"
    type: agent
    agent:
      type: opencode
      agent_type: general
      timeout: 600
      read_only: true
    expects:
      - "代码审查完成"
    gate_after: true
```

Key changes from earlier version:
- **No `on: <event>`** — transition logic entirely by LLM
- **No `entry_condition`** — LLM judges whether prerequisites are met
- **`type: agent | interactive | gate`** — for **code** to read (controls execution behavior)
- **`expects`** — for **LLM** to read (used to evaluate output quality)
- **`agent` config** — for **code** to read (used to build spawn command)
- **`gate_after: true`** — for **code** to read (triggers session pause)

## Event-Driven Architecture (Revised Understanding)

### Core Insight: Events Are Transport, Not Rules

After analyzing openclaw's event system:

```
openclaw 的模式:
  事件 → 运输到 LLM → LLM 决策
  代码不做决策，代码只做运输。
```

### Two Types of "Events" in Mohist

```
┌─── Session 内事件（工具调用返回值） ──────────────────────┐
│                                                           │
│  spawn_agent 返回 → LLM 读结果 → 决定下一步              │
│  read_file 返回 → LLM 用内容 → 继续                       │
│  advance_stage 返回 → LLM 知道阶段变了 → 继续             │
│                                                           │
│  不是"事件"，是工具调用的返回值                            │
│  AI SDK 的 streamText() 自动处理                          │
│                                                           │
├─── 外部事件（从 session 外部注入的） ─────────────────────┤
│                                                           │
│  人类 approve    → 注入 user message → LLM 决策           │
│  人类 comment    → 注入 user message → LLM 决策           │
│  人类 reject     → 注入 user message → LLM 决策           │
│  人类 pause      → session 暂停（不经过 LLM）             │
│  Server restart  → 恢复 session → LLM 重新评估            │
│                                                           │
│  真正的"事件"，需要注入到 session 中                      │
│                                                           │
└───────────────────────────────────────────────────────────┘
```

Mohist does NOT need a complex event system. Only two mechanisms:
1. **Tool call return values** (AI SDK built-in) — intra-session "events"
2. **External message injection** (write to session messages, trigger agent loop resume) — extra-session "events"

### Openclaw Event System Analysis

Openclaw uses callback-based listeners (not an explicit event bus):
- `src/infra/agent-events.ts` — primary event bus via `Set<listener>` on `globalThis`
- `src/sessions/session-lifecycle-events.ts` — session-level lifecycle changes
- `src/agents/subagent-lifecycle-events.ts` — type definitions for subagent reasons/outcomes

Key event types:
- `lifecycle:start|end|error|fallback` — agent run phases
- `assistant` — streaming content
- `tool` — tool execution events

Subagent completion flow (most complex chain):
```
lifecycle:end → Subagent Registry → completeSubagentRun()
  → build Internal Event → deliver to parent session
  → parent session LLM receives as "user message"
```

### OpenSpec Progress System (Reference)

OpenSpec tracks progress via **filesystem-derived state** (no database):
- Artifact completion: check if file exists on disk
- Task progress: count checkboxes in `tasks.md` or `passes: true` in `prd.json`
- CLI commands: `openspec list`, `openspec status --change <id>`, `openspec view`

Ralph (autonomous agent) updates progress by:
- Setting `passes: true` in `prd.json` for completed tasks
- Appending entries to `progress.txt` with timestamps
- Outputting `<promise>COMPLETE</promise>` sentinel when all done

## Workflow Event Log (Progress Tracking)

### Design: Append-Only Event Log in SQLite

A single `workflow_events` table stores the complete execution history for visualization and auditing.

```
workflow_events (Append-only event log)

| id | issue_id | timestamp | event_type | stage | data (JSON) |
|----|----------|-----------|------------|-------|-------------|
| 1  | 42       | 10:00     | stage_enter| explore | {} |
| 2  | 42       | 10:00     | agent_spawn| explore | {type:"opencode/explore", prompt:"..."} |
| 3  | 42       | 10:20     | agent_done | explore | {exit_code:0, duration_ms:120000} |
| 4  | 42       | 10:21     | decision   | explore | {action:"advance", reason:"..."} |
| 5  | 42       | 10:22     | stage_exit | explore | {status:"completed"} |
| 6  | 42       | 10:25     | stage_enter| design  | {} |
| 7  | 42       | 10:25     | agent_spawn| design  | {type:"opencode/build", prompt:"..."} |
| 8  | 42       | 10:35     | agent_done | design  | {exit_code:1, duration_ms:600000, stderr:"..."} |
| 9  | 42       | 10:36     | decision   | design  | {action:"retry", reason:"..."} |
| 10 | 42       | 10:40     | agent_spawn| design  | {type:"opencode/build", prompt:"...(modified)"} |
| 11 | 42       | 10:55     | agent_done | design  | {exit_code:0, duration_ms:900000} |
| 12 | 42       | 10:56     | decision   | design  | {action:"wait", reason:"等待人类确认"} |
| 13 | 42       | 10:56     | stage_exit | design  | {status:"waiting_approval"} |
| 14 | 42       | 11:30     | human_action| design | {type:"approve"} |
| 15 | 42       | 11:30     | stage_enter| dev     | {} |
```

### Event Types

```
┌─── 阶段生命周期 ────────────────┐
│  stage_enter                    │
│  stage_exit                     │
└─────────────────────────────────┘

┌─── 子代理执行 ──────────────────┐
│  agent_spawn                    │
│  agent_done                     │
└─────────────────────────────────┘

┌─── Main Agent 决策 ────────────┐
│  decision                      │
│  (action: advance|retry|wait|rollback|abort) │
└─────────────────────────────────┘

┌─── 人类交互 ────────────────────┐
│  human_action                  │
│  (type: approve|reject|comment|pause|resume) │
└─────────────────────────────────┘

┌─── 产出 ────────────────────────┐
│  artifact_created              │
│  (type: file|comment|pr|commit) │
└─────────────────────────────────┘
```

### Event Writers

```
Code 自动写入:
  stage_enter  → spawn 前
  stage_exit   → spawn 后 / gate 暂停后
  agent_spawn  → spawn 调用时
  agent_done   → 子进程退出时

Main Agent 通过工具写入:
  decision     → Main Agent 调 decision 工具
  artifact_created → Main Agent 调 log 工具

API 写入:
  human_action → 人类通过 CLI/API 操作时
```

### Visualization Queries

All visualization derived from the append-only log:
- **Timeline**: `SELECT * WHERE issue_id=42 ORDER BY timestamp`
- **Stage progress**: derived from `stage_enter`/`stage_exit` events
- **Stage detail**: group by stage, count attempts
- **Sub-agent records**: pair `agent_spawn` + `agent_done`
- **Duration stats**: `agent_done.duration_ms` aggregation

### Storage: SQLite

Confirmed: all workflow events stored in SQLite (not filesystem).
- Already have SQLite infrastructure in current codebase
- Flexible queries (by issue, time, type)
- Supports aggregation (count, avg duration)
- Worktree files store artifacts (for git tracking), SQLite stores metadata

## Open Questions

- Session message persistence: how much history to keep? When to compact?
- System prompt design: what does Mohist's system prompt look like?
- How does `mo attach 42` work technically (stdin/stdout vs HTTP)?
- Should Mohist have its own config file (`~/.mohist/config.json`) for LLM provider settings?
- How to handle the first run (no issues yet, workflow not defined)?
- `type: interactive` stage: does code auto-enter dialog mode, or does LLM decide when to call `ask_user()`?
- Session duration: short issues (~10-15 messages) vs long issues (needs compaction?)
