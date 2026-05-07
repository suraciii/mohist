## Context

Issue #133 replaced the old duplicated ACP session functions with `AgentSession`, `withSession`, `AcpProcess`, `SessionObserver`, and `SessionStateMachine`. That moved most visibility side effects behind observers, but the runtime boundary still leaks workflow visibility dependencies: `AgentSessionOptions` accepts `eventBus`, `workflowLogRepo`, `sessionStreamLogRepo`, and `coderSessionRepo`; `agent-session.ts` imports `WorkflowSessionObserver`; and `AgentSession` uses that specific observer for workflow logs, coder-session identity, and stable tool-call IDs.

This makes `AgentSession` partly a runtime abstraction and partly a workflow visibility factory. The implementation should instead make `AgentSession` a deep runtime module with a small interface: create a session, execute prompts, cancel, close, expose state, and publish events to observers. Workflow, Explore, Skill, Check, and conflict-resolution callers own the decision to attach observers that persist logs or emit UI events.

Constraints:

- No frontend API, SSE payload, or persistence schema changes.
- Plan and Check must continue to reuse one session across multiple prompts.
- Build tasks, checks, Skill, Explore, and conflict resolution must keep their existing visibility behavior.
- Abort and timeout behavior must still cancel ACP, run optional `onBeforeKill`, clean up the process, and return a user-visible failure result.
- Model override must still be applied to the ACP session, and set failure remains degraded behavior rather than hard failure.

## Goals / Non-Goals

**Goals:**

- Remove EventBus and DB repository types from `AgentSessionOptions`.
- Remove the `WorkflowSessionObserver` import and construction from `agent-runtime/agent-session.ts`.
- Keep runtime dependencies flowing from callers to observers to `AgentSession`, not from `AgentSession` back to workflow visibility infrastructure.
- Preserve the existing observer event contract for text chunks, tool calls, raw ACP notifications, lifecycle start, and state transitions.
- Move workflow observer construction to workflow/service code, preferably through a small helper so consumers do not duplicate visibility setup.
- Extract ACP SDK details only if it simplifies `AgentSession` without creating a shallow pass-through layer.

**Non-Goals:**

- No new user-facing capability.
- No frontend route, event-name, payload, or query-shape changes.
- No schema migration unless an implementation detail proves unavoidable.
- No new `AgentSessionPool`, connection pooling, or retry model.
- No change to Plan/Check multi-round semantics or Build per-task isolation.
- No generic option bag that simply hides EventBus/repos under a different runtime-facing name.

## Decisions

### D1: Keep `AgentSessionOptions` as the runtime interface and move visibility inputs out

`AgentSessionOptions` will keep runtime/session fields only:

```typescript
export interface AgentSessionOptions {
  cwd: string;
  task?: string;
  taskId?: string;
  timeout?: number;
  issueId?: string;
  projectId?: string;
  executionId?: string;
  issueNumber?: number;
  opencodeBinPath?: string;
  signal?: AbortSignal;
  observers?: SessionObserver[];
  onProcessSpawned?: (proc: import('child_process').ChildProcess) => void;
  stage?: string;
  model?: string;
  onBeforeKill?: (cwd: string) => Promise<boolean>;
  title?: string;
}
```

Remove `eventBus`, `workflowLogRepo`, `sessionStreamLogRepo`, `coderSessionRepo`, `throttleMs`, and `onSessionUpdate` from this type. `throttleMs` is not runtime behavior; it belongs to the visibility observer. `onSessionUpdate` is a compatibility callback for raw ACP visibility events; it should become an observer in the caller layer.

**Alternatives considered:** Keep the fields optional in `AgentSessionOptions` and only stop using them inside `AgentSession`. This preserves compile compatibility but keeps the information leak and allows new runtime callers to keep depending on workflow visibility details. Move these fields into `visibilityOptions` on `AgentSessionOptions`. This is only a renamed generic option bag and still violates the desired dependency direction.

### D2: Introduce a workflow-layer observer factory/helper

Most affected call sites need the same translation from workflow context to `WorkflowSessionObserver`. To avoid duplicating observer construction, add a helper outside `agent-runtime`, for example `packages/cli/src/workflow/session-observers.ts` or `packages/cli/src/services/session-observers.ts`.

The helper should accept workflow/service dependencies and metadata, then return a `SessionObserver[]`:

```typescript
export interface WorkflowSessionObserverOptions {
  eventBus?: EventBus;
  workflowLogRepo?: WorkflowLogRepo;
  sessionStreamLogRepo?: SessionStreamLogRepo;
  coderSessionRepo?: CoderSessionRepo;
  throttleMs?: number;
  taskDescription?: string;
  title?: string;
  stage?: string;
  rawNotificationObserver?: SessionObserver;
}

export function createWorkflowSessionObservers(
  options: WorkflowSessionObserverOptions,
  extraObservers: SessionObserver[] = [],
): SessionObserver[];
```

This helper is allowed to import EventBus, repos, and `WorkflowSessionObserver`. `AgentSession` is not. Callers can still construct `WorkflowSessionObserver` directly for special cases, but the common path should be the helper.

**Alternatives considered:** Have each consumer instantiate `WorkflowSessionObserver` manually. This is explicit but causes duplicated metadata mapping and higher regression risk. Put the helper in `agent-runtime`. That keeps the dependency leak in the runtime package even if `agent-session.ts` no longer imports the observer.

### D3: Move `WorkflowSessionObserver` out of `agent-runtime` or split observer types from visibility implementation

`SessionObserver`, `SessionContext`, `SessionState`, and `ToolCallEvent` are runtime contracts and should remain in `agent-runtime`. `WorkflowSessionObserver` is a visibility adapter because it imports EventBus and DB repos, so it should move to a workflow/service visibility module, or `agent-runtime/session-observer.ts` should be split into:

- `agent-runtime/session-observer.ts`: runtime interfaces and event types only.
- `workflow/workflow-session-observer.ts` or `services/workflow-session-observer.ts`: EventBus/repo adapter.

This satisfies the stronger acceptance criterion that Agent Runtime modules do not import workflow, DB, or service visibility layers.

**Alternatives considered:** Leave `WorkflowSessionObserver` in `agent-runtime` but stop importing it from `agent-session.ts`. This meets one narrow acceptance criterion but still leaves `agent-runtime` as a package importing DB and service layers. Move all observer interfaces into workflow. That reverses the dependency direction because `AgentSession` needs observer contracts.

### D4: Replace `onSessionUpdate` with explicit raw-notification observers at call sites

Plan and Check currently pass `onSessionUpdate` through `AgentSessionOptions` to emit `plan_session_update` while reusing one multi-round session. Preserve this behavior by creating a lightweight `SessionObserver` in those runners:

```typescript
const planBridgeObserver: SessionObserver = {
  onRawNotification(_ctx, notification) {
    eventBus.emit('plan_session_update', { ... });
  },
};
```

This keeps raw ACP notification forwarding available without making `AgentSessionOptions` a callback bag. The observer must be included with the `WorkflowSessionObserver` when constructing the multi-round session.

**Alternatives considered:** Keep `onSessionUpdate` because it is convenient for Plan/Check. This preserves an ACP-specific callback in the runtime options and competes with the observer interface. Convert raw notifications into a generic `onSessionEvent` only. This loses the full ACP notification shape that current Plan/Check visibility relies on.

### D5: Make tool-call ID generation runtime-owned, not observer-owned

`AgentSession` currently calls `WorkflowSessionObserver.nextToolCallId()`, which couples runtime event normalization to a workflow visibility adapter. Move stable tool-call ID generation into `AgentSession` or a small runtime helper, so every observer receives the same stable `ToolCallEvent.toolCallId` without knowing about `WorkflowSessionObserver`.

The runtime should keep the current algorithm shape for compatibility: derive IDs from `acpSessionId`, tool name, and a monotonic counter, and match started/completed events for the same tool call where ACP data allows. If ACP notifications provide a native tool-call identifier, prefer it only if doing so does not break existing frontend deduplication expectations.

**Alternatives considered:** Let `WorkflowSessionObserver` keep assigning IDs and mutate tool-call events. This hides a runtime event identity concern inside one observer and breaks other observers. Generate IDs independently in each observer. This risks inconsistent IDs across SSE, logs, and tests.

### D6: Keep `SessionContext.coderSessionId` optional and observer-provided only where needed

`coderSessionId` is created by the visibility observer when it persists the `coder_session` row. `AgentSession` should not know how that row is created. Keep `SessionContext.coderSessionId` optional and avoid requiring the runtime to read it from a workflow observer.

State changes should still be delivered to observers. `WorkflowSessionObserver` remains responsible for updating `coder_session.status`. Do not attach `SessionStateMachine` to `CoderSessionRepo` from the runtime after this refactor; persistence of state is a visibility concern, and duplicating status updates in both the state machine and observer creates split ownership.

**Alternatives considered:** Add a generic `SessionObserver.getCoderSessionId()` method so the runtime can recover the persisted ID. This makes observers bidirectional and exposes visibility state back to the runtime. Keep DB-backed state in `SessionStateMachine`. That requires `CoderSessionRepo` in runtime, directly violating the boundary.

### D7: Preserve `withSession` as lifecycle sugar around `AgentSession.create`

`withSession` remains the safe single-prompt path:

```typescript
const session = await AgentSession.create(options);
try {
  return fn ? await fn(session) : await session.execute(options.task ?? '');
} finally {
  await session.close();
}
```

Manual multi-round callers, especially Plan and Check, continue to call `AgentSession.create()` once and `execute()` multiple times before `close()`. The refactor must not replace their loops with multiple `withSession()` calls.

**Alternatives considered:** Make all callers use `withSession`. This would simplify lifecycle management but break Plan/Check conversation continuity. Move multi-round logic into `withSession` callbacks everywhere. This is acceptable mechanically but unnecessary churn; existing explicit lifecycle in Plan/Check is clear.

### D8: Extract an ACP driver only after boundary cleanup, and only if it hides real complexity

The first phase should remove visibility dependencies from the runtime boundary. After that, evaluate whether `agent-session.ts` is still doing too much ACP-specific work. If extraction helps, add a small `AcpConnectionDriver` that owns:

- `ClientSideConnection` and `PROTOCOL_VERSION`.
- requestPermission default policy.
- `initialize`, `newSession`, `prompt`, `cancel`, and `setSessionConfigOption` calls.
- ACP SDK notification wiring.
- timeout and spawn-failure races only if this makes `AgentSession` more domain-oriented.

The driver must not import issues, stages, EventBus, DB repos, workflow logs, stream logs, or coder-session policy. Its interface should be smaller than the ACP SDK surface and should not become a one-method pass-through wrapper.

**Alternatives considered:** Always extract an ACP protocol layer to reduce file size. This risks a shallow layer whose interface mirrors the SDK. Never extract. This leaves protocol setup, permission policy, and lifecycle races mixed with domain logic even after visibility cleanup.

## Risks / Trade-offs

[Plan/Check multi-round sessions accidentally become per-prompt sessions] → Keep `AgentSession.create()` in `PlanStageRunner` and `CheckStageRunner`; only change observer construction.

[Visibility regressions from missing observer metadata] → Centralize observer construction in a workflow/service helper and require each caller to pass issueId, issueNumber, projectId, executionId, stage, title, and task description where those values exist today.

[SSE text or tool-call events disappear] → Preserve `WorkflowSessionObserver.onTextChunk` and `onToolCall` semantics, including throttling, issue-number fallback, executionId checks, rawInput/rawOutput/title fields, and stable toolCallId delivery.

[Coder session status becomes stale] → Keep terminal state transitions delivered through `onStateChange`; `WorkflowSessionObserver` updates `coder_session` for completed, failed, timeout, and cancelled states.

[Runtime no longer writes workflow start/completion log events] → Represent start/completion/process error/process exit/model events as observer notifications or move those log writes to a workflow/service observer wrapper. Do not reintroduce direct `workflowLogRepo` calls in `AgentSession`.

[Abort/timeout cleanup regression leaves zombie processes] → Keep existing execute-path ordering: detect abort/timeout, attempt ACP cancel, run optional `onBeforeKill`, mark terminal state, cleanup ACP process, and return failure result. Add tests around both abort and timeout paths.

[Model override silently stops applying] → Keep model application in runtime or ACP driver immediately after `newSession`; emit/log success or degraded failure through observers rather than direct repo writes.

[More explicit observer setup increases call-site code] → Use the workflow/service helper for common cases and allow direct observer construction only for special bridges like Plan/Check raw notification forwarding.

## Migration Plan

1. Split runtime observer contracts from workflow visibility implementation: keep `SessionObserver` types in `agent-runtime`, move `WorkflowSessionObserver` to a workflow/service visibility module, and update exports/imports.
2. Add the workflow/service observer factory/helper and migrate common observer construction into it.
3. Remove `buildWorkflowObserver`, `_wfObserver`, and all EventBus/repo imports from `agent-runtime/agent-session.ts`.
4. Move stable tool-call ID generation into `AgentSession` or a runtime-only helper and keep `ToolCallEvent.toolCallId` stable for observers.
5. Remove `eventBus`, `workflowLogRepo`, `sessionStreamLogRepo`, `coderSessionRepo`, `throttleMs`, and `onSessionUpdate` from `AgentSessionOptions`.
6. Update `WorkflowEngine`, `StageContext`, Plan, Build, Check, checks, RalphExecutor, Skill, Explore, conflict-resolution, AgentRunnerService, and server entry points to pass `observers` instead of workflow visibility dependencies in runtime options.
7. Preserve Plan and Check manual lifecycle: create one session, execute each round/retry on that session, close in `finally`.
8. Run build and tests; add or update targeted tests for cleaned options, observer construction, raw notification bridge, tool-call IDs, model override warnings, abort/timeout cleanup, and multi-round reuse.
9. Only after tests pass, consider extracting `AcpConnectionDriver`; if extraction creates a shallow pass-through or increases call-site complexity, leave ACP SDK calls inside `AgentSession` for this change.

Rollback strategy: this is an internal refactor with no schema or external API changes. Roll back by reverting the implementation commits; persisted data and frontend contracts remain compatible.

## Open Questions

- Should `WorkflowSessionObserver` live under `workflow/` or `services/`? `services/` may fit Skill and Explore callers better, while `workflow/` better names the current persistence/EventBus adapter.
- Should model-selected and model-set-failed visibility be represented as new observer methods or as ordinary `onSessionEvent` events with runtime-defined event names?
- Should `SessionStateMachine` remain in `agent-runtime` as a pure in-memory validator only, or should its DB attachment API be removed immediately to prevent future boundary regressions?
