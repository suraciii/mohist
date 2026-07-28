## Context

Issue 512 makes manual Agent launch reliable across Web/CLI response loss, client disconnection, and Server or Runner restart. Today `AgentSessionLaunchRoutes` validates a request, then `AgentLauncher` creates random Session and Job IDs, opens the Session, and submits the Job. The `201` response exposes only Job and Session identities. The first input is first reported later by the Runner as a `session.input` runtime event, so neither it nor its turn has a durable identity at acceptance time; repeating a manual request creates another launch.

The change implements `agent-launch-idempotency` and `agent-launch-observation`. `AgentJob` remains the authority for the first work result. `AgentSession` remains the authority for SessionInput, AgentTurn, activity, transcript, and runtime binding. Server remains the state authority; Runner reports facts only. The architecture permits no cross-aggregate transaction, so the four launch facts cannot be created by one write.

## Goals / Non-Goals

**Goals:**

- Accept one stable manual-launch identity from Web and CLI and map it to one immutable launch plan.
- Durably establish the Job, Session, first Input, and first Turn before reporting acceptance; recover unfinished setup after activation or process loss.
- Give every accepted launch a composite, read-only observation that keeps Job status, Session activity, Input acceptance, and Turn status separate.
- Preserve Unknown when Runner execution cannot be confirmed; reconcile the original work identity without automatically dispatching another prompt.
- Keep existing generic Session, Job read, transcript, and Runner binding surfaces as the underlying ownership boundaries.

**Non-Goals:**

- Follow-up input, multi-input scheduling, Compact, Reset, Cancel, or stop behavior.
- Routed, mention, Slack, or other Agent Connection launches.
- Network exactly-once delivery, a public API-key model, or a stored-data rewrite.
- Treating Session as the owner of AgentJob result, or turning Input/Turn into top-level aggregates.

## Decisions

### D1. Use `Idempotency-Key` as the manual launch identity

`POST /api/projects/{projectRef}/agents/{agentRef}/sessions` requires a non-empty `Idempotency-Key`. The key scope is `(ProjectId, key)`; it is opaque to Server business logic. The route continues all existing validation before it creates a launch plan.

Web generates the key before its first mutation and retains it with the pending composer attempt until that attempt receives its result. CLI accepts `--idempotency-key`; when omitted, it generates a UUID, prints it before sending the request, and reuses it for in-process transport retries. A new CLI process retries an interrupted request with the emitted `--idempotency-key` value. Neither client derives a key from prompt content.

The coordinator stores a canonical request fingerprint and resolved Agent snapshot with the key. A replay with matching canonical content resumes or returns the original plan. A replay with different Agent, prompt, context, or resolved execution snapshot returns `409 launch_idempotency_conflict` and cannot mutate the original plan.

**Rationale:** the existing recovery commands already use this header, so it is a single transport convention rather than a second request-id language. Content-derived keys would collapse intentional identical launches; server-generated keys cannot recover a lost response.

**Alternatives considered:** a body `requestId` was rejected because it duplicates the established command boundary; a client-local retry journal was rejected because it introduces retention and cleanup semantics while still not helping another client; random IDs per retry were rejected because they duplicate work.

### D2. Introduce a Project-and-keyed durable launch coordinator

Add `AgentLaunchCoordinatorGrain`, keyed by the reversible public codec for `(ProjectId, IdempotencyKey)`. It is a durable application process manager, not a business aggregate. Its persisted `LaunchPlan` holds only the canonical request snapshot, generated opaque Job/Session/Input/Turn IDs, request fingerprint, and the current single-participant command fence. It does not mirror Job status, Session activity, transcript, or Runner state.

The coordinator advances the following recoverable sequence:

1. Resolve and snapshot the Agent and manual context once; persist the plan and its generated IDs.
2. Send `PrepareManualLaunch` to the AgentJob participant. The Job stores its immutable execution snapshot and initial IDs in `pending` state, but does not dispatch.
3. Send `EnsureInitialLaunch` to the AgentSession participant. The Session opens with the fixed source metadata, persists the first `SessionInput` as accepted and the first `AgentTurn` as queued, and associates both with the Job ID.
4. Send `SubmitPreparedLaunch` to the Job participant. The Job becomes eligible for normal Runner assignment using the same Job/work dispatch path as today.

Before each participant call the coordinator persists `Pending { commandId, kind, payload, expectedRevision }`; it clears that fence only after an explicit applied or already-applied response. A reminder and reactivation resume the same fence. Each participant command is independently idempotent by its generated IDs and rejects a mismatched plan. The HTTP request returns `201` only after steps 1-4 have durably completed; a timeout during the sequence is retried with the same key.

**Rationale:** this preserves one aggregate write per transaction while making the cross-aggregate setup restart-safe. It follows the existing prepared routed-launch pattern without making routed launch, Session, or Job depend on a new manual API abstraction.

**Alternatives considered:** direct `AgentLauncher` calls to Session then Job were rejected because a crash creates an untraceable partial launch and replays mint new IDs; a database transaction across Job and Session was rejected by the aggregate boundary; putting Session facts into `AgentJobState` was rejected because it gives Job a second authority for conversation state.

### D3. Persist initial Input and Turn in AgentSession before Runner execution

Extend the Session domain state with ordered child records for the initial launch only:

```text
SessionInput { id, sequence, text, source: agent-launch, acceptance }
AgentTurn   { id, sequence, inputIds, status, jobId, result? }
```

`EnsureInitialLaunch` atomically opens the Session when absent, records the Input as `accepted`, records the Turn as `queued`, and sets Session activity to `active`. Repeating the command with the same IDs is a no-op; incompatible IDs or immutable source metadata conflict. Input and Turn remain children addressed through their Session and are exposed in the composite observation, not as independent list resources.

The coordinator passes the generated Input/Turn IDs through the Job dispatch. The Runner stops creating the initial `session.input` record. It reports only facts correlated to those IDs: work accepted/running, terminal result, and unresolved execution. Durable Job events drive idempotent Session updates for `executing` and terminal Turn state; Job stays the result authority while Session records the corresponding conversation fact.

**Rationale:** acceptance belongs to Server, not to a later Runner callback. Recording the input before dispatch means queueing and client loss cannot erase or replace it.

**Alternatives considered:** retaining Runner-created input events was rejected because no accepted Input exists while queued or before Runner attach; storing Input/Turn only in the transcript was rejected because transcript facts cannot enforce child identity, association, or current status.

### D4. Model unresolved first execution as Unknown, with no replay

Add `Unknown` to the AgentJob and initial AgentTurn observable status models. If Server cannot determine whether the assigned work accepted or completed the original prompt, it retains the Job ID, work ID, Input, and Turn and moves the affected work fact to `unknown`; it does not synthesize `failed`, `idle`, or a new dispatch.

Runner reconnect reconciliation uses the persisted Job/work identity. A positive running report returns the original Turn to `executing`; an authoritative terminal report resolves the original Job and Turn; absent or inconclusive runtime evidence leaves it `unknown`. Runner loss and status timeouts must no longer transform an uncertain in-flight prompt into a retryable new launch. Session activity follows the stored Turn fact and is never inferred from prior terminal transcript entries.

**Rationale:** an unavailable Runner is not evidence that a provider did not execute a prompt. Preserving uncertainty is the only way to prevent duplicate side effects.

**Alternatives considered:** immediately failing on Runner loss was rejected because it lies about delivery and encourages a duplicate launch; automatic retry on reconnect was rejected because Runtime acceptance is not provable; holding every unknown forever as `running` was rejected because it hides the need for reconciliation.

### D5. Add one composite launch observation while retaining existing read owners

Add `GET /api/projects/{projectRef}/agent-jobs/{jobId}/launch-observation`. It verifies the Job belongs to the Project, resolves its linked Session, and returns an `AgentLaunchObservationDto`:

```text
job:     { id, status, terminalResult? }
session: { id, activity, runtime?, transcriptUrl }
input:   { id, acceptance }
turn:    { id, status, result? }
```

The launch `201` includes all four IDs and an observation URL. Job result fields continue to come from `AgentJob`; Session/Input/Turn/activity/transcript fields come from `AgentSession`; the assembler only composes read models. The existing Job view, unified Session view, and transcript routes remain valid focused reads. Input and Turn identifiers are returned as child references, never as new top-level API resources.

Web navigates to the Session page using the returned Session ID and loads the composite observation plus transcript. CLI prints the four IDs, the observation URL, and uses the same DTO state vocabulary. Both clients render server-provided state and recovery guidance: observe for pending/queued/executing, read result/transcript for terminal, and re-read or retry using the original key for Unknown.

**Rationale:** one composite query lets a reconnecting caller reconstruct the launch without teaching clients how to join unrelated read models. It preserves the domain rule that Session children are accessed through their Session.

**Alternatives considered:** four new independent REST resources were rejected because Input and Turn are not top-level entities; client-side joining was rejected because it would duplicate state interpretation and produce stale mixed snapshots; replacing current Job/Session routes was rejected because they remain useful canonical focused reads.

### D6. Test recovery at ownership boundaries

Server specs use the in-process grain cluster, migrated in-memory SQLite, fake `TimeProvider`, and fake dispatch observers. They cover duplicate/concurrent same-key submissions, plan-content conflict, each coordinator crash fence, queueing, Job/Session child persistence, Runner reconnect convergence, Unknown preservation, project isolation, and composite observation consistency.

Runner tests inject a fake Runtime and Server connection. They verify the initial `session.input` is no longer runner-created, all reports retain the supplied Input/Turn/work IDs, and reconnect never submits a second prompt. Web tests verify a pending key survives mutation retry and that the state mapping comes solely from the observation DTO. CLI tests verify header forwarding, emitted key reuse, four identifiers, and Unknown guidance. No test uses a real Runtime, network, process, filesystem, or clock.

## Risks / Trade-offs

- [Coordinator plan contains prompt and execution snapshot until launch setup completes] -> Persist it only as the command payload needed for recovery; clear command fences when applied and rely on Job/Session as the long-lived owners.
- [A request can time out while the coordinator later completes] -> The same `Idempotency-Key` returns the canonical IDs and observation; callers must not use a new key for recovery.
- [Unknown can leave capacity unavailable longer than an eager failure] -> Reconcile by the original Runner/work identity and expose actionable Unknown state; never exchange correctness for speculative re-execution.
- [Composite observation reads multiple owners and is eventually consistent] -> Return each owner’s explicit state, omit unavailable terminal fields rather than fabricating them, and let subsequent reads converge; the coordinator is never a state cache.
- [CLI process loss before the caller records an auto-generated key limits cross-process retry] -> Print the key before the request and document `--idempotency-key` as the retry contract; automatic in-process retries always reuse it.
- [New Job `Unknown` affects list, capacity, and terminal assumptions] -> Treat it as non-dispatchable and nonterminal, update all enum projections deliberately, and add architecture tests that Job alone resolves it from Runner facts.

## Migration Plan

1. Add Session child records, Job `Unknown`, coordinator persistence, participant commands, and durable-event handlers behind the existing manual launch route; preserve current Job/Session reads.
2. Add the `Idempotency-Key` validation, coordinator-based `AgentLauncher` path, expanded launch response, and composite observation route. Update Runner dispatch and event reporting to carry Input/Turn IDs and stop publishing the initial `session.input`.
3. Update Web and CLI to generate/forward/reuse the key, render the four IDs and observation vocabulary, and use the existing Session transcript for continuous output.
4. Add server, runner, Web, and CLI regression coverage, then remove the manual route’s random-ID/session-open/direct-submit sequence.
5. Deploy Server before clients. Older clients lacking the required header receive an actionable validation error; no compatibility alias is added because the project is under active development. Roll back by reverting the complete change; persisted coordinator plans are inert without the coordinator and Job/Session records remain readable through current routes.

## Open Questions

- None. The wire identity is `Idempotency-Key`; the coordinator, child-record ownership, Unknown semantics, and composite observation shape are fixed by this design.
