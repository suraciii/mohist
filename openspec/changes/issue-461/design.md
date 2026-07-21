## Context

Workflow OpenCode reporting currently sends each `session.input`, projected runtime event, and `session.closed` directly from `WorkflowAgentSessionReporter`. The reporter serializes requests, suppresses later events after an unaccepted input, logs upload failures, and waits up to 30 seconds for network reporting before returning the Action result. Follow-up handling similarly sends user input fire-and-forget. Only operation-correlated `session.followup_completed` and `session.followup_failed` events use `FollowupFailureOutbox`, whose JSON file survives restart and drains on startup, retry, and SignalR reconnection.

Both Workflow and generic runtime-event endpoints already return `AgentSessionRuntimeEventReceipt[]`. The Workflow client exposes those receipts, while the generic client currently discards them. An empty receipt is significant: the Session grain uses it when the physical runtime binding is stale and when an input boundary cannot be persisted. Therefore HTTP 2xx alone cannot acknowledge a pending event.

This is runner infrastructure for reporting Session facts. The Server remains the Session state authority and continues to validate the physical binding, event allowlist, and transcript boundary. The implementation must preserve event order, avoid coupling event transport to runtime execution, survive runner restart, and comply with the repository test rules: no real network, filesystem, or wall-clock time in tests.

## Goals / Non-Goals

**Goals:**

- Durably retain Workflow turn events and follow-up user input on the originating runner until positively accepted.
- Resume pending delivery after transient failure, runner restart, and server reconnection.
- Preserve FIFO order per logical AgentSession while allowing unrelated sessions to progress independently.
- Keep network delivery and retry outside the Workflow Action result and follow-up runtime invocation.
- Preserve the existing durable guarantee for operation-correlated follow-up terminal outcomes.

**Non-Goals:**

- Server-side event IDs, deduplication, idempotency, endpoint changes, or persistence-model changes.
- Exactly-once transcript persistence when a response is lost after the Server accepted an event.
- Moving pending events to another runner or retargeting them to a newer physical runtime binding.
- Recovering a follow-up runtime invocation interrupted by runner process failure.
- A general-purpose runner outbox for work results, task logs, or other transports.
- Administrative eviction, dead-lettering, or replay controls for permanently rejected Session events.

## Decisions

### D1: Replace the terminal-only outbox with one AgentSession runtime-event outbox

`RunnerHost` will own one `AgentSessionRuntimeEventOutbox` shared by Workflow reporting and SignalR follow-up handling. It replaces `FollowupFailureOutbox` rather than adding a second queue. `WorkExecutor` will pass the shared outbox through `ActionContext` to the Workflow OpenCode Action; `RunnerSignalRClient` will pass the same instance to the follow-up handler and own its connection lifecycle hooks.

The outbox accepts a normalized record containing a local record ID, a binding-free discriminated Session target, the original `runtimeSessionId`, optional Workflow work metadata, and exactly one runtime event with its original payload. Follow-up input and operation-correlated follow-up terminal outcomes use the same record shape as Workflow input, activity, and close events.

Alternatives considered:

- Keep direct retry inside `WorkflowAgentSessionReporter`: rejected because reporter-local state cannot survive restart and would duplicate receipt, retry, and lifecycle policy in follow-up handling.
- Add a new Workflow/input outbox beside `FollowupFailureOutbox`: rejected because independently draining queues can deliver a follow-up outcome or later Session event ahead of its pending input. Coordinating them would recreate a shared outbox with more state and more failure modes.

### D2: Persist ordered records before detached network delivery

`enqueue(record)` assigns the record's order synchronously, appends it to the in-memory state, and resolves only after an atomic snapshot containing that record has been persisted under `.mohist/runner-state/runtime-events.json`. Adjacent mutations may be coalesced into one snapshot, but every returned enqueue promise must be covered by a completed write. The file store writes a temporary file and renames it over the snapshot; the outbox logic depends on an injected store so tests use an in-memory implementation.

The ordered entry list is the persistence authority for sequence position. A canonical sequence key is derived in one place from the logical target fields: `(projectId, workflowRunId, sessionName)` for Workflow sessions or `(projectId, sessionId)` for generic sessions. The optional binding carried by a follow-up command is not persisted as part of the target; the original `runtimeSessionId` is stored separately and is never recomputed during retry.

The Workflow reporter no longer performs HTTP requests. It enqueues input before starting the OpenCode prompt, enqueues projected events as they are observed, enqueues one logical close after runtime completion, and waits only for its local persistence promises before returning. It does not await delivery or retry. Follow-up handling persists its input before invoking `runtime.followup`; terminal callbacks enqueue their outcome without invoking the runtime again.

Alternatives considered:

- Persist only after the first upload fails: rejected because a process exit between the first attempt and fallback write would still lose the event.
- Store an entire completed turn as one record: rejected because input must precede runtime execution and per-event acceptance is needed to retain the exact failed suffix.
- Use an append-only journal immediately: rejected as unnecessary complexity for the current volume. Coalesced atomic snapshots preserve the existing outbox pattern; write amplification is tracked as a risk.

### D3: Send one event per request and require a matching receipt

The outbox sends the head event alone through the existing Workflow or generic runtime-events endpoint. `ServerConnection.agentSessionRuntimeEvents` will return `AgentSessionRuntimeEventReceipt[]`, matching the existing Workflow method. The head is acknowledged only when the response contains its event type. A timeout, transport error, non-2xx response, malformed response, empty receipt, or receipt without the submitted type leaves the head pending.

One event per request makes the existing `{ type }` receipt unambiguous without changing the API. A response can be lost after server acceptance, so retry remains at-least-once and can duplicate an event; no local record ID is sent as a server deduplication key.

Alternatives considered:

- Treat any 2xx as success, as the current follow-up outbox does: rejected because stale-binding and transcript-boundary rejection return 2xx with an empty receipt.
- Batch a complete pending suffix: rejected because type-only receipts cannot identify individual repeated event types reliably and one rejected head must fence all later events in that sequence.
- Add delivery IDs to the Server API: rejected by the issue's explicit server idempotency non-goal.

### D4: Drain one FIFO per logical AgentSession with bounded cross-session concurrency

At most one head per sequence key is in flight. After a matching receipt is received, acknowledgement re-enters the serialized mutation path, verifies that the same record is still the head, removes it, persists the new snapshot, and advances that sequence. A failed or unaccepted head fences later records for that logical Session, including records for a newer physical binding. Different sequence keys remain eligible and drain with a small bounded concurrency so one stale Session cannot stop all delivery.

The outbox uses the existing bounded request timeout and retry timer pattern. Enqueue, startup, automatic reconnect, and forced reconnect all call the same idempotent `kick()` operation. Concurrent kicks share in-flight sequence work rather than starting duplicate requests.

Alternatives considered:

- One global FIFO: rejected because one stale binding would block every AgentSession on the runner.
- Key queues by `runtimeSessionId`: rejected because later turns and binding changes belong to the same logical transcript; it would allow a newer binding to overtake an older pending turn.
- Retarget a stale head to the current binding: rejected because it could attach old content or a delayed close to the wrong turn.

### D5: Make outbox readiness part of runner lifecycle, not Action completion

The outbox loads before the runner starts accepting SignalR commands or claiming work. A missing file means an empty queue; an unreadable or invalid file makes the outbox unavailable and keeps the runner from accepting new execution that it cannot record. After SignalR starts, the initial drain is kicked asynchronously so a server outage does not delay runner startup. Both automatic and forced reconnect paths invoke the same drain hook.

`stop()` cancels retry timers and in-flight HTTP attempts but does not remove pending records. A later start reloads and resumes them. Store failures after startup are logged without event payloads, retain the records in memory, mark the outbox unhealthy for new work, and schedule persistence recovery; they do not replace an already-running OpenCode result.

Alternatives considered:

- Await a full startup drain: rejected because an unavailable Server would prevent the runner from becoming available even though execution and eventual delivery are intentionally decoupled.
- Silently fall back to direct best-effort upload when local state is unavailable: rejected because it reintroduces permanent event loss under the exact failure being fixed.

### D6: Import the existing follow-up terminal state idempotently

On first load, the new file store also reads `.mohist/runner-state/followup-failures.json` version 1. Each legacy entry is converted to a `session.followup_completed` or `session.followup_failed` record with deterministic ID `legacy-followup-terminal:{operationId}`. The importer merges by ID, atomically persists the new snapshot, and only then marks the legacy file migrated. Re-running after a crash cannot create a second pending record for the same operation.

Alternatives considered:

- Discard the old file because runner state is local: rejected because those entries represent terminal facts already promised durable delivery.
- Keep both outboxes active: rejected because it restores competing delivery paths and ordering races.

### D7: Test behavior through injected stores, transport fakes, and fake time

Outbox unit tests will share one in-memory store across two outbox instances to model restart. They will cover matching and empty receipts, timeout/retry with fake timers, per-sequence ordering, cross-sequence progress, concurrent kicks, stale bindings, snapshot failure, and idempotent legacy import. No test will instantiate the physical file adapter or touch a temporary directory.

Runner specs will verify that Workflow input/activity/close and follow-up input/outcome enter the shared outbox in order, that runtime execution and Workflow results do not await HTTP delivery, and that retry never invokes `runtime.followup` again. Connection tests will verify receipt parsing for both endpoint shapes; SignalR lifecycle tests will verify startup and reconnect kicks. Existing real-filesystem `FollowupFailureOutbox` tests will be replaced rather than retained beside the new suite.

## Risks / Trade-offs

- [A Server acceptance followed by a lost response causes replay and may duplicate transcript content] -> Keep one local logical record and remove it only on a matching receipt; accept at-least-once behavior because server deduplication is explicitly out of scope, and cover the ambiguous-response path in tests.
- [A permanently stale binding leaves its sequence head pending forever and blocks newer events for that logical Session] -> Never retarget or drop the event; isolate the blockage to that Session, retain warning diagnostics without payload content, and leave administrative remediation to a follow-up change.
- [A long outage or permanently rejected event grows the local state file] -> Coalesce snapshot writes, bound concurrent drains, expose queue/store failures in runner logs, and do not apply a lossy retention limit in this change.
- [Snapshot rewrites amplify I/O for high-volume delta streams] -> Coalesce adjacent enqueue and acknowledgement mutations. If measured volume makes this material, replace the store behind the same outbox interface with an append journal in a separate change.
- [Disk-full, permission, or corruption failures can defeat local durability] -> Use atomic replacement and restrictive file permissions, fail runner readiness when state cannot be loaded, mark the runner unhealthy on later persistence failure, and never silently switch to best-effort upload.
- [The outbox stores prompts and assistant/tool payloads on runner disk] -> Keep the file under runner-owned `.mohist/runner-state`, use owner-only permissions, and exclude payloads from diagnostics.
- [A crash during legacy import could leave both files present] -> Use deterministic imported IDs and persist the new snapshot before marking the old file migrated, making import replay-safe.

## Migration Plan

1. Add the shared outbox model, injected persistence boundary, physical snapshot store, receipt validation, per-sequence drain logic, and in-memory tests.
2. Change the generic `ServerConnection` runtime-events method to return the receipts already produced by the Server.
3. Construct and load the shared outbox in `RunnerHost`, pass it through `WorkExecutor`/`ActionContext` and `RunnerSignalRClient`, and unify startup, reconnect, and stop hooks.
4. Replace direct Workflow reporter uploads and follow-up input emission with durable enqueue; route follow-up terminal outcomes through the same outbox and remove `FollowupFailureOutbox` after its migration reader is covered.
5. Run runner typecheck and tests. No Server, database, Web, or API deployment ordering is required because endpoint shapes do not change.

Deployment may be rolling across runners because pending state never transfers between them. Each upgraded runner imports only its own legacy file and drains only its own events.

Rollback is safe only after the new outbox has drained. An older runner cannot understand new Workflow/input records, so operators must preserve `runtime-events.json` and either roll forward again or use a purpose-built converter before downgrading with pending entries. The migrated legacy file is not deleted until the new snapshot is durable, but restoring it alone cannot represent newly queued event kinds.

## Open Questions

None for this change. Server-side deduplication, stale-entry administration, queue size limits, and an append-journal store are explicit follow-up scopes rather than unresolved implementation decisions.
