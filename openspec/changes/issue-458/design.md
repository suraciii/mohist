## Context

Workflow Inline Agent work enters the runner through `mohist/opencode` in `packages/runner/src/actions/opencode.ts`. The Action opens or resolves a Workflow-source AgentSession, persists its physical OpenCode binding, and calls `OpenCodeRuntime.runTurn`, but it does not pass a `RuntimeTurnObserver`. The runtime already projects OpenCode events into stable Mohist events (`message.delta`, `reasoning.delta`, tool-call lifecycle, `usage.updated`, and `model.resolved`), so those facts are currently discarded only on the Workflow path.

The AgentJob executor demonstrates the existing delivery pattern: attach the physical session, record `session.input`, serialize projected event uploads, and isolate upload failures from the work result. Workflow sessions already have a dedicated runner endpoint keyed by `(projectId, workflowRunId, sessionName)`. The server validates the supplied `runtimeSessionId`, appends accepted events to the canonical AgentSession transcript, pushes them to live consumers, and derives the latest terminal state from `session.closed`.

This change is constrained to the execution plane. TaskRun remains the Workflow work authority; AgentSession remains the transcript and runtime-fact authority. No Session event may advance or fail Workflow work, and no server endpoint or persistence shape needs to change.

## Goals / Non-Goals

**Goals:**

- Deliver each Workflow OpenCode turn's composed input and normalized runtime events to its existing Workflow AgentSession in production order.
- Record one terminal event after each executed turn so accepted reports resolve the session to `completed` or `failed`.
- Keep event delivery best-effort and diagnostically visible without changing the Action result.
- Preserve physical-session identity checks, multi-turn session reuse, and the existing AgentJob path.
- Cover the behavior with deterministic runner specs using fake runtime and server collaborators.

**Non-Goals:**

- Changing runner-to-server routes, AgentSession persistence, status derivation, Web rendering, or empty-state copy.
- Retrying failed event uploads, introducing an outbox, or backfilling transcripts for turns that already completed.
- Moving TaskRun completion authority into AgentSession or deriving Workflow transitions from `session.closed`.
- Refactoring AgentJob reporting into a shared abstraction as part of this bug fix.

## Decisions

### 1. Report from the Workflow Action adapter

After the Action has resolved and, when necessary, attached the physical session, it will create a turn-scoped reporter using the existing Workflow target: project ID, WorkflowRun ID, session name, work metadata, and non-null runtime session ID. The Action will enqueue a `session.input` event containing the exact composed prompt sent to OpenCode, then call `runTurn` with an `onEvent` observer that forwards the runtime's normalized events.

The input payload will identify the event as Workflow task input (`kind: "task"`, `source: "workflow"`, `role: "user"`) and include the runtime session ID. Every endpoint request will also carry the existing `workId`, `workType`, `stage`, and `runtimeSessionId` envelope.

**Alternative considered:** emit the input from `RuntimeTurnObserver.onSessionReady`. That callback is awaited by `runTurn`; a rejected upload would therefore turn an observational failure into a runtime failure and could prevent prompt submission. The Workflow adapter already owns the composed prompt and persisted binding, so it is the correct boundary for this fact.

**Alternative considered:** make `OpenCodeRuntime` emit `session.input` and `session.closed`. This would mix source-specific Session lifecycle policy into the shared runtime and could duplicate the AgentJob close, which is owned by AgentJob completion on the server. The runtime remains responsible only for normalized OpenCode facts.

### 2. Serialize best-effort uploads with a turn-scoped promise chain

The reporter will maintain one promise tail. Enqueuing an event appends one call to `ServerConnection.workflowAgentSessionRuntimeEvents`; each call catches and logs its own rejection before the next event proceeds. This preserves input-before-output order, preserves projected event order, and prevents one rejection from poisoning later event attempts.

The runtime observer remains synchronous and only appends work to the queue. It never awaits HTTP and never throws into `runTurn`. After `runTurn` returns, the Action enqueues the terminal event and waits for the queue to settle before returning the already-determined Action result. Reporting calls use request-scoped cancellation independent of an already-aborted runtime signal and a bounded timeout consistent with runner report calls, so an interruption can still attempt its failed close and a stalled upload cannot retain the Action indefinitely. Timeout or transport rejection is logged once; it is not retried or persisted locally.

Diagnostics will include the WorkflowRun, work ID, session name, event type, and error without logging event payloads or prompt content.

**Alternative considered:** fire all uploads without waiting. That avoids report latency but allows `session.closed` to overtake transcript events, permits the next turn to interleave with the prior queue, and can lose events during runner shutdown.

**Alternative considered:** buffer the whole turn and send one batch after completion. The endpoint supports batches, but this removes live transcript updates and makes one failed request lose the complete turn. The serialized stream matches existing AgentJob behavior and current UI expectations.

**Alternative considered:** extract a generic reporter and migrate AgentJob to it. The two sources use different targets and terminal ownership. Touching the working AgentJob path increases regression risk without being required to fix Workflow reporting; shared extraction can follow only if a third caller or real duplication pressure appears.

### 3. Generate the close in the Workflow adapter after the runtime result

Once `runTurn` resolves, the Action will enqueue exactly one `session.closed` event after all previously observed events:

- `result.ok === true` maps to `status: "completed"` and `exitCode: 0`.
- `result.ok === false` maps to `status: "failed"`, `exitCode: 1`, and `failureReason: result.error.message`.

The close payload will include the runtime session ID. The server remains responsible for timestamping, persistence, context-exhaustion classification, live fan-out, and read-side terminal-state derivation. The Action then maps the unchanged runtime result to its existing success or failure `ActionResult`; close delivery cannot alter this mapping.

A later Workflow turn that reuses the same logical and physical session enqueues a new `session.input`; the existing AgentSession logic treats new activity after `session.closed` as a resumed turn. Its subsequent close becomes the latest terminal observation.

**Alternative considered:** close the Workflow AgentSession when TaskRun reports to the server. That would couple Session facts to Workflow result handling, lose the runtime event ordering boundary, and require server contract changes. It would also confuse Action runtime failure with later task expectation or Workflow decisions.

### 4. Keep server and Web behavior unchanged

The existing Workflow runtime-events endpoint already resolves the canonical AgentSession and rejects stale physical identities by ignoring events whose `runtimeSessionId` is not current. `AgentSessionGrain` already accumulates all selected event types, emits live updates, marks the current turn ended on `session.closed`, and supports later activity. The Web already reads these projections. Implementation therefore changes runner code and runner tests only.

**Alternative considered:** add a new endpoint or direct Workflow-to-Session server command. Existing behavior is missing producer calls, not server capability; another contract would duplicate identity and persistence rules.

### 5. Verify at the lowest useful boundary

Add a focused runner spec for Workflow Action session reporting using a fake `OpenCodeRuntime`/SDK client and a recording `ServerConnection`. It will verify:

- exact input, projected event, and terminal ordering for a successful turn;
- reasoning, tools, usage, model, and final-response reconciliation forwarding;
- failed/interrupted runtime results produce a failed close while preserving the original Action failure;
- input, projected-event, and close upload rejection is logged, not retried, and does not change the Action result;
- two turns reusing one session each record input and close in order.

Run the existing AgentJob executor specs unchanged as the regression guard. Server and Web tests do not need new behavior matrices because their endpoint, persistence, and presentation contracts are unchanged and already covered.

## Risks / Trade-offs

- [Best-effort delivery can leave gaps or a session running when the endpoint rejects an event] -> Log target and event identity, continue the turn, and leave retry/outbox work explicitly outside this change.
- [Asynchronous event callbacks can reorder HTTP writes or allow close to arrive early] -> Use one per-turn promise chain and enqueue close only after `runTurn` has emitted live and reconciled events.
- [Runtime cancellation can abort telemetry before a failed close is attempted] -> Separate bounded reporting cancellation from the runtime signal while preserving a finite wait.
- [One HTTP request per projected event adds request volume] -> Match the proven AgentJob path for correctness and live updates; do not add batching until measured load justifies it.
- [Repeated logical-session turns could appear terminal between tasks] -> Preserve current semantics: each close terminates one turn, and the next input resumes activity in the same AgentSession.
- [A stale physical binding can cause accepted HTTP calls to append nothing] -> Always send the binding used for the turn; retain the server's current-binding guard rather than weakening identity validation.

## Migration Plan

1. Add the Workflow turn reporter and wire it into `opencodeAction` after physical binding resolution.
2. Add focused runner specs and run runner typecheck and tests, including the unchanged AgentJob executor suite.
3. Deploy the runner change without a server or Web migration. New turns begin producing transcripts and terminal events immediately; existing empty transcripts are not reconstructed.
4. Verify a Workflow stage session shows input, assistant text, tool calls, and a completed or failed status after a real turn.

Rollback consists of reverting the runner change. Events already accepted by AgentSession remain valid audit history and require no cleanup; rollback only restores the prior missing-event behavior for future Workflow turns.

## Open Questions

None. The existing event vocabulary, Workflow endpoint, physical-binding guard, and terminal status values fully define the implementation boundary.
