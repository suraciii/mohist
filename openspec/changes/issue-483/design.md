## Context

Issue 483 replaces the current terminal Session protocol with the reusable Turn model defined in [design/agent-execution.md](../../../design/agent-execution.md). Today, the Server accepts `session.input`, `session.closed`, and terminal Follow-up events through `AgentSessionGrain`; `TranscriptAccumulator` implicitly chooses the latest transcript row, and query/Web projections infer Session terminal status from `session.closed`. The Runner's shared durable outbox persists and retries those same event names.

This makes a completed TaskRun or AgentJob execution close a logical AgentSession, even though work result, conversation activity, Runtime binding, and physical Runtime lifetime are separate authorities. This design implements the contracts in [proposal.md](proposal.md) and its three capability specs without changing TaskRun, AgentJob, Workflow retry, Runtime resource retention, or adding archive/delete command surfaces.

The Session bounded context owns transcript acceptance, activity, binding, command eligibility, and Follow-up operation state. The Runner owns runtime invocation and durable fact delivery. TaskRun and AgentJob remain the sole owners of their work results; Web remains a projection consumer.

## Goals / Non-Goals

**Goals:**

- Make `turn.started`, `turn.input.added`, `turn.finished`, and the three `followup.*` admission facts the only Session execution protocol.
- Persist a stable `turnId` on every Turn-scoped fact, validate Turn ordering and terminality in the Session grain, and project activity independently from binding and the latest result.
- Preserve exactly-once intent for Follow-up input through durable operation and Turn identities, including the case where Runtime acceptance cannot be confirmed.
- Convert persisted transcript data and Runner outbox snapshots before they are processed by the new protocol.
- Make OpenCode, Pi, Workflow, AgentJob, API, and Web use the same protocol and read model.

**Non-Goals:**

- Change TaskRun or AgentJob verdict, retry, recovery, or dispatch ownership.
- Define Runtime cache eviction, process lifetime, physical-session file retention, Session archive/delete, or new CLI command surfaces.
- Support a mixed or dual-written legacy/new protocol after migration.

## Decisions

### 1. Make the Session grain the sole protocol interpreter

`AgentSessionGrain` will replace close-specific commands and Follow-up lease confirmation methods with narrow operations that reserve and resolve Turn or Follow-up facts. It will validate event shape, binding, `turnId`, operation correlation, per-Turn order, and duplicate delivery before persisting Session state and transcript changes. The grain will own a compact current-Turn state, latest-Turn summary, activity, and pending Follow-up operation fences; transcript rows remain the audit history.

Workflow and AgentJob entry paths will mint a replay-stable Turn ID before invoking a Runtime and include it in the Runner execution context and every resulting fact. The Follow-up route will allocate `operationId`, and for `new-turn` also `turnId`, before dispatching to the Runner. The Runner must persist the resulting fact in its outbox before an invocation whose outcome needs that identity.

This keeps state arbitration at the aggregate boundary and lets retrying producers replay facts rather than recreate a conversation operation. The alternative, allowing each Runtime adapter or Web/API route to decide activity and synthesize transcript state, would duplicate the invariants and reintroduce disagreement between OpenCode, Pi, Workflow, and AgentJob paths. A separate cross-aggregate coordinator is rejected because Session/Runtime binding is explicitly outside that pattern and this is one Session aggregate's state transition.

### 2. Use explicit Turn facts and a Turn-keyed transcript store

The Runner-to-Server event contract will add `turnId` to all Turn-scoped payloads. `turn.started` carries the first input and source (`task-run`, `agent-job`, or `followup`); an executing Follow-up uses `turn.input.added`; `turn.finished` contains the only Turn outcome and optional failure data. The Server will reject legacy terminal event names and any fact without the required Turn identity.

`AgentSessionTranscriptTurnRow` will gain the durable domain `TurnId`, unique within `SessionId`, and the transcript persistence API will upsert by that pair rather than selecting the latest runtime/session row. Transcript parts will continue to use their row-local sequence but inherit their Turn through the parent row. `TranscriptAccumulator`, event serialization, transcript loaders, summary reducers, and AgentOps assemblers will use the explicit Turn identity and treat `turn.finished` as the terminal part.

This preserves the existing efficient accumulated-part storage while making identity and ordering explicit. Storing all transcript facts as a new append-only event table was considered, but it would duplicate the current transcript model and expand this protocol migration beyond its required behavior. Retaining implicit latest-turn selection was rejected because concurrent/retried facts cannot be validated safely that way.

### 3. Project independent activity, binding, and Turn summaries

`AgentSessionStatusSnapshot` and Session DTOs will replace the terminal Session `Status`/completion projection with:

- `activity`: `idle`, `active`, or `unknown`.
- `binding`: derived as `unbound`, `bound`, or `missing` from the Runtime binding and registered Runtime availability.
- `currentTurn`: the active or uncertain Turn identity and start summary.
- `latestTurn`: the latest finished Turn identity, timestamps, outcome, and failure summary.

`turn.started` makes activity active; only `turn.finished` makes it idle and clears `currentTurn`. A missing binding does not change activity to a terminal Session state. An unconfirmed new-Turn delivery creates a candidate `currentTurn` with activity `unknown`; confirmed rejection clears it and restores idle. A Cancel attempt that lacks confirmed Runtime stop also becomes unknown. Compact and Reset reject active or unknown Sessions; Follow-up requires idle, bound, and available Runner; Cancel targets only `currentTurn`.

The alternative of retaining a Session status enum with additional terminal values is rejected because it still conflates the latest Turn result with current operability. Web, API, and AgentOps will consume the same Server projection rather than independently derive eligibility from transcript events.

### 4. Model Follow-up admission as an operation state machine

The Session grain will persist one fence per Follow-up operation containing `operationId`, intended placement, Turn ID, and resolution state. The states are pending delivery, `admitted`, `rejected`, and `delivery-unconfirmed`; only admitted and rejected are mutually exclusive final admission results.

For a confirmed active-Turn delivery, the grain atomically records `followup.admitted` then `turn.input.added`. For a confirmed idle delivery, it atomically records `followup.admitted` then `turn.started`. A confirmed non-acceptance records `followup.rejected` with no input fact. If transport or Runtime behavior leaves acceptance unknown, the Runner reports `followup.delivery.unconfirmed`; the Server preserves the operation and never retries the Runtime call automatically. Reconciliation uses the original operation identity to append the one valid final admission result.

The current design's `session.followup_completed`/`session.followup_failed` terminal settlement is rejected because it incorrectly names admission as execution completion and cannot distinguish rejection from a potentially applied side effect. Treating every error as rejected is also rejected because it permits duplicate prompts after an ambiguous delivery.

### 5. Keep Runtime adapters as fact producers, not state deciders

OpenCode and Pi reporters will receive a Turn context and emit the same initial, runtime, and terminal facts. Workflow and AgentJob adapters will report their independent work outcome through their existing owner channel in parallel with Session facts. The AgentJob-specific `AppendTerminalCloseAsync` path will be removed in favor of idempotent `turn.finished` delivery carrying its existing durable delivery identity.

Runner outbox records will remain ordered per Session target and preserve their acknowledgement policy, but all non-streaming records will carry canonical Turn/operation identities. A pre-execution initial Turn fact is persisted before Runtime invocation. Runtime-produced facts, including `turn.finished`, are persisted and retried without causing another Runtime invocation. Follow-up admission uncertainty is represented by a fact delivery/reconciliation record, not by rerunning `runtime.followup`.

Sharing one reporter/protocol vocabulary is preferred over an OpenCode/Pi translation layer because the runtimes already converge at the Runner outbox. A generic terminal Session compatibility adapter is rejected because the change requires no legacy writes or consumers after cutover.

### 6. Migrate as a versioned, one-way protocol cutover

The Server EF migration will add the durable Turn identity and fields needed by the new Session snapshot. A versioned startup migrator will then transform persisted Sessions transactionally before Session API/Runner admission begins. For each legacy transcript turn, it will derive a deterministic `turnId` from the stable Session and persisted row identity, transform its initial input into `turn.started`, attach that ID to retained Turn-scoped facts, and transform a legacy close into `turn.finished` with the mapped `completed`, `failed`, or `stopped` outcome. A legacy row with no terminal evidence is projected as `unknown`, never silently finished.

The Server transcript migration groups legacy facts by Session and Follow-up `operationId`. A persisted legacy Follow-up input always receives `turnId = legacy-followup:{operationId}`. Because the old transcript creates a distinct input row for that fact, the migrator atomically writes `followup.admitted` followed by `turn.started` for the same ID and marks it `new-turn`; a repeated Runner delivery is then idempotent. Work-owned input instead uses `turnId = legacy-work:{workId}`. A legacy close joins the Turn selected by its ordered input and becomes that Turn's finish. A row with no terminal evidence remains unknown.

The Runner cannot inspect Server transcript persistence, so its outbox converter never classifies a legacy Follow-up group as admitted. Instead, it groups v1 records by target and `operationId`, preserves their sequence order, and replaces each group with one durable `migration.followup.resolve` control record at the group's first sequence position. The record contains the group record IDs, target, runtime binding, original operation ID when present (or a deterministic migrated ID), and deterministic `turnId = legacy-followup:{operationId}`. It invokes no Runtime operation.

The Server accepts that control record only during the version-2 migration window. Its resolver checks the already migrated transcript by operation ID. If the Server migration found the legacy input, the resolver returns the existing idempotent admitted/new-Turn pair. Otherwise, it atomically records `followup.delivery.unconfirmed` with the supplied operation and Turn identity and `attemptedPlacement: new-turn`. A legacy failure is therefore never converted directly to rejected. The resolver receipt settles the one replacement record and consumes all of its old group records. Work-owned input becomes `turn.started` using `legacy-work:{workId}` and its close becomes a finish. The old snapshot is replaced only after the complete converted group is durable, so a crash reruns an idempotent conversion and cannot leave an admitted Follow-up without its required Turn fact.

The cutover uses `sessionProtocolVersion: 2` as a required field in `RunnerRegistration`, the Server register/heartbeat requests, persisted `RunnerInfo`, and the RunnerHub connection query. The Server accepts registration, heartbeat, runtime-event upload, poll, and Hub tracking only from a runner at version 2; a mismatch returns `409 runner_session_protocol_incompatible`, removes any tracked connection, and does not mark the runner available. The Runner treats that response as a permanent incompatibility: it does not start the outbox drain, poll, or Session-command handlers until it has been upgraded and registration succeeds. The Hub independently checks the query version before adding the connection to `RunnerConnectionTracker`, preventing an old binary from receiving commands through an otherwise valid connection.

After conversion, parsing accepts only the new snapshot version and Server protocol endpoints reject legacy event names. This avoids two terminal interpretations for one Session. Lazy conversion on read or accepting an older runner is rejected because either approach would require producers and consumers to understand both semantics and would let a runner retry race with a query-dependent conversion.

## Risks / Trade-offs

- [A partial Server/Runner deployment sends legacy facts to a new Server] -> Require `sessionProtocolVersion: 2` on registration, heartbeat, runtime-event upload, poll, and Hub tracking; mismatch is a permanent `409 runner_session_protocol_incompatible` that leaves the runner unavailable.
- [A legacy in-flight execution lacks a trustworthy terminal result] -> Convert it to `unknown`, retain its binding and candidate Turn, and require Runtime reconciliation or explicit recovery instead of inventing a terminal outcome.
- [Outbox conversion mistakes cause repeated Runtime input] -> Convert ambiguous Follow-up records to delivery-unconfirmed, preserve the original operation identity, and prohibit automatic runtime resend.
- [Transcript transformation corrupts history or creates duplicate finishes] -> Use deterministic IDs, transactionally migrate each Session, validate exactly-one start/at-most-one finish before commit, and retain database/outbox backups until verification completes.
- [Broader DTO changes break Web or API clients] -> Update Server DTOs, canonical event types, live handlers, and read projections together; contract/spec tests cover Session detail, lists, transcript, and command eligibility.
- [The new aggregate snapshot grows with history] -> Persist only current Turn, latest Turn, and unresolved operation fences in Session state; reconstruct historical detail from Turn-keyed transcript rows.

## Migration Plan

1. Ship the Server schema migration, transcript converter, Session state converter, and `sessionProtocolVersion: 2` gate in a release that does not admit mismatched Runner execution delivery.
2. Stop or drain Runner delivery, back up the Server database and each Runner outbox snapshot, then run Server migration. Verify every converted Turn has one start, at most one finish, and no legacy terminal part remains.
3. Upgrade each Runner. Its outbox loader converts and durably writes its snapshot before the Runner registers or invokes a Runtime. Any conversion ambiguity becomes delivery-unconfirmed.
4. Enable new Runner registration and Session traffic only after REST registration/heartbeat and the RunnerHub query all report version 2. New producers and consumers use only `turn.*` and `followup.*`; legacy names are rejected.
5. Run focused migration, Session grain, Runner outbox, API, and Web projection tests, then verify representative Workflow, AgentJob, active Follow-up, idle Follow-up, Cancel, missing-binding, and unknown-state flows.

Rollback is not an in-place binary downgrade: once Turn facts are accepted, legacy code cannot interpret them safely. On failure before cutover, keep Runners stopped, restore the backed-up Server database and Runner snapshots, and restart the prior matched Server/Runner release. After cutover, forward-fix with the new protocol unless restoring those durable backups is explicitly approved.

## Open Questions

- The deterministic ID encoding for migrated and work-owned Turns must use the repository's canonical ID utilities while retaining the stable `legacy-followup:{operationId}` and `legacy-work:{workId}` derivation rule; implementation must not introduce a second externally visible identity format.
