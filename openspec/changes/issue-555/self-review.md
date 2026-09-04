# Issue 555 Plan Self-Review

Review type: re-review. The issue record was read with `mo issue view 555 --project proj_f6c141d63b6243bfbb481737b2243b87`; it is P1, in `plan`, `in_progress`, and its CLI body is empty. The proposal and four capability specs remain the detailed contract used below. No implementation files were changed.

## Findings

### Must-Fix

None. The five must-fix findings from the first review are resolved:

1. **`operator_all` authorization and issuer validation: fixed.** `design.md:105-121`, `specs/external-agent-authentication/spec.md:13-31`, and `tasks.json:11-16` define a deployment-owned current-private Project catalog, request-time `operator_all` evaluation, immediate denial after removal or ownership/visibility change, explicit-binding validation before persistence, and corresponding tests.
2. **Durable projection source contract: fixed.** `design.md:197-227`, `specs/external-agent-execution/spec.md:45-71`, and `tasks.json:52-61` define typed source facts, lifecycle-to-event mappings, same-transaction production, unresolved-state handling, durable context boundaries, and the prohibition on inferring ordered history from snapshots or raw internal events.
3. **Replay page envelope and event allowlist: fixed.** `specs/external-agent-session-replay/spec.md:1-3` and `tasks.json:97-106` lock the public page envelope, the seven execution event types, the separately constrained context-reset event, and tests for forbidden internal event types.
4. **Sequence continuity across generations: fixed.** `design.md:241-249`, `specs/external-agent-session-replay/spec.md:37-51`, and `tasks.json:104-106` require generation invalidation without resetting `nextSequence`, preserve `(SessionId, sequence)` identity, and test projector rebuild continuity.
5. **Historical Session eligibility and rollout behavior: fixed.** `design.md:221-227,292,318-324`, `specs/external-agent-session-replay/spec.md:81-94`, and `tasks.json:141-148` explicitly exclude pre-contract historical Sessions from first-release backfill/exposure and require safe `503 projection_lag` for known incomplete history.

## Dimension Checks

- **Issue goals and acceptance criteria:** PASS. The issue body is empty, so the proposal/spec contract is authoritative; every stated capability and acceptance surface is represented and now has a concrete policy.
- **Coverage:** PASS. Authentication, grants, idempotency, execution projection, replay, stop fencing, rollout, documentation, migrations, and tests are covered by the task graph.
- **Correctness:** PASS. The resolved policies remove the previous ambiguity without weakening authorization ordering, public privacy, durable replay, terminal fencing, or unknown handling.
- **Codebase consistency:** PASS. The plan still preserves the existing `IAgentLauncher`/grain authorities, keeps the public projection separate from `AgentSessionEvents`, uses additive persistence, and follows the fake-dependency and injectable-time constraints.
- **Task breakdown, ordering, and verifiability:** PASS. The dependency graph is acyclic; T-003 can proceed independently of PAT issuance, route work waits for projection/auth/idempotency foundations, replay and stop depend on their required foundations, and T-007 remains the rollout/documentation gate.

## Observations

- Retention duration, maximum journal size, cursor verification-key storage/rotation, and public output/error limits remain open in `design.md:335-342`. T-007 is correctly `HITL` and keeps the feature disabled until those rollout policies are recorded and tested.
- Existing user documentation still declares the External Agent API `wip-not-implemented` and references issue `#387`; T-007 explicitly owns removing that stale status/link language when the contract ships.
- Plan validation passed with `npm run docs:check`, `jq empty openspec/changes/issue-555/tasks.json`, and `git diff --check`. No implementation tests were needed for this artifact re-review.

<promise>PASS</promise>
