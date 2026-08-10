# Issue 555 Plan Self-Review

Review type: first full sweep. The issue record was read with `mo issue view 555 --project proj_f6c141d63b6243bfbb481737b2243b87`; it is P1, in `plan`, and its CLI body is empty. The proposal and four capability specs are therefore the detailed contract used below. No implementation or test was changed or run.

## Findings

### Must-Fix

1. **P1: `operator_all` authorization semantics and issuer validation are still unresolved.**

   `specs/external-agent-authentication/spec.md:13-27` requires `operator_all` to authorize each current private Project owned by the deployment, while rejecting readonly issuance. `design.md:286-288` leaves the important question open: whether access follows current ownership/visibility or is preserved after a Project changes. `tasks.json:11-16` validates the grant form and generic ordering, but does not define the issuer's Project validation rule, the runtime meaning of "current private," or the behavior after ownership/visibility changes. `tasks.json:141-146` does not resolve this either.

   Without this decision, two valid implementations can grant different Projects to the same PAT, and the security boundary cannot be verified. Resolve the `operator_all` and issuer-validation policy before T-001 is buildable, then add acceptance tests for current private Projects, non-private or non-owned Projects, and ownership/visibility changes.

2. **P1: The projector has no specified durable source contract for the required public lifecycle events.**

   The replay contract requires `input.accepted`, `input.rejected`, `turn.queued`, `turn.running`, `turn.outcome_pending`, `turn.terminal`, and `session.unknown` events (`specs/external-agent-session-replay/spec.md:1-3`). The design only says that a projector will consume canonical facts and outboxes (`design.md:177-197`) and explicitly leaves the required historical/source facts open (`design.md:281-283`). In the current code, `AgentSessionEvent` contains only runtime-bound, usage, model, and context metadata events (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSessionEvent.cs:3-9`), and the AgentSession/AgentJob event catalog has no input/turn lifecycle event types (`packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs:37-47,171-183`).

   `T-003` only says “defined vocabulary” and “canonical facts and outboxes” (`tasks.json:52-60`); it does not identify the source event inventory, add typed outbox facts, or define how state-only facts produce ordered transitions. A projector built from the currently identified durable events cannot deterministically produce the required stream or distinguish a missing transition from `unknown`. Add a source-fact/event mapping contract, the required canonical/outbox additions, and tests for every public event type, including durable context reset and unresolved state.

3. **P1: Session replay acceptance does not lock the required page envelope or event allowlist.**

   The contract requires every page to contain `sessionId`, `events`, `nextCursor`, and `highWaterSequence`, and limits public execution event types to the exact seven-value vocabulary (`specs/external-agent-session-replay/spec.md:1-3`). `T-005` verifies per-event fields and pagination behavior (`tasks.json:98-104`), but never requires the page envelope fields or enumerates/asserts the exact allowed event types. The task can therefore pass while returning a page with the wrong top-level shape or exposing an additional projected event.

   Add acceptance criteria and tests for the exact page schema, exact public event enum, and the absence of all other internal/transcript event types.

4. **P1: Sequence continuity across stream generations is not acceptance-locked.**

   The specification requires strictly increasing positive sequences that are never reused or renumbered across projector restart, crash recovery, outbox replay, or stream-generation changes (`specs/external-agent-session-replay/spec.md:37-47`). `T-003` covers crash/replay deduplication (`tasks.json:57`), and `T-005` rejects old-generation cursors (`tasks.json:100-102`), but neither task requires sequence continuity when a new generation is created. A rebuild could reset `nextSequence` and still satisfy the current task criteria, breaking stable `(SessionId, sequence)` deduplication and the reconnect contract.

   Specify the sequence/tombstone invariant across generation changes and add a projector-rebuild test proving that old sequences are never reused or renumbered while old-generation cursors still fail with `cursor_invalid`.

5. **P1: Backfill eligibility and behavior for unprojected historical Sessions are not defined before rollout.**

   The migration plan says to backfill retained Sessions where canonical facts are sufficient and later enable selected backfilled Sessions (`design.md:242-266`), but the same design leaves source sufficiency and first-release backfill scope unresolved (`design.md:281-283,291-292`). `T-007` only says to add readiness handling for "selected backfilled Sessions" and to record a policy (`tasks.json:139-146`); it does not define the eligibility predicate, the required source watermark, or whether an ineligible Session returns `404`, `503 projection_lag`, or a controlled non-projectable result.

   This is required to avoid fabricating event history or silently exposing an empty stream, contrary to `specs/external-agent-session-replay/spec.md:77-87`. Make the backfill/first-release policy an explicit prerequisite of rollout, define the route behavior for an unprojected Session, and test both eligible and ineligible historical Sessions before enabling the feature flag.

## Dimension Checks

- **Issue goals and acceptance criteria:** FAIL. The five must-fix findings leave the `operator_all` security boundary, public event production, replay wire contract, sequence identity, and historical-session behavior unverifiable.
- **Coverage:** FAIL. The proposal capabilities are represented by tasks, but the missing source-event and backfill contracts mean the session replay capability is not fully covered.
- **Correctness:** FAIL. The current durable event inventory does not establish a deterministic path to the required public lifecycle stream, and unresolved grant/backfill semantics permit materially different security and replay behavior.
- **Codebase consistency:** Checked, no additional independent must-fix issue. The plan correctly preserves the existing `IAgentLauncher`/grain authorities, introduces a separate `/api/v1` adapter, keeps the public projection separate from `AgentSessionEvents`, and follows the repository's fake-dependency and injectable-time rules. The current auth and event implementations reinforce the source-contract findings above.
- **Task breakdown, ordering, and verifiability:** FAIL. The dependency graph is acyclic and the vertical slices are reasonable, but T-003/T-005 can be implemented before the unresolved source and policy decisions are made, and their acceptance criteria allow an incomplete public stream to pass.

## Observations

- `design.md:277-290` also leaves retention duration, maximum journal size, cursor verification-key storage/rotation, and public output/error limits open. T-007 is correctly marked `HITL`, so these are rollout prerequisites rather than additional findings if the implementation remains disabled until the decisions are recorded and tested.
- The existing user documentation still declares the External Agent API `wip-not-implemented` and points to issue `#387` (`docs/agent-api.md:1-3,150-159`; `docs/auth.md:183-194`). T-007 intends to update these documents; the fix should remove stale status/link language when the contract is shipped.
- Test evidence was not collected because this is a read-only plan review: Total 0, Failed 0, Skipped 0, Not Run: all build and test suites. This is verification scope, not a product finding.

<promise>FAIL</promise>
