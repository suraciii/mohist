## Context

The current Runner already has a host-owned durable outbox in `packages/runner/src/server/runtime-event-outbox.ts`, persisted at `.mohist/runner-state/runtime-events.json`. It assigns admission sequence numbers, writes atomic snapshots, batches delivery, retries failed transport, and schedules several delivery groups concurrently. `runtime-event-delivery.ts` maps records to the Workflow and AgentSession Server endpoints.

The defect is in the retention and settlement contract around that implementation. The current cap evicts only `reasoning.delta` and `message.delta`; protected input, terminal activity, tool, usage, model, and binding facts can therefore grow without a finite admission result. Some terminal paths use successful-response settlement or fire-and-forget enqueueing, and AgentJob observers still report directly through `ServerConnection`. A timeout or mismatched receipt can consequently leave ownership ambiguous, while one blocked workflow lane can delay records that should be independent.

The primary stakeholders are Runner and Server owners, Workflow and AgentSession execution paths, OpenCode/Pi observers, and operators diagnosing overloaded or disconnected Runners. The proposal and the `runtime-event-outbox-retention` and `runtime-event-delivery-liveness` specifications are authoritative for behavior. The durable record must retain exact event identity and physical `runtimeSessionId`; it must not retarget a record after binding recovery. Existing Workflow transcript and terminal-result semantics remain authoritative, and unrelated Web, CLI, Workflow ownership, `AgentResultSettlement`, and terminal-result arbitration are outside this change.

## Goals / Non-Goals

**Goals:**

- Enforce a finite default capacity of 5,000 logical records across memory and the durable snapshot, with deployment configuration able to override it.
- Make protected admission, persistence failure, and outbox health observable as awaitable outcomes. Never append beyond the bound or report a rejected record as admitted.
- Preserve protected records exactly through retention, restart, retry, receipt mismatch, timeout, and transport failure.
- Define deterministic, identity-scoped compaction for deltas and only those tool-call, usage, or binding facts whose Server-visible effects can be proven equivalent.
- Deliver each logical sequence FIFO, with independent progress for unrelated target sequences and bounded fair scheduling.
- Make receipt matching strict for event type, record identity, logical target, AgentSession identity, Agent turn identity, and physical runtime identity where the protocol supplies it. Make leases and late responses idempotent.
- Propagate capacity and reporting failures through Workflow input admission, synchronous observers, follow-up handling, terminal settlement, and AgentJob reporting without re-invoking runtime execution.
- Emit bounded structured diagnostics for pressure, unsafe compaction, receipt mismatch, transport failure, and timeout.

**Non-Goals:**

- Adding a live cleanup or operator purge operation for the outbox.
- Reconstructing a dropped protected fact, fabricating a receipt, or replacing an old physical binding with a newer one.
- Changing runtime execution, model selection, Workflow ownership, terminal result arbitration, or Server transcript semantics.
- Allowing retry of an HTTP delivery to invoke a Workflow, follow-up, or Agent runtime turn again.
- Introducing a general-purpose event store or a new external dependency.

## Decisions

### 1. Make the outbox the single bounded admission authority

Extend the existing outbox ports rather than introducing another queue. Add a retention class and, where needed, a compaction descriptor to the durable record model. The classifier is centralized in the outbox so producers cannot accidentally mark a protected fact as discardable:

- `session.input`, terminal `session.activity`, `turn.failed`, and every tool-call, usage, model, and binding-reconciliation fact are protected in this release.
- Only `reasoning.delta` and `message.delta` may be compacted. A delta is compactable only when its payload has a non-empty `text`, `partId`, and `messageId`, and its applicable `turnId`, logical target, physical runtime session, and sequence identity are present. Adjacent deltas must have the same event type and all of those identities.
- The text reducer concatenates `text` in admission order, keeps the earliest record ID as the representative, retains every replaced source ID and a `compactedRawEventCount`, and never crosses a non-delta record, turn boundary, logical target, physical runtime session, text part, or sequence boundary. The Server transcript accumulator applies that count so text, ordering, and raw-event accounting remain equivalent.
- Tool-call, usage, model, and binding payloads have no reducer in this release. Missing identity, unsupported event shape, or any attempted compaction of those facts makes every source record protected and emits `unsafe-compaction` diagnostics.

Admission first applies only the text-delta reducer above within the same logical target, physical runtime session, turn, text part, and sequence boundary. If the resulting protected-plus-compactable set still exceeds the configured capacity, the new record is rejected with a structured `protected-capacity` outcome. No protected record is evicted, and no record is appended past the limit. A batch admission is atomic: either all records are admitted after one capacity decision and snapshot write, or the batch is rejected without partial admission.

Change `enqueueBeforeExecution`, `enqueueProducedFact`, and `enqueueProducedFactBatch` from an uninformative `Promise<void>` contract to `Promise<RuntimeEventAdmissionResult>`, a resolved discriminated union with `status: admitted | compacted | rejected`, `recordId` or record IDs, `reason`, `capacity`, `pendingCount`, and `retryable`. Storage errors remain typed failures with the same observable context. Input admission uses a rollback-on-write-failure path; post-start facts remain in the desired in-memory state for local persistence recovery only when they were actually admitted to that state. `ready()` remains a health gate, and protected-capacity pressure is exposed separately so a full but readable outbox is not confused with a corrupt snapshot. The deployment override is `RunnerOptions.runtimeEventOutboxMaxRetentionEntries`, populated by `RUNTIME_EVENT_OUTBOX_MAX_RETENTION_ENTRIES` and passed to the host-owned outbox.

On load, seed the sequence allocator above the maximum persisted sequence and validate duplicate IDs, sequence values, and retention metadata. An over-capacity legacy or current snapshot is not truncated blindly: all protected entries remain with their original IDs and payloads, safe streaming entries may be compacted according to the same deterministic rules, and the outbox remains under pressure until enough records settle. The snapshot is not overwritten with an empty state or a protected-record truncation.

**Alternative considered: continue evicting the oldest deltas and let protected records grow.** Rejected because the total logical record count remains unbounded and protected admission cannot be reported honestly.

**Alternative considered: reserve a separate protected-record quota.** Rejected as the primary bound because it hides total memory and snapshot growth and can still starve producers when the protected partition is full. One total cap with explicit protected pressure gives operators the relevant capacity signal.

### 2. Keep sequence ordering and physical identity separate but authoritative

Represent each record with an immutable admission sequence, logical sequence key, and physical delivery identity. The logical key contains producer family and target identity; the physical identity contains the recorded runtime, `runtimeSessionId`, AgentSession/turn/work identity, and any input or operation identity. The scheduler uses the logical lane to preserve FIFO across binding changes, while a delivery batch may contain only records with the exact same physical delivery identity and Server endpoint contract.

The head of each logical lane is the only eligible record or batch. A newer record with `runtimeSessionId=B` cannot overtake an older pending record for `runtimeSessionId=A` in that lane. An unrelated Workflow target, AgentSession target, or producer family has its own lane and remains eligible. At most one attempt is active per lane, with a bounded number of lanes selected per tick by a round-robin cursor. Retry backoff is tracked per lane so repeated failure in one lane cannot starve a newly deliverable lane.

Keep the existing endpoint-specific adapter, but make batch construction validate the complete delivery identity rather than copying work metadata from the head. The adapter must preserve one receipt slot per submitted record and normalize the Server response into a common receipt containing event type and record/identity fields.

**Alternative considered: use one global queue and serialize every HTTP call.** Rejected because a blocked receipt or transport request for one Workflow would prevent unrelated sequences from progressing.

**Alternative considered: group only by `runtimeSessionId`.** Rejected because physical identity alone does not preserve Workflow target, producer family, turn boundaries, or the ordering fence across an old and new binding.

### 3. Use strict identity matching and explicit delivery leases

Every delivery attempt gets a unique local attempt/lease ID, a start time, timeout, submitted record IDs, and the immutable sequence positions. The outbox keeps the attempt state until the response is classified. On timeout, the records remain pending and the lane remains ordered; a retry may start according to backoff, but a late response from the expired attempt can settle only the same still-pending record and only through that attempt's receipt policy. If a newer retry settled it first, the late response is ignored. Persistence of removal completes before the next record in that lane can settle. Responses for a record already removed are idempotently ignored.

Replace the current `successful-response` shortcut for protected terminal facts. `session.input`, terminal `session.activity`, and every other protected record require a positive receipt matching event type, submitted record ID, logical target, AgentSession/turn identity, and physical runtime identity where applicable. The coordinated wire contract is version 2: every runtime-event request carries `runtimeEventContractVersion: 2`, one canonical `logicalTarget`, the physical `runtimeSessionId`, applicable runtime/session/turn identity, and each event carries `runtimeEventId`, `type`, and `payload`. Every acceptance is a `RunnerRuntimeEventReceipt` carrying `runtimeEventContractVersion`, `runtimeEventId`, `type`, `logicalTarget`, `runtimeSessionId`, and the applicable `agentSessionId`, `agentTurnId`, and `inputDeliveryId`; batch responses have exactly one positional receipt per submitted event. The durable input ID is the `runtimeEventId` for `session.input`, and `cleanupOperationId` is the `runtimeEventId` for `session.cleanup`. `ServerConnection` rejects absent, old-version, malformed, or count-mismatched responses before settlement. An empty, non-success, stale, or mismatched response retains the original record and classifies the failure; it never settles a later record.

The exact v2 DTO shape is shared by the workflow runtime-events, generic runtime-events, and Session runtime-events endpoints. `logicalTarget` is a tagged union: `{ kind: "workflow", projectId, workflowRunId, sessionName }`, `{ kind: "generic", projectId, sessionId }`, or `{ kind: "session", sessionId }`; the route-derived target must equal it. The request envelope is `{ runtimeEventContractVersion: 2, logicalTarget, runtime: "opencode" | "pi" | null, runtimeSessionId, agentSessionId, agentTurnId, inputDeliveryId, taskRunId, workId, workType, stage, runtimeEvents: [{ runtimeEventId, type, payload }] }`, with identity fields explicitly `null` when they are not applicable. The Server requires `runtimeEventId`, `type`, and `payload` for every item and requires all applicable envelope identities; all records in a batch share the envelope identity. The response is an array with exactly one `RunnerRuntimeEventReceipt` in each submitted position: `{ runtimeEventContractVersion: 2, runtimeEventId, type, logicalTarget, runtime: "opencode" | "pi" | null, runtimeSessionId, agentSessionId, agentTurnId, inputDeliveryId }`, again using explicit `null` for non-applicable fields. The cleanup-turn endpoint uses the same workflow target and identity fields plus `cleanupOperationId`, `prompt`, `taskRunId`, and `workId`; it is a single-record endpoint but returns a one-element receipt array, with `runtimeEventId === cleanupOperationId`. No endpoint has a successful-response or object-only compatibility shape after v2.

`RuntimeEventAdmissionResult` is the public resolved union: `admitted` has `recordIds`, `capacity`, `pendingCount`, `retryable: false`; `compacted` has `recordIds` (including the representative), `replacedRecordIds`, `capacity`, `pendingCount`, `retryable: false`; and `rejected` has `recordIds`, `reason` (`protected-capacity` or `unsafe-compaction`), `capacity`, `pendingCount`, and `retryable: true`. A snapshot persistence failure remains a typed rejected promise with the failed record IDs, sequence context, and retryability; it is not converted into an admitted or compacted result.

A successful-response policy may remain only for explicitly non-protected events whose endpoint contract proves acceptance without an event-specific receipt. It is not permitted for `session.input` or terminal `session.activity`. Receipt mismatch must not trigger binding recovery or record retargeting; the retry submits the original target and payload unchanged.

#### Server acceptance-ledger owner

`AgentSessionGrain` owns replay idempotency; it is not a DTO or Runner-only concern. Extend `AgentSessionRuntimeEventInput` and `AppendAgentSessionRuntimeEventsCommand` with the immutable `runtimeEventId` and the command's v2 delivery identity. Persist an `AgentSessionRuntimeEventAcceptance` entry for every accepted record containing `runtimeEventId`, an identity-and-payload fingerprint, and the complete `RunnerRuntimeEventReceipt`. The ledger lives in the durable AgentSession state for the lifetime of the AgentSession, because a late retry after the Runner has removed its record must still receive the original receipt.

Before applying a batch, the grain validates every submitted identity and checks every ID in the ledger. A matching existing fingerprint returns its stored receipt without applying the domain event, appending a transcript row, invoking follow-up dispatch, or calling the Workflow binding port again. A conflicting fingerprint is a hard conflict for the whole batch. A mixed batch of duplicates and new records is processed in admission order: all new records and their ledger entries are applied in one grain state commit, then the response returns the stored and new receipts in the original positional order. A persistence failure returns no acceptance, so retrying the original record is safe. The same ledger check and receipt construction is used by `AcceptWorkflowInputAsync`, `AcceptWorkflowCleanupAsync`, `AppendRuntimeEventsAsync`, and the workflow, generic, session, and cleanup HTTP routes; specialized input/cleanup operations keep their existing turn-binding validation but cannot bypass the ledger.

The Server contract tests must post the same `runtimeEventId` twice through each route, assert one transcript/domain application and byte-equivalent receipts, test duplicate-plus-new and conflicting mixed batches, and prove that an old or incomplete request cannot settle a Runner record.

**Alternative considered: treat any valid 2xx response as terminal success.** Rejected because replay, empty receipts, and stale responses cannot prove which protected fact was accepted.

**Alternative considered: discard a timed-out attempt and accept only the next attempt's response.** Rejected because a late response can be the only positive acknowledgement and must be able to settle the original still-pending record without duplicating execution.

### 4. Make producer reporting awaitable without throwing through runtimes

Keep runtime observers synchronous at their provider boundary, but have them enqueue an awaitable reporting promise in a per-execution reporter. `WorkflowAgentSessionReporter.settle()` returns a structured reporting result and preserves the first protected-capacity or persistence failure. It does not throw from `onEvent`; the owning action observes the result at its existing settlement boundary. A rejected initial or follow-up `session.input` returns an explicit unavailable/backpressured outcome and the runtime turn is not invoked. A post-start admission failure is surfaced as reporting failure while the runtime's actual success or failure remains the execution result.

Route the remaining AgentJob `createAgentSessionEventSink` input and observer paths through the same host-owned outbox, carrying the work and AgentSession identity already available in the dispatch. Add that outbox as a required `AgentJobTurnDeps` dependency. AgentJob performs a pre-run physical-session phase for both OpenCode and Pi: resolve the existing binding or create the physical session, open/attach the Server AgentSession, durably enqueue the `session.input`, and await its positive v2 receipt before calling `runtime.runTurn`. OpenCode's current `onSessionReady` callback is no longer the input-admission boundary; `runTurn` receives the prepared non-null runtime session ID, and the callback may only verify the prepared identity. If admission, persistence, attach, or receipt matching fails, the executor returns `execution-unavailable` and `runTurn` is never called. Once the gate passes, observer facts are outbox records and their asynchronous failures are reported by `drain()` without throwing into the provider callback.

Remove direct fire-and-forget calls to `ServerConnection.agentSessionRuntimeEvents` for durable runtime facts. Follow-up terminal activity is enqueued and tracked through its operation completion boundary; a failure is observable in the command/operation diagnostic and never changes a completed runtime result into a fabricated one. Binding convergence also uses the outbox; because no lossless binding reducer is selected, different or repeated binding facts remain protected and ordered.

The host loads the outbox before SignalR command registration, work claims, or follow-up admission. Capacity pressure is a distinct admission gate from unreadable or failed persistence. Recovery retries local persistence and network delivery without accepting new protected records when no capacity action exists.

**Alternative considered: throw from the observer and let the runtime abort.** Rejected because synchronous provider callbacks may crash or corrupt runtime result handling, and post-start reporting failure must not rewrite the runtime's actual result.

**Alternative considered: keep direct AgentJob HTTP reporting and add only a larger warning threshold.** Rejected because it leaves a producer outside bounded durable admission and preserves the exact overload failure mode.

### 5. Centralize bounded diagnostics

Add an outbox diagnostic aggregator keyed by reason and logical lane. It records counts, first/last timestamps, capacity, pending count, and safe identity labels such as target kind, workflow/session identifier, producer family, and sequence key; it never logs event payloads. Emit on state transitions or a bounded interval. Reasons are distinct for protected-capacity pressure, unsafe/rejected compaction, receipt mismatch, transport failure, timeout, and persistence failure. The aggregator is reset or summarized when a lane recovers so repeated retries do not produce one warning per record.

## Risks / Trade-offs

- [A full protected set can remain above the configured record target after restart] -> Retain every protected record exactly, expose pressure, reject further protected admissions, and require normal successful delivery to create capacity; never repair the condition by truncation.
- [Compaction can silently change transcript or accounting semantics] -> Require type-specific identity and reducer validation, persist the compaction identity, test reducer equivalence against Server-visible invariants, and leave unsupported records protected.
- [A late response can race with a retry and snapshot persistence] -> Associate responses with attempt IDs, compare the still-pending record object and sequence position, serialize removal through the snapshot write tail, and make already-settled responses no-ops.
- [Changing receipts and enqueue return types breaks producer and Server contracts] -> Treat the change as coordinated and breaking, update adapters and all producer test doubles together, and fail closed on old or malformed receipt shapes.
- [Moving AgentJob reporting into the shared outbox can delay job settlement under pressure] -> Make input admission backpressure explicit before runtime invocation, bound observer buffering, and preserve runtime results while exposing post-start reporting failure.
- [Per-lane fairness can increase total latency when many lanes are active] -> Keep concurrency and batch size configurable, use round-robin selection with per-lane backoff, and measure pending counts and attempt latency.
- [Legacy snapshots lack new retention or receipt metadata] -> Parse the existing version into protected-by-default records, seed sequence allocation from persisted data, and write the new snapshot version only after a successful validated load and migration.
- [Aggregated diagnostics may hide an individual failing record] -> Include bounded logical sequence and pending-count context and expose aggregate counters while excluding payload contents.

## Migration Plan

1. Implement the outbox retention classifier, `RuntimeEventAdmissionResult` union, sequence seeding, over-cap load behavior, the text-delta-only reducer, diagnostic aggregation, and unit tests for protected pressure, exact text equivalence, unsafe tool/usage/binding compaction, and legacy snapshots.
2. Implement the v2 receipt contract and Server acceptance ledger together. Add `runtimeEventId` and identity fields to the Orleans commands and endpoint DTOs; persist an acceptance fingerprint and exact receipt in AgentSession state; atomically deduplicate mixed batches; and update `AcceptWorkflowInputAsync`, `AcceptWorkflowCleanupAsync`, `AppendRuntimeEventsAsync`, and all workflow, generic, session, and cleanup routes. Update `ServerConnection`, `runtime-event-delivery.ts`, and Server contract tests. Reject malformed or short acceptance arrays before settlement.
3. Replace lease handling and batch settlement in the outbox. Add deterministic tests for FIFO fences, cross-lane liveness, fair retries, timeout/late receipts, mismatched receipts, and no duplicate removal.
4. Update `WorkflowAgentSessionReporter`, follow-up handling, `agent-job-turn.ts`, both runtime session ports, binding convergence, and terminal settlement boundaries to observe admission/reporting outcomes. Implement the OpenCode/Pi pre-run physical-session and input-admission phase, and confirm a rejected AgentJob input never invokes `runTurn` while a post-start failure never replaces the runtime result.
5. Add `runtimeEventOutboxMaxRetentionEntries` to `RunnerOptions`, parse `RUNTIME_EVENT_OUTBOX_MAX_RETENTION_ENTRIES`, pass it through `RunnerHost`, and load and health-gate the outbox before accepting work. Add structured diagnostics assertions and operational counters.
6. Deploy Runner and Server together, then monitor protected-capacity, compaction-rejection, mismatch, timeout, and transport-failure aggregates. Existing records are replayed with their original IDs, sequences, payloads, and physical bindings.

Rollback is version-boundary based. Before any new snapshot or receipt version is written, revert the binaries normally. After the new snapshot version or new receipt fields are persisted, do not run an older Runner against the live state: restore the pre-deployment Runner-state backup and deploy the compatible previous Server/Runner pair. There is no live rollback that truncates protected records or converts a strict receipt into a successful-response acknowledgement.

## Resolved Contract Decisions

- The durable wire field is `runtimeEventId`; `id` is not accepted as an alias. All runtime-event routes use `runtimeEventContractVersion: 2`, and the workflow, generic, session, and cleanup route changes plus the matching Orleans grain changes deploy as one Server/Runner compatibility boundary. A v1 or missing-version response fails closed and leaves the record pending.
- This release has no tool-call, usage, model, or binding reducer. Those records remain protected under pressure. Only adjacent, identity-complete text deltas use the reducer defined in Decision 1, and the Server transcript test proves text and raw-event accounting equivalence.
- Protected-capacity rejection is a resolved `RuntimeEventAdmissionResult` with `status: "rejected"`; persistence and transport failures remain typed rejected promises carrying the same record and sequence context. Producers await the result or the typed failure at their existing action boundary.
- The 5,000-record override is per Runner host and is supplied by `RunnerOptions.runtimeEventOutboxMaxRetentionEntries`, populated from `RUNTIME_EVENT_OUTBOX_MAX_RETENTION_ENTRIES`; it applies to the host's single durable outbox snapshot.
