## Context

Workflow OpenCode reporting currently sends each `session.input`, projected runtime event, and `session.closed` directly from `WorkflowAgentSessionReporter`. The reporter serializes requests, suppresses later events after an unaccepted input, logs upload failures, and waits up to 30 seconds for network reporting before returning the Action result. Follow-up handling similarly sends user input fire-and-forget. Only operation-correlated `session.followup_completed` and `session.followup_failed` events use `FollowupFailureOutbox`, whose JSON file survives restart and drains on startup, retry, and SignalR reconnection.

Both Workflow and generic runtime-event endpoints already return `AgentSessionRuntimeEventReceipt[]`. The Workflow client exposes those receipts, while the generic client currently discards them. An empty receipt is significant: the Session grain uses it when the physical runtime binding is stale and when an input boundary cannot be persisted. Therefore HTTP 2xx alone cannot acknowledge a pending event.

This is runner infrastructure for reporting Session facts. The Server remains the Session state authority and continues to validate the physical binding, event allowlist, and transcript boundary. The implementation must preserve event order, avoid coupling event transport to runtime execution, survive runner restart, and comply with the repository test rules: no real network, filesystem, or wall-clock time in tests.

## Goals / Non-Goals

**Goals:**

- Durably retain Workflow turn events and follow-up user input on the originating runner until positively accepted, while preserving the existing operation-fenced settlement rule for follow-up terminal outcomes.
- Resume pending delivery after transient failure, runner restart, and server reconnection.
- Preserve FIFO order within each outbox-managed Workflow or generic-follow-up producer sequence while allowing independent sequences to progress.
- Keep network delivery and retry outside the Workflow Action result and follow-up runtime invocation.
- Preserve the existing durable guarantee for operation-correlated follow-up terminal outcomes.

**Non-Goals:**

- Server-side event IDs, deduplication, idempotency, endpoint changes, or persistence-model changes.
- Exactly-once transcript persistence when a response is lost after the Server accepted an event.
- Moving pending events to another runner or retargeting them to a newer physical runtime binding.
- Recovering a follow-up runtime invocation interrupted by runner process failure.
- Recovering a Workflow event when the runner terminates before that event's asynchronous local enqueue commits.
- A general-purpose runner outbox for work results, task logs, or other transports.
- Migrating AgentJob input/activity reporting or imposing new cross-producer ordering between AgentJob and generic follow-up events.
- Administrative eviction, dead-lettering, or replay controls for permanently rejected content and Workflow terminal events.

## Decisions

### D1: Replace the terminal-only outbox with one AgentSession runtime-event outbox

`RunnerHost` will own one `AgentSessionRuntimeEventOutbox` shared by Workflow reporting and SignalR follow-up handling. It replaces `FollowupFailureOutbox` rather than adding a second queue. `WorkExecutor` will pass the shared outbox through `ActionContext` to the Workflow OpenCode Action; `RunnerSignalRClient` will pass the same instance to the follow-up handler and own its connection lifecycle hooks.

The outbox accepts a normalized record containing a local record ID, a binding-free discriminated Session target, the original `runtimeSessionId`, optional Workflow work metadata, exactly one runtime event with its original payload, and an acknowledgement policy. Workflow input/activity/close and follow-up input use `matching-receipt`; operation-correlated follow-up terminal outcomes use `successful-response` to preserve their current operation-fenced protocol.

Alternatives considered:

- Keep direct retry inside `WorkflowAgentSessionReporter`: rejected because reporter-local state cannot survive restart and would duplicate receipt, retry, and lifecycle policy in follow-up handling.
- Add a new Workflow/input outbox beside `FollowupFailureOutbox`: rejected because independently draining queues can deliver a follow-up outcome or later Session event ahead of its pending input. Coordinating them would recreate a shared outbox with more state and more failure modes.

### D2: Persist ordered records before detached network delivery

`enqueue(record)` assigns the record's order synchronously, appends it to the in-memory state, and resolves only after an atomic snapshot containing that record has been persisted under `.mohist/runner-state/runtime-events.json`. Adjacent mutations may be coalesced into one snapshot, but every returned enqueue promise must be covered by a completed write. The snapshot/import store depends on a narrow `RuntimeEventOutboxFileSystem` port for read, owner-only temporary write, atomic rename, and legacy-file marking. Production uses a Node filesystem adapter; tests run the same store and importer against a recording in-memory implementation of that port.

The ordered entry list is the persistence authority for sequence position. A canonical sequence key is derived in one place from producer family plus target. `workflow-session:(projectId, workflowRunId, sessionName)` contains Workflow turn events and Workflow-targeted follow-ups. `generic-followup:(projectId, sessionId)` contains generic follow-up input and operation-correlated outcomes. The optional binding carried by a follow-up command is not persisted as part of the target; the original `runtimeSessionId` is stored separately and is never recomputed during retry.

AgentJob input/activity remains on `AgentJobExecutor`'s existing direct serialized upload chain. It does not enter `generic-followup`, so the design preserves existing source-local order but makes no new ordering promise between concurrent AgentJob and generic follow-up producers. Moving AgentJob into the durable outbox would expand this bug fix into AgentJob execution semantics and is deferred.

The outbox exposes two producer operations over the same ordered state. `enqueueBeforeExecution` attempts an atomic snapshot and, on failure, removes the uncommitted input record before rejecting so persistence recovery cannot later deliver an input whose runtime invocation never occurred. `enqueueProducedFact` retains an already-produced activity or terminal fact in memory when its snapshot fails, marks the outbox unhealthy, schedules local persistence recovery, and rejects its promise for observability.

The Workflow reporter no longer performs HTTP requests. It awaits `enqueueBeforeExecution` for input before starting the OpenCode prompt. Because `RuntimeTurnObserver.onEvent` is synchronous, each callback immediately registers an ordered `enqueueProducedFact` promise; after `runTurn` returns, the reporter enqueues one logical close and waits for every local enqueue to settle before returning the unchanged runtime result. A failed input enqueue returns an explicit execution-unavailable result without invoking OpenCode. Failed post-start fact enqueues remain tracked and observable but do not replace the runtime result. The reporter never waits for Server delivery or retry. The recoverable restart boundary is a completed local enqueue or a later successful recovery snapshot, not observation alone; a process crash before either commit remains outside this issue.

Follow-up handling awaits `enqueueBeforeExecution` for input before invoking `runtime.followup`. If that local write fails, the handler returns `unavailable` and does not invoke the runtime, allowing command delivery to be retried. Server upload remains detached. Terminal callbacks use `enqueueProducedFact` without invoking the runtime again. A local write failure after runtime execution is observable and marks the outbox unhealthy, but does not replace the runtime result.

Alternatives considered:

- Persist only after the first upload fails: rejected because a process exit between the first attempt and fallback write would still lose the event.
- Store an entire completed turn as one record: rejected because input must precede runtime execution and per-event acceptance is needed to retain the exact failed suffix.
- Use an append-only journal immediately: rejected as unnecessary complexity for the current volume. Coalesced atomic snapshots preserve the existing outbox pattern; write amplification is tracked as a risk.

### D3: Send one event per request with an event-kind-specific acknowledgement policy

The outbox sends the head event alone through the existing Workflow or generic runtime-events endpoint. `ServerConnection.agentSessionRuntimeEvents` will return `AgentSessionRuntimeEventReceipt[]`, matching the existing Workflow method.

For Workflow input/activity/close and follow-up input, the head is acknowledged only when the response contains its event type. A timeout, transport error, non-2xx response, malformed response, empty receipt, or receipt without the submitted type leaves that head pending.

Operation-correlated `session.followup_completed` and `session.followup_failed` records preserve the existing terminal protocol: timeout, transport error, non-2xx, and malformed JSON remain pending, but a 2xx with a valid receipt array settles the local record even when that array is empty. Server acceptance consumes the pending operation lease, so replay after a lost successful response legitimately returns `[]`; requiring a match would fence the Session forever. The Server also returns `[]` before terminal processing for a stale physical binding, and this runner-only protocol cannot distinguish the two outcomes. Therefore stale-binding retention and no-retarget guarantees apply only to `matching-receipt` records; a `successful-response` follow-up terminal may settle without persistence when the binding is stale. This preserves current terminal behavior and is accepted because issue 461 changes Workflow/content delivery, not terminal reconciliation.

The existing `Promise<AgentSessionRuntimeEventReceipt[]>` connection result is sufficient for both policies: rejection represents transport/status/parse failure, and resolution provides the valid array inspected by the selected policy.

One event per request makes the existing `{ type }` content receipt unambiguous without changing the API. A content response can be lost after server acceptance, so retry remains at-least-once and can duplicate an event; no local record ID is sent as a server deduplication key.

Alternatives considered:

- Treat any 2xx as success for every event: rejected because stale-binding and transcript-boundary rejection return 2xx with an empty receipt. The rule is retained only for operation-fenced follow-up terminals, where a consumed lease makes replay receipts non-repeatable.
- Batch a complete pending suffix: rejected because type-only receipts cannot identify individual repeated event types reliably and one rejected head must fence all later events in that sequence.
- Add delivery IDs to the Server API: rejected by the issue's explicit server idempotency non-goal.

### D4: Drain one FIFO per managed producer sequence with bounded cross-sequence concurrency

At most one head per managed sequence key is in flight. After the head's acknowledgement policy is satisfied, acknowledgement re-enters the serialized mutation path, verifies that the same record is still the head, removes it, persists the new snapshot, and advances that sequence. A failed or unaccepted head fences later records in that managed sequence, including records for a newer physical binding. Different sequence keys remain eligible and drain with a small bounded concurrency so one stale sequence cannot stop all delivery.

The outbox uses the existing bounded request timeout and retry timer pattern. Enqueue, startup, automatic reconnect, and forced reconnect all call the same idempotent `kick()` operation. Concurrent kicks share in-flight sequence work rather than starting duplicate requests.

Alternatives considered:

- One global FIFO: rejected because one stale binding would block every AgentSession on the runner.
- Key every queue only by logical AgentSession target: rejected because generic AgentJob reports remain outside this issue's outbox; such a key would falsely imply ordering across producers the outbox does not control. Workflow and generic-follow-up producer families are explicit in the key.
- Key queues by `runtimeSessionId`: rejected because later Workflow turns and binding changes belong to the same logical transcript; it would allow a newer binding to overtake an older pending turn.
- Retarget a stale head to the current binding: rejected because it could attach old content or a delayed close to the wrong turn.

### D5: Make outbox readiness part of runner lifecycle, not Action completion

The outbox loads before the runner starts accepting SignalR commands or claiming work. A missing file means an empty queue; an unreadable or invalid file makes the outbox unavailable. `RunnerHost` includes `outbox.ready()` in its claim gate, and the follow-up handler checks the same state before accepting a command. After SignalR starts, the initial drain is kicked asynchronously so a server outage does not delay runner startup. Both automatic and forced reconnect paths invoke the same drain hook.

`stop()` cancels network and local-persistence retry timers plus in-flight HTTP attempts but does not remove pending records. A later start reloads and resumes them.

Local health recovery is autonomous and independent of enqueue or network delivery. A post-start snapshot failure retains the latest desired in-memory state (except a rolled-back pre-execution input), marks `ready()` false, and starts one fake-time-testable retry timer. Each retry atomically writes the complete retained state; only successful rename of that snapshot restores health, cancels the local retry, and kicks network delivery. Startup read/parse failure keeps in-memory state unknown and retries load without writing an empty replacement. Startup, automatic reconnect, and forced reconnect also trigger the same idempotent load-or-save recovery operation. Thus claim and follow-up gates can recover even when no new execution is admitted.

Alternatives considered:

- Await a full startup drain: rejected because an unavailable Server would prevent the runner from becoming available even though execution and eventual delivery are intentionally decoupled.
- Silently fall back to direct best-effort upload when local state is unavailable: rejected because it reintroduces permanent event loss under the exact failure being fixed.

### D6: Import the existing follow-up terminal state idempotently

On first load, the new file store also reads `.mohist/runner-state/followup-failures.json` version 1. Each legacy entry is converted to a `session.followup_completed` or `session.followup_failed` record with deterministic ID `legacy-followup-terminal:{operationId}` and `successful-response` acknowledgement. The importer merges by ID, atomically persists the new snapshot, and only then marks the legacy file migrated. Re-running after a crash cannot create a second pending record for the same operation.

Alternatives considered:

- Discard the old file because runner state is local: rejected because those entries represent terminal facts already promised durable delivery.
- Keep both outboxes active: rejected because it restores competing delivery paths and ordering races.

### D7: Test behavior through injected stores, transport fakes, and fake time

Outbox unit tests will share one recording `RuntimeEventOutboxFileSystem` across two real snapshot-store/outbox instances to model restart. They will cover serialization, owner-only write options, temporary-write/rename ordering, matching and empty content receipts, successful empty terminal receipts including stale binding, timeout/retry with fake timers, per-sequence ordering, cross-sequence progress, concurrent kicks, snapshot failure, autonomous full-state recovery without a new enqueue, startup-load recovery without empty overwrite, and idempotent legacy import at every write/rename/marker boundary. No test will instantiate the Node filesystem adapter or touch a temporary directory.

Runner specs will verify that Workflow input/activity/close and follow-up input/outcome enter their managed sequences in order, that independent sequences continue, that runtime execution and Workflow results do not await HTTP delivery, and that retry never invokes `runtime.followup` again. Workflow integration tests will reject the input snapshot before runtime invocation and reject activity/close snapshots after multiple synchronous callbacks, proving no orphan input and preservation of the original runtime result. Generic follow-up regression coverage will prove AgentJob's direct chain remains unchanged and neither producer gains a cross-producer wait. Host specs will prove an unhealthy outbox prevents polling/claiming and autonomous health recovery resumes it without new work; SignalR follow-up specs will prove an unhealthy outbox returns `unavailable` before runtime invocation and later health recovery admits a command. Connection tests will verify receipt parsing for both endpoint shapes, and lifecycle tests will verify startup and reconnect recovery kicks. Existing real-filesystem `FollowupFailureOutbox` tests will be replaced rather than retained beside the new suite.

## Risks / Trade-offs

- [A content-event Server acceptance followed by a lost response causes replay and may duplicate transcript content] -> Keep one local logical record and remove it only on a matching receipt; accept at-least-once behavior because server deduplication is explicitly out of scope, and cover the ambiguous-response path in tests.
- [A valid empty follow-up terminal response can mean either consumed operation or stale binding] -> Preserve the existing successful-response policy and explicitly allow stale terminal records to settle without persistence; keep strict stale-binding retention only for matching-receipt content and Workflow terminal records. Distinguishing terminal outcomes would require a future Server contract change.
- [The runner exits after observing a Workflow event but before its asynchronous local enqueue commits] -> Start enqueue immediately, wait for all local writes before returning the turn result, and define completed local enqueue as the restart-recovery boundary; full in-progress turn journaling is outside this change.
- [A permanently stale binding leaves its sequence head pending forever and blocks newer events for that logical Session] -> Never retarget or drop the event; isolate the blockage to that Session, retain warning diagnostics without payload content, and leave administrative remediation to a follow-up change.
- [A long outage or permanently rejected event grows the local state file] -> Coalesce snapshot writes, bound concurrent drains, expose queue/store failures in runner logs, and do not apply a lossy retention limit in this change.
- [Snapshot rewrites amplify I/O for high-volume delta streams] -> Coalesce adjacent enqueue and acknowledgement mutations. If measured volume makes this material, replace the store behind the same outbox interface with an append journal in a separate change.
- [Disk-full, permission, or corruption failures can defeat local durability] -> Use atomic replacement and restrictive file permissions, fail runner readiness when state cannot be loaded, gate host claims and follow-up acceptance on outbox health, retry full-state persistence autonomously, and never silently switch to best-effort upload.
- [The outbox stores prompts and assistant/tool payloads on runner disk] -> Keep the file under runner-owned `.mohist/runner-state`, use owner-only permissions, and exclude payloads from diagnostics.
- [A crash during legacy import could leave both files present] -> Use deterministic imported IDs and persist the new snapshot before marking the old file migrated, making import replay-safe.
- [A generic follow-up can interleave with AgentJob direct reporting for the same AgentSession] -> Preserve source-local ordering and existing behavior; do not claim a cross-producer FIFO. Migrating AgentJob reporting is a separate change.

## Migration Plan

1. Add the shared outbox model, acknowledgement policies, injected file-I/O boundary, physical snapshot/import store, per-sequence drain logic, and recording-filesystem tests.
2. Change the generic `ServerConnection` runtime-events method to return the receipts already produced by the Server.
3. Construct and load the shared outbox in `RunnerHost`, add it to the claim and follow-up acceptance gates, pass it through `WorkExecutor`/`ActionContext` and `RunnerSignalRClient`, and unify startup, reconnect, and stop hooks.
4. Replace direct Workflow reporter uploads and follow-up input emission with durable enqueue; route follow-up terminal outcomes through the same outbox and remove `FollowupFailureOutbox` after its migration reader is covered.
5. Run runner typecheck and tests. No Server, database, Web, or API deployment ordering is required because endpoint shapes do not change.

Deployment may be rolling across runners because pending state never transfers between them. Each upgraded runner imports only its own legacy file and drains only its own events.

Rollback is safe only after the new outbox has drained. An older runner cannot understand new Workflow/input records, so operators must preserve `runtime-events.json` and either roll forward again or use a purpose-built converter before downgrading with pending entries. The migrated legacy file is not deleted until the new snapshot is durable, but restoring it alone cannot represent newly queued event kinds.

## Open Questions

None for this change. Server-side deduplication, stale-entry administration, queue size limits, and an append-journal store are explicit follow-up scopes rather than unresolved implementation decisions.
