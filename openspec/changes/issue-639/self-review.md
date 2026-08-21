# Self-Review: issue-639

## Verdict

FAIL. The plan has must-fix gaps relative to issue 639's runtime-event convergence and fail-closed attribution criteria.

## Must-Fix Findings

### MF-1 — The planned Server relaxation does not enforce the pure-activity boundary for every Workflow-introduced session

**Violates:** issue acceptance criterion: “`AppendRuntimeEventsAsync` still rejects non-activity events without turn binding on workflow-introduced sessions,” and the capability requirement `The relaxed path is limited to pure activity batches`.

The current grain gate in `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1698-1705` only detects `hasWorkflowTurnForRuntime` and rejects an unattributed request when a persisted Workflow turn has the same runtime-session id. The design adopts that boundary (`design.md:40-46`) and says the old rejection remains “when a Workflow turn exists for that runtime,” but it does not require checking that the AgentSession is Workflow-introduced (`SourceKind == workflow`) and rejecting unattributed non-activity batches even when that particular runtime has no persisted turn.

That leaves a concrete failure case: a Workflow-labeled session with no turn for the reported/current runtime can submit an unattributed `message.delta` (or a mixed activity/non-activity batch) through the session-scoped route. `RunnerRoutes.cs:548-575` currently permits the request to reach the grain without identity, and the proposed grain condition as described would not reject it. The spec and T-001 acceptance criteria require the batch to be rejected/ignored without append, not merely when a matching Workflow turn happens to exist. T-001 must define and test the authoritative Workflow-session classification and the exact pre-append rejection for all unattributed non-activity or mixed batches, while continuing to permit pure current-binding `session.activity`.

### MF-2 — The plan cannot reach the required empty-receipt settlement for Workflow cleanup records through the current delivery protocol

**Violates:** proposal goal (`proposal.md:10`) and the convergence requirement `Confirmed-consumed matching records settle without fabricating identity`, including its `session.cleanup` scope.

The current cleanup path does not return a receipt array that can be empty. `runtime-event-delivery.ts:16-61` sends a `workflow-cleanup` record through `ServerConnection.workflowAgentSessionCleanupTurn` and always synthesizes a positive `session.cleanup` receipt from the returned acceptance object. The Server route in `packages/server/src/Mohist.Server/Api/RunnerRoutes.WorkflowCleanup.cs:12-61` also always returns `WorkflowAgentSessionCleanupTurnResponse`, including on an idempotent replay; it has no valid 2xx `[]` outcome. In contrast, the ordinary Workflow runtime-events route can return an empty array (`RunnerRoutes.cs:397-416`), which is why the existing `session.input` path can observe an empty receipt.

The design and T-003 (`design.md:66-76`, `tasks.json:49-68`) specify a two-empty counter in the outbox but do not specify any change to the cleanup endpoint, `workflowAgentSessionCleanupTurn`, or the delivery adapter that would make an empty 2xx receipt array observable for `session.cleanup`. As written, the new rule can only operate for `session.input`; cleanup records will either receive the synthesized positive receipt or fail, so the plan does not actually cover the stale cleanup backlog it explicitly includes. T-003 must select and describe the cleanup replay/empty-response contract and cover it end-to-end through the real adapter, while preserving the positive cleanup identity checks.

### MF-3 — The terminal 4xx classifier is left as an unresolved product decision

**Violates:** proposal goal (`proposal.md:9`) and the outbox requirement that deterministic 4xx refusals settle while retryable failures, including transient client failures, remain durable.

The design says to classify “stable 4xx conflicts and validation failures” and to keep 408/429 retryable (`design.md:48-54`), but it never defines the concrete status/code predicate. It then leaves “the exact refusal threshold and the final allowlist/metadata contract” open (`design.md:113-116`). T-002 requires an allowlist and tests it, but a builder cannot determine from the plan whether, for example, top-level `code: conflict`, `workflow_runtime_binding_rejected`, `agent_session_changed`, `validation`, authentication failures, or other 4xx responses are terminal. This is load-bearing: an overly broad predicate violates the issue's retain-and-retry behavior, while an overly narrow one leaves the observed 409 backlog retrying forever. The plan must name the structured status/code combinations, explicitly include the observed runtime-event 409 refusal, exclude transient statuses, and commit to the threshold used by T-002 rather than leaving that choice in Open Questions.

## Review Dimensions

### Issue basis — checked, no issue

I re-read the complete current Mohist issue with `mo issue view 639 --project proj_f6c141d63b6243bfbb481737b2243b87` before interpreting the artifacts. The review basis is the current-binding activity-only acceptance, preservation of Workflow attribution fences, bounded deterministic-refusal settlement, two-empty already-consumed settlement, warn-once retention behavior, and live Workflow receipt/stage-transition liveness.

This is the first review of this plan; no prior `self-review.md` exists under `openspec/changes/issue-639/`, so I performed the full sweep rather than disposition verification.

### Coverage — FAIL

The artifacts cover the named high-level goals and link all four tasks to the two capability specs. However, MF-1 leaves a required unattributed-event boundary uncovered for Workflow sessions without a matching persisted turn, MF-2 leaves the explicitly included cleanup-record empty-response path unreachable through the current adapter, and MF-3 leaves the terminal-vs-retry classification incomplete. These are not optional hardening items; each affects an issue goal or acceptance criterion.

### Correctness — FAIL

The outbox design correctly notices the existing atomicity problem and requires snapshot persistence before waiter settlement (`design.md:72-74`), and its fair-group approach is compatible with the current scheduler. However, the Server relaxation as described can admit an unattributed non-activity event in the failure case above, and the cleanup double-empty rule has no response path in the current implementation. The unresolved 4xx predicate also prevents proving that the proposed terminal behavior is safe and convergent.

### Consistency with the current codebase — FAIL

The proposed target files and current scheduler are real, and the plan generally follows existing snapshot, timer, and typed-error patterns. The review found the important protocol mismatch at the cleanup boundary: the current route returns an acceptance object, while the plan's terminal algorithm assumes an empty receipt array. The plan also relies on a grain condition based on runtime-bound turns without accounting for the session metadata that identifies Workflow-introduced sessions.

### Task breakdown — FAIL

The task graph is ordered and acyclic, and T-003/T-004 have useful persistence, timer, recovery, and fairness acceptance criteria. But the task breakdown does not assign the missing cleanup protocol work to any task, does not state the required Workflow-session classification/rejection rule in the design at the same precision as the spec, and leaves the central 4xx allowlist/threshold decision unresolved while T-002 is supposed to be directly verifiable. The plan is therefore not ready to build as written.

## Observations

- `design.md:99` correctly acknowledges that full-snapshot serialization remains a cost for non-delta settlement. This is a plausible residual operational risk, but the issue explicitly keeps snapshot-format redesign out of scope, so it is not a must-fix.
- The plan says an already-consumed outcome should make the Workflow caller “stop or escalate” (`design.md:72`, `design.md:98`) while leaving the precise higher-level action open (`design.md:116`). The acceptance criteria do require fail-closed behavior and no fabricated identity, which is the must-have boundary; the exact recovery UX is recorded as an observation unless it changes whether the current turn is allowed to continue.
- The design's rollout ordering (Server before Runner, `design.md:103-111`) is sensible and should be retained when the missing response/classification decisions are resolved.

<promise>FAIL</promise>
