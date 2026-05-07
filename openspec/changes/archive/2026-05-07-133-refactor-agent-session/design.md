## Context

`acp-session.ts` (954 lines) is a god module containing two near-identical functions — `runAcpSession` (lines 98–484) and `createAcpConnection` (lines 517–954) — that share 90% of their code. Both spawn an opencode subprocess, establish ACP protocol over stdio JSON-RPC, handle session lifecycle, and dispatch events. The only semantic difference: `runAcpSession` calls prompt once then closes; `createAcpConnection` returns a `{ prompt, close }` handle for multi-round use.

Alongside this, three other session abstractions exist with no connection to reality:
- `SessionManager` (`session.ts`) — in-memory `Map` for "Main Agent" conversations, never used by ACP sessions
- `AgentSessionMessageRepo` — table + repo with zero callers of `insert()`
- `AgentRunnerService.runningSlots` — in-memory `Map` tracking running tasks, disconnected from process PIDs

The ACP protocol layer directly depends on `EventBus`, `WorkflowLogRepo`, `SessionStreamLogRepo`, and `CoderSessionRepo` — preventing clean reuse by non-workflow callers (SkillService, ExploreService).

**Constraints:**
- ACP SDK API (`ClientSideConnection`, `ndJsonStream`) must not be modified
- External SSE events (`coder_text_chunk`, `coder_tool_call`, `coder_session_started`) must remain identical — the Web UI consumes them
- `coder_session` table schema is in production; changes must be additive migrations
- This is a pure refactor — no new user-facing features

## Goals / Non-Goals

**Goals:**
- Eliminate `runAcpSession` / `createAcpConnection` duplication via a single `AgentSession` abstraction
- Move EventBus/repo side-effects out of `acp-session.ts` into pluggable observers
- Remove dead code: `SessionManager`, `AgentSessionMessageRepo`, `agent_session_message` table
- Centralize session state in `coder_session` table via a typed state machine
- Split `acp-session.ts` (954 lines) into focused modules each < 300 lines

**Non-Goals:**
- No connection pooling or `AgentSessionPool`
- No frontend changes — backend SSE events remain wire-compatible
- No changes to RalphExecutor's per-task-isolated-process strategy
- No `session_stream_log` schema changes (it's an append-only event log, works fine)

## Decisions

### D1: `withSession()` replaces `runAcpSession`; `AgentSession.create()` replaces `createAcpConnection`

The two existing functions differ only in lifecycle ownership. `runAcpSession` is sugar for "create → prompt → close." Rather than maintaining two entry points, we provide:
- `AgentSession.create(options)` — returns a session handle with `execute(prompt)` / `close()` / `state` (replaces `createAcpConnection`)
- `withSession(options, fn)` — auto-manages create/fn(close), replaces `runAcpSession`

```typescript
export class AgentSession {
  static async create(options: AgentSessionOptions): Promise<AgentSession>
  async execute(prompt: string): Promise<AcpSessionResult>
  async cancel(): Promise<void>
  async close(): Promise<void>
  get state(): SessionState
  get acpSessionId(): string
}

export async function withSession<T>(
  options: AgentSessionOptions,
  fn: (session: AgentSession) => Promise<T>
): Promise<T>
```

**Alternatives considered:**
- *Keep both functions, extract shared logic into helpers* — still two public APIs to maintain, doesn't solve the conceptual split
- *Make `AgentSession` an interface with multiple implementations* — over-engineered; there's only one kind of session (ACP over stdio)

### D2: Three-layer split — Process / Protocol / Session

```
acp-process.ts      → AcpProcess (spawn, kill, PID, exit/error events)
acp-protocol.ts     → AcpProtocol (initialize, newSession, prompt, cancel → emits SessionEvents)
agent-session.ts    → AgentSession (combines both, manages state, dispatches to observers)
```

**Layer responsibilities:**
- **AcpProcess**: Owns the `child_process.ChildProcess`. Handles spawn args, env sanitization, SIGTERM→SIGKILL escalation, stdin/stdout stream lifecycle. Emits `process_error`, `process_exit` events. ~120 lines.
- **AcpProtocol**: Owns the `ClientSideConnection` + ndJsonStream. Translates ACP protocol calls (`initialize`, `newSession`, `prompt`, `cancel`, `setSessionConfigOption`) and `sessionUpdate` notifications into a typed `SessionEvent` stream. No knowledge of Mohist business logic. ~250 lines.
- **AgentSession**: Composes AcpProcess + AcpProtocol. Manages `SessionState` transitions, aggregates `agentText`, dispatches to `SessionObserver[]`. This is the only layer that touches `coder_session` repo and observers. ~250 lines.

**Alternatives considered:**
- *Two-layer split (Protocol + Session)* — Process lifecycle is complex enough (SIGTERM/SIGKILL timing, stream cleanup, EPIPE handling) to justify its own module
- *Keep it as one file with extracted helpers* — doesn't reduce the cognitive load of a 950-line file

### D3: `SessionObserver` interface for side-effect decoupling

```typescript
export interface SessionObserver {
  onSessionStart?(ctx: SessionContext): void;
  onTextChunk?(ctx: SessionContext, text: string): void;
  onToolCall?(ctx: SessionContext, event: ToolCallEvent): void;
  onSessionEvent?(ctx: SessionContext, eventType: string, data: unknown): void;
  onStateChange?(ctx: SessionContext, from: SessionState, to: SessionState): void;
}

export interface SessionContext {
  readonly issueId: string;
  readonly issueNumber: number | undefined;
  readonly projectId: string;
  readonly executionId: string | undefined;
  readonly acpSessionId: string;
  readonly coderSessionId: string | undefined;
  readonly stage: string | undefined;
  readonly model: string | undefined;
}
```

`WorkflowSessionObserver` implements this interface and contains all the current inline logic: EventBus emission (`coder_text_chunk`, `coder_tool_call`, `coder_session_started`), `sessionStreamLogRepo.insert()`, `workflowLogRepo.insert()`, `coderSessionRepo.insert()/updateStatus()`.

`AgentSessionOptions.observers: SessionObserver[]` defaults to `[]`. Workflow callers pass `[new WorkflowSessionObserver(...)]`. SkillService/ExploreService callers pass `[]` or a lightweight logging observer.

**This makes the open-closed principle work**: adding a new event sink (e.g., WebSocket relay) requires zero changes to `AgentSession` or the ACP layer.

**Alternatives considered:**
- *EventEmitter pattern (session.on('text_chunk', ...))* — More flexible but loses type safety and makes observer ordering non-deterministic
- *Callback soup (onTextChunk, onToolCall passed as options)* — What we have today; exactly the problem we're solving
- *Middleware chain* — Over-engineered for two known observer types

### D4: SessionStateMachine with `coder_session` as source of truth

```typescript
type SessionState = 'initializing' | 'running' | 'completed' | 'failed' | 'timeout' | 'cancelled' | 'closed';

const VALID_TRANSITIONS: Record<SessionState, SessionState[]> = {
  initializing: ['running', 'failed', 'timeout'],
  running:      ['completed', 'failed', 'timeout', 'cancelled'],
  completed:    ['closed'],
  failed:       ['closed'],
  timeout:      ['closed'],
  cancelled:    ['closed'],
  closed:       [],
};
```

Every `transition()` call writes to `coder_session.status` via `CoderSessionRepo.updateStatus()`. This means:
- `AgentRunnerService.cleanupOrphanedCoderSessions()` queries `coder_session WHERE status='running'` — no separate in-memory tracking needed
- `runningSlots` Map becomes a cache over DB state, not the primary source
- Server restart can detect orphaned sessions from DB

**Additive migration:** Add `process_pid INTEGER` column to `coder_session` so orphan detection can kill by PID.

**Alternatives considered:**
- *Pure in-memory state machine* — Loses state on restart; doesn't solve the "5 sources of truth" problem
- *Separate `session_state` table* — Adds a join for no benefit; `coder_session` already has the status column

### D5: Merge option types into single `AgentSessionOptions`

`AcpSessionOptions` (19 fields) and `AcpConnectionOptions` (15 fields) share 14 identical fields. Merge into one type:

```typescript
export interface AgentSessionOptions {
  cwd: string;
  task?: string;            // for withSession; optional for create()
  taskId?: string;          // for logging correlation (code-compiles-check, build-test-check)
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  issueNumber?: number;
  opencodeBinPath?: string;
  model?: string;
  stage?: string;
  title?: string;
  throttleMs?: number;
  signal?: AbortSignal;
  observers?: SessionObserver[];
  onProcessSpawned?: (proc: import('child_process').ChildProcess) => void;
  onBeforeKill?: (cwd: string) => Promise<boolean>;
}
```

Note: `workflowLogRepo`, `sessionStreamLogRepo`, `eventBus`, `coderSessionRepo` are **removed** from options. They're injected via `WorkflowSessionObserver` instead. The ACP layer no longer knows about them.

### D6: Phase-by-phase implementation order

Execute in 5 PRs, each independently shippable:

1. **Dead code removal** — Delete `SessionManager`, `AgentSessionMessageRepo`, `agent_session_message` table. Merge option types. Low risk, immediate clarity gain.
2. **Extract AcpProcess** — Pull spawn/kill/lifecycle into `acp-process.ts`. `acp-session.ts` delegates to it. No behavior change.
3. **Extract AcpProtocol + SessionObserver** — Pull protocol layer + define observer interface. Wire `WorkflowSessionObserver` inline. `acp-session.ts` becomes thin.
4. **AgentSession class** — Replace `runAcpSession` with `withSession()`, `createAcpConnection` with `AgentSession.create()`. Update all 11 consumer files.
5. **SessionStateMachine** — Add `process_pid` column, centralized state transitions, `runningSlots` → DB cache.

Each phase builds on the previous but can be tested and shipped independently.

## Risks / Trade-offs

**[Large scope — 11 consumer files updated]** → Phase-by-phase PRs with green tests at each step. Phase 4 (the breaking API change) is the only one that touches consumers.

**[ACP protocol timing assumptions]** → Phases 2–3 preserve exact protocol sequencing (initialize → newSession → prompt → cancel). The split is structural, not behavioral. ACP SDK calls remain identical.

**[Observer ordering — `WorkflowSessionObserver` must write DB before emitting events]** → Document ordering contract: `onSessionEvent` → DB write → EventBus emit. Observers are called in array order.

**[`coder_session` migration on running sessions]** → `process_pid` column is nullable; existing rows get NULL. Orphan scan falls back to current behavior (status-based) when PID is NULL.

**[SkillService uses `runAcpSession` without issueId/projectId — minimal observer context]** → `SessionContext` fields are all optional. `WorkflowSessionObserver` skips DB writes when `issueId` is undefined. This matches current behavior (SkillService sessions don't write to `coder_session` today).

## Migration Plan

**Phase 1 (dead code):**
1. Delete `packages/cli/src/agent-runtime/session.ts`
2. Delete `packages/cli/src/db/agent-session-message-repo.ts`
3. Remove `agent_session_message` table creation from `migrations.ts` (v19 migration to DROP TABLE)
4. Remove `SessionManager` from `server/index.ts` instantiation and all imports
5. Remove `_agentSessionMessageRepo` parameter from `AgentRunnerService` constructor
6. Merge `AcpSessionOptions` / `AcpConnectionOptions` into unified type, update imports
7. Run `npm run build && npm test`

**Phase 5 (state machine — production data migration):**
1. Add migration: `ALTER TABLE coder_session ADD COLUMN process_pid INTEGER`
2. `AgentSession.create()` writes `process.pid` to `coder_session.process_pid`
3. `AgentRunnerService` orphan scan: `SELECT * FROM coder_session WHERE status='running'` → kill by PID
4. `runningSlots` Map removed; `isRunning(issueId)` queries `coder_session` table

**Rollback:** Each phase is a separate commit/PR. Revert the specific PR to roll back. No data-destructive operations until Phase 5 (which only adds a nullable column).

## Open Questions

- Should `AcpProcess` also handle the `onBeforeKill` callback (current line in `AgentRunnerService`), or should that stay in `AgentSession`? Leaning toward `AgentSession` since it's a business-policy decision.
- The `onSessionUpdate` callback in `AcpConnectionOptions` (used by `PlanStageRunner` for raw ACP notification forwarding) — should this become a special observer method or remain as a direct callback? The `plan_session_update` event needs the raw notification, which is ACP-protocol-level data that `SessionObserver` doesn't expose. Propose: add `onRawNotification?(ctx, notification)` to `SessionObserver` for this use case.
