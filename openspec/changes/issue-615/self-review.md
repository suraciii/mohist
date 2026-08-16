# Self Review

Review round: re-review. The prior review was the FAIL in `self-review.md` from the first round. The live issue was read first with `mo issue view 615 --project proj_f6c141d63b6243bfbb481737b2243b87`; its acceptance criteria remain the review basis.

## Verdict

FAIL

## Must-Fix Findings

### M1. The acceptance ledger does not define the binding-side-effect failure state

The revised design correctly assigns replay ownership to `AgentSessionGrain`, but its replay rule conflicts with the current Workflow input boundary. The design says that a matching ledger entry returns the stored receipt without calling the Workflow binding port again (`design.md:79`), and T-002 repeats that replay must not perform a second binding operation (`tasks.json:32`). The existing grain, however, can durably create the input and turn while `BindAgentExecutionAsync` returns false; the current route then returns no receipt, and a retry deliberately calls the binding port again. This behavior is covered by `packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionGrainInputBoundarySpecs.cs:158-188`.

If the new ledger is written before the binding succeeds, the first protected `session.input` retry returns the same non-positive or empty acceptance forever and never retries binding. If the ledger is written only after binding, the plan has not defined how a timeout or crash between the grain commit and the cross-grain binding call avoids either a lost Workflow binding or a duplicate binding operation. The same distributed window exists for route-level `DispatchNextAsync` after `AppendRuntimeEventsAsync` (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:477,703`).

This violates the issue goal of preserving an exact protected input for replay and can leave a sequence permanently blocked after a transient Server-side rejection; it also violates T-002's positive receipt and replay criteria. Define an explicit pending/non-accepted ledger state and recovery rule, or make the binding and follow-up side effects durably idempotent under the runtime-event ID. Add failure-injection tests for binding rejection, response loss after binding, and response loss before follow-up dispatch. This is a regression risk introduced while addressing prior finding M1; the earlier review verified that a Server ledger owner was missing but did not verify the external side-effect transaction.

### M2. The Server acceptance ledger is not tied to durable transcript persistence

The plan says a persistence failure returns no acceptance and that new records plus ledger entries are applied in one AgentSession state commit (`design.md:79`, `design.md:119`), but the current Server has two persistence boundaries. `AppendEventsAsync` applies rows to `TranscriptAccumulator` and schedules deferred persistence at `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:2258-2266`. Generic and Session runtime-event routes return immediately after `AppendRuntimeEventsAsync` (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:564,702`); only some Workflow observation and specialized input/terminal paths force `FlushAsync`.

A v2 receipt ledger stored in AgentSession state can therefore survive a crash after the state save but before the transcript-store flush. A retry would return the stored receipt and skip the event, leaving the Server-visible transcript missing. The reverse ordering can persist a transcript row before the ledger and cause a retry to apply it twice. This violates the issue's exact replay goal and acceptance criterion 2, and can violate criterion 4's requirement that compaction/replay preserve the Server-visible transcript result.

T-002 must own an atomic or idempotent boundary across acceptance-ledger state and transcript persistence: for example, persist the transcript before issuing the receipt and retain the runtime event ID through the transcript store for retry deduplication, or commit both through a transaction that has a recovery protocol. Add crash-ordering tests for generic, Session, Workflow, and cleanup deliveries. The current plan's TranscriptAccumulator equivalence test does not cover this acceptance/flush ordering.

### M3. AgentJob's coordinator-owned initial input path is unspecified

The new AgentJob requirement says every AgentJob with an AgentSession must enqueue `session.input` and await a positive receipt before `runTurn` (`design.md:91`, `tasks.json:73`, `runtime-event-outbox-retention/spec.md:90-96`). Current AgentJob execution intentionally skips that publication when the coordinator already supplied `work.initialInputId` and `work.initialTurnId` (`packages/runner/src/runtime/agent-job-turn.ts:142-153,270-273`); OpenCode's current `onSessionReady` callback then only attaches the session before `runTurn` (`agent-job-turn.ts:147-164`).

The plan describes the new pre-run physical-session phase but never says whether this existing launch input is already considered admitted, whether it must be represented by an outbox record using `initialInputId`, or how the Server acceptance ledger recognizes it without creating a second domain/transcript input. Preserving the current skip leaves this AgentJob outside the required positive input-receipt gate; removing it without a Server rule risks a duplicate launch input. Under protected pressure, the path can therefore invoke `runTurn` without the explicit bounded admission outcome required by the plan's AgentJob behavior and the issue's fail-closed input goal.

T-004 must define the initial-launch case for both OpenCode and Pi, including the durable record ID, Server behavior for the already-recorded input/turn, attach ordering, and zero-`runTurn` regressions for admission failure. This pre-existing gap was missed in the first review because its M2 check focused on the OpenCode `onSessionReady` ordering and did not cross-check the separate `initialInputId` skip branch.

### M4. Logical-sequence identity is contradictory across the design and task contract

Decision 2 defines the logical lane as producer family plus logical target and keeps physical delivery identity separate so an older `runtimeSessionId=A` fences a newer `runtimeSessionId=B` (`design.md:53-55`). T-003 instead says pending records are partitioned into a logical sequence containing the producer family, logical target, and required physical identity (`tasks.json:52`). The liveness spec also describes physical runtime identity as part of the sequence definition while requiring B not to overtake A (`runtime-event-delivery-liveness/spec.md:1,15-33`).

If the implementation uses the physical runtime session in the partition key, A and B become separate schedulable groups and B can progress while A is blocked, violating the issue's requirement that each sequence preserve FIFO and the spec's newer-binding scenario. If it excludes physical identity, the plan must explicitly define the per-record delivery-identity fence and batching rule. Resolve one canonical model in the design, spec, and T-003, then retain the old/new binding test as the proof. The first review marked task ordering as clear but did not compare the sequence-key definition against the later binding-fence requirement; this is a pre-existing contract inconsistency that meets the must-fix bar.

## Prior Finding Dispositions

- Prior M1, Server replay idempotency ownership: fixed in scope. The revised design assigns `AgentSessionGrain` durable ledger ownership, defines fingerprints and mixed-batch behavior, and T-002 assigns the grain, routes, and tests. The new M1 above is the remaining external-side-effect gap exposed by that fix.
- Prior M2, AgentJob admission after `runTurn`: substantially fixed for a newly created or resolved physical session. The design now owns pre-run OpenCode/Pi preparation and adds the shared outbox dependency. M3 above is the omitted coordinator-owned initial-input case.
- Prior M3, unspecified compaction semantics: fixed. The plan now protects all tool-call, usage, model, and binding facts and limits compaction to identity-complete adjacent text deltas with an explicit `compactedRawEventCount` transcript invariant.
- Prior M4, unresolved receipt wire contract: fixed. The plan resolves `runtimeEventId`, v2 envelopes, tagged targets, positional receipts, cleanup shape, admission-result shape, and the deployment boundary.

## Dimension Checks

- Issue goals and acceptance criteria: checked against the live issue before the artifacts; the five criteria and non-goals are the review basis.
- Coverage: FAIL. M1-M3 leave exact replay/admission behavior incomplete in current producer and Server failure paths; M4 leaves the required FIFO binding fence ambiguous.
- Correctness: FAIL. The stated ledger replay rule can strand a rejected Workflow input, and the stated ledger persistence boundary can acknowledge an event whose transcript was never durably flushed.
- Consistency with the current codebase: FAIL. Current grain tests require retrying a failed Workflow binding, current generic/Session routes use deferred transcript persistence, and AgentJob has a coordinator-owned input skip path that the plan does not account for.
- Task ordering: checked, no issue. T-001 through T-004 have a reasonable dependency order; M4 is a contract-definition problem within T-003, not an ordering problem.
- Task completeness and verifiability: FAIL. The listed tests do not cover binding-side-effect failure windows, ledger/transcript crash ordering, coordinator-owned AgentJob inputs, or a single authoritative sequence-key definition.
- Consistency with non-goals and operational constraints: checked, no issue. The plan does not propose live outbox cleanup, a Runner restart, a cap-only increase, Workflow ownership changes, or terminal-result arbitration changes.

## Observations

- The plan still does not state whether T-003 consumes, supersedes, or is deployed alongside the separate PR #624 stalled-send liveness fix called out in the issue comments. This is an integration/ownership clarification, not a must-fix issue-level gap.
- The acceptance ledger is described as lifetime-of-AgentSession state with no retention policy. That may add unbounded Server-side state for high-volume runtime events even though the Runner outbox is bounded; the issue does not specify a Server ledger retention limit, so this remains an observation.
- T-004 should enumerate every current `successful-response` producer, including follow-up and binding-convergence paths, and assert that protected records use positive receipts under the v2 contract. The design states this rule, but the task acceptance does not name every producer.
- No implementation tests were run because this round reviewed plan artifacts only and was restricted to writing this file.

<promise>FAIL</promise>