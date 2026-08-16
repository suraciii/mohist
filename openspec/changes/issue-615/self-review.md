# Self Review

Review round: first review. No prior `self-review.md` existed.

The live issue was read before the artifacts. Its goals are to bound protected runtime-event pressure with an explicit overload result, preserve exact fail-closed `session.input` and terminal activity records for replay, keep unrelated delivery sequences live, prove any tool/usage compaction against Server transcript semantics, and expose aggregated diagnostics. The issue also excludes live outbox cleanup/restart, cap-only changes, Workflow ownership changes, and terminal-result arbitration changes.

## Verdict

FAIL

## Must-Fix Findings

### M1. Server replay idempotency has no planned domain or persistence owner

The design requires Server-side idempotency for a replayed durable record ID (`design.md:67`), and T-002 makes replay idempotency an acceptance criterion (`tasks.json:28-36`). However, T-002 and the migration step only name DTOs, `ServerConnection`, the delivery adapter, endpoint/contract tests, and response normalization (`tasks.json:28-41`, `design.md:105`). They do not assign the required AgentSession grain/domain work: carrying a record ID into the grain command, persistently remembering accepted record IDs and their receipts, atomically deduplicating a batch, and returning the same receipt on replay across all workflow, generic, session, and cleanup routes.

The current code demonstrates why this is a correctness boundary, not merely a DTO change. The request routes currently project events into `AgentSessionRuntimeEventInput`, which contains only type and payload (`packages/server/src/Mohist.Server/Sessions/Grains/IAgentSessionGrain.cs:365-367`; `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:470-476,561-565,699-702`). `AppendEventsAsync` then creates a fresh server envelope for every delivery (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:2089-2090,2227-2290`). A retry of a protected terminal/activity fact can therefore append a second transcript/domain event instead of returning the original acceptance.

This violates the issue's fail-closed exact-replay goal for `session.input` and terminal activity, and can violate acceptance criterion 4 by changing the Server-visible transcript on a replay. Add an explicit Server grain/domain task and contract: define the durable deduplication key and retained receipt, define duplicate and mixed-batch behavior, update every route and command, and require an endpoint/grain regression that posts the same record twice and proves one transcript/domain application and the same receipt.

### M2. AgentJob input admission is not placed before runtime invocation

The design claims that a rejected initial input must be observed before the runtime turn is invoked (`design.md:77-81`), and T-004 repeats that acceptance (`tasks.json:71-78`). The current OpenCode AgentJob path cannot provide that behavior through the described sink rewrite. It creates the sink at `agent-job-turn.ts:135`, publishes `session.input` from the asynchronous `onSessionReady` callback at `agent-job-turn.ts:147-152`, and invokes `runtime.runTurn` at `agent-job-turn.ts:164`. Thus a protected-capacity rejection from that publication occurs after `runTurn` has already been called. The same sink currently performs direct HTTP input and observer reporting at `agent-job-turn.ts:416-524`.

The plan does not identify a pre-run admission phase or explain how it obtains the physical runtime session identity when OpenCode currently supplies that identity through `onSessionReady`. Simply routing `publishSessionInput` through the outbox will preserve the ordering bug: capacity rejection can still start a runtime turn without an admitted input, contrary to the issue's fail-closed `session.input` goal and the explicit bounded-overload behavior required by acceptance criterion 1. It also leaves the required outbox dependency absent from the current `AgentJobTurnDeps` boundary (`agent-job-turn.ts:30-35`).

T-004 must specify and own the required restructuring for both OpenCode and Pi: how the session is created/attached or how an input reservation is made before `runTurn`, how the durable record ID and physical binding are obtained, how a capacity result is returned, and the regression proving `runTurn` is never called after protected input rejection. Observer failures after runtime start must remain separate from this pre-run admission result.

### M3. The required compaction invariant is not concrete enough to preserve current Server semantics

The issue requires any tool-call or usage coalescing to be backed by an explicit domain invariant and to preserve the Server-visible transcript. The design only says that a reducer must preserve token, cost, lifecycle, and count effects (`design.md:34-45`), while the spec repeats the desired equivalence without defining a supported payload shape or reducer (`runtime-event-outbox-retention/spec.md:44-62`). The open question explicitly defers which current payloads are reducible (`design.md:116`), yet T-001 already requires implementation of deterministic reducers and safe-compaction tests (`tasks.json:8-16`).

The current Server behavior makes an unspecified reducer unsafe. `AgentSession.ApplyUsage` adds each submitted usage field to the accumulated totals (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:158-185`), and `TranscriptAccumulator` emits a transcript part for each usage/tool event (`packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs:14-30,60-96,265-274`). Replacing usage updates with the latest payload, or replacing tool updates without defining lifecycle and transcript-part equivalence, changes accounting or transcript output. A safe implementation must either define exact supported shapes and reducer equations, including record identity and Server handling of the compacted representation, or explicitly keep all currently unsupported tool/usage/binding facts protected and reject under pressure.

Without that decision, an implementation can satisfy the task wording while violating acceptance criterion 4. Make the invariant authoritative in the design/spec and assign the corresponding Runner and Server tests; do not leave the central compaction choice as an open question.

### M4. The strict receipt wire contract is left unresolved at the point where implementation must begin

T-002 requires every request and acceptance to carry the durable record ID, event type, logical target, physical runtime identity, and applicable AgentSession/turn identity (`tasks.json:28-36`). The design nevertheless leaves the receipt field name and atomically deployable endpoint versions open (`design.md:115`), alongside the choice of public admission-result shape (`design.md:117`). This is not a cosmetic naming decision: the current workflow, generic, session, and cleanup endpoints have different request/response paths (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:370-476,529-565,686-702`), and the current receipt DTO has no durable record ID (`RunnerRoutes.cs:1090-1104`; `packages/runner/src/server/connection.ts:822-840`).

An unresolved wire contract prevents the Runner matcher, Server grain deduplication, batch positional receipts, and compatibility/rollback boundary from being implemented as one behavior. It directly threatens acceptance criteria 2 and 3, which depend on exact identity matching and replaying the original protected record. Resolve the field names, per-endpoint request/receipt shape, batch semantics, compatibility boundary, and admission-result contract in the plan before build work starts. The Server persistence change in M1 must use that selected contract.

## Dimension Checks

- Issue goals and acceptance criteria: checked first against the live issue; no issue-level goal was omitted from the review basis.
- Coverage: FAIL. The broad capabilities cover bounded retention, receipts, liveness, producers, and diagnostics, but M1-M4 leave required Server replay, pre-run producer admission, and transcript-safe compaction/receipt contracts incomplete.
- Correctness: FAIL. The stated behavior is not achievable for the current AgentJob OpenCode callback boundary without a pre-run design, and receipt replay is not correct without Server-side deduplication.
- Consistency with the current codebase: FAIL. The current Server commands discard the durable local record ID, the grain appends fresh envelopes on repeat delivery, and AgentJob still publishes directly from inside runtime startup; the plan does not assign all corresponding ownership changes.
- Task ordering: checked, no issue. T-001 through T-004 have a sensible dependency order, with receipts before lease settlement and producer integration after the shared primitive.
- Task completeness and verifiability: FAIL. The task acceptance lists tests for the missing behaviors, but no task owns the grain persistence/idempotency implementation or the OpenCode pre-run identity/admission restructuring, and the central reducer and wire decisions remain open.
- Non-goals and operational constraints: checked, no issue. The plan does not propose live outbox cleanup, a Runner restart, a cap-only increase, Workflow ownership changes, or terminal-result arbitration changes.

## Observations

- The deployment override surface for the 5,000-record default remains open (`design.md:118`). The issue does not mandate a particular configuration channel, but T-001/T-004 should select one before implementation and test that the Runner actually passes it to the outbox.
- The literal record count can remain above 5,000 while a legacy snapshot contains protected records. The plan correctly chooses exact retention plus explicit pressure rather than truncation; it should document that this is an exceptional pressure state and distinguish it from normal capacity in health/metrics.
- The issue comments identify PR #624 as a separate stalled-send liveness fix. The plan should state whether T-003 consumes that change, supersedes it, or is intentionally implemented together so the delivery lease work is not duplicated or accidentally split across incompatible branches.
- Existing terminal paths use `successful-response`, including follow-up and binding-reconciliation activity. T-002/T-004 appear to cover the policy change, but the implementation task should enumerate every protected terminal producer and assert that none retains the shortcut.
- No tests were run because this was a first-round artifact review and the requested change is limited to this review file.

<promise>FAIL</promise>
