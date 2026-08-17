# Self-Review: issue-620

## Review Mode

This is a re-review. I read the live issue with `mo issue view 620 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the artifacts. The live record returns the title `Slack 签名动作按钮：失败 Retry 与多 Bot 交互选择`, is in plan/in-progress state, and has an empty body; no additional acceptance text was returned. The prior review's issue interpretation was a Retry action with click-time authorization and visible rejection, interactive single-Bot selection for ambiguous messages, and no duplicate execution on redelivery.

The previous review reported MF-001 through MF-006. I verified each disposition against the current `proposal.md`, `design.md`, `tasks.json`, and both capability specs, then checked regressions and the current Slack route, lease, access-decider, session, outbox, and adapter boundaries.

## Must-Fix Findings

None. No must-fix problem remains relative to the stated issue scope and the capability acceptance criteria.

## Previous Finding Dispositions

- **MF-001: fixed.** `design.md` Decision 2, `proposal.md`, the Retry spec, and T-002 define `IAgentLauncher.LaunchConnectionRetryAsync`, the exact `slack-retry:{projectId}:{actionKey}` key, persisted deterministic root Session/input/turn identities, and reuse of those identities on recovery. The plan no longer reuses the original message coordinator for a root retry.
- **MF-002: fixed.** `design.md` Decision 7, both capability specs, and T-001/T-002/T-003 define the fixed-key `SlackActionRecoveryGrain`, persistent reminders, conditional recovery leases, pending-operation resume, and interaction replay behavior. The button operation is separate from the source-message provider-inbox fence.
- **MF-003: fixed.** `design.md` Decision 3, `proposal.md`, the Retry spec, and T-002 agree on the exact retryability allowlist. Missing, legacy, unknown, and non-allowlisted categories remain readable but text-only, with the required category-matrix tests.
- **MF-004: fixed.** `design.md` Decision 2, the Retry spec, and T-002 define atomic Retry-only force-new-turn admission, pre-minted input/turn identities, persistence of the follow-up operation ID, and operation-targeted dispatch instead of a first-queued-turn scan. The unrelated queued-follow-up case and recovery are explicitly testable.
- **MF-005: fixed.** `proposal.md`, `design.md`, the Retry spec, and T-001/T-002 require Retry authorization to re-evaluate the current receiving Connection policy and live Slack member/channel boundary through `SlackConnectionAccessDecider` and the validated `SlackLeaseContext`. Owner, allowlist, policy, and live-member changes are covered; owner-or-session-initiator control alone is not sufficient.
- **MF-006: fixed.** `design.md` Decision 1 and Decision 4, `proposal.md`, the selection spec, and T-001/T-003 now provide an implementable cross-Connection boundary. `SlackInteractionRoutes` keeps prompt-owner A's validated lease as `Receiving`; a Server-only `ResolveCurrentTarget` callback resolves and revalidates selected B's current runtime lease for the same authenticated operator and adapter identity; B is evaluated with a B-bound `SlackLeaseContext`. The plan forbids reusing A's lease, accepting a client-supplied B lease, or persisting credentials. It also defines no-lease/disabled/unbound outcomes, the valid B `allowlist`/`anyone` path, selected-target denial tests, and post-commit recovery without either click lease.

## Regression Check

The MF-006 fix is consistent with the current codebase. The existing route validates one connection-scoped lease, the adapter can hold separate runtime leases for multiple discovered Connections under one adapter identity, and `SlackConnectionAccessDecider` already consumes a target-specific lease-backed token resolver. The proposed Server-side resolver fills the missing boundary without adding target lease fields to the adapter envelope or moving authorization into `mohist-slack`.

No regression was found in the earlier fixes for retry-specific root identity, durable recovery liveness, the retryability matrix, threaded retry isolation, current Retry authorization, provider-inbox ordering, or preservation of signed Stop behavior. Selection recovery persists only the selected target, operation, and source snapshot, so it does not require a later click lease or silently choose another candidate.

## Dimension Checks

### Issue Goals and Acceptance Criteria

Checked, no must-fix issue. The Retry capability covers signed failed-result recovery, current actor/Connection authorization, fresh root or threaded attempts, explicit rejection states, and redelivery idempotency. The selection capability covers one interactive choice per persisted candidate, original-context routing, selected-target authorization, single-winner persistence, explicit outcomes, and readable fallback behavior.

### Coverage

Checked, no must-fix issue. T-001 covers the shared action and adapter boundary, T-002 covers Retry rendering and dispatch, and T-003 covers ambiguous prompt state and selection routing. The task acceptance criteria include the required success, rejection, stale, expiry, concurrency, recovery, root/thread, lease, and adapter contract cases.

### Correctness

Checked, no must-fix issue. Both operations validate authoritative state before effect, commit a stable operation before dispatch, reuse the same operation on replay/recovery, and preserve immutable failed history. Selection evaluates A and B separately before winner commit, while committed recovery cannot select a replacement candidate.

### Consistency With the Current Codebase

Checked, no must-fix issue. The plan extends the existing Server-signed action, `SlackConnectionAccessDecider`, runtime lease, Agent launcher/session, provider inbox, Slack outbox, status projection, and stateless adapter boundaries. The selected-target resolver is a new Server method on the existing lease service rather than a parallel authorization mechanism.

### Task Breakdown, Ordering, and Verifiability

Checked, no must-fix issue. T-001 -> T-002 -> T-003 is coherent: the shared interaction contract precedes Retry and selection, T-002 owns the shared recovery worker, and T-003 integrates selection with it. Each task has concrete outputs and focused tests, including failure injection at the durable commit and dispatch boundaries.

## Observations

- Retry's presentation identity still needs a concrete implementation contract. The signed Retry payload is described in terms of the original source message, while the control may be rendered in a replaceable status message whose provider identity is learned by the outbox after delivery. The implementation should bind or validate both the source and presentation identities so a valid status-button click is not rejected as a wrong-message action.
- An ambiguous prompt is claimed before a Session/input exists, while the current attachment binder associates accepted files with a Session/input owner. The plan requires bounded accepted attachment descriptors in the prompt snapshot but leaves their pre-selection ownership and later binding to implementation.
- Retry and selection lifetime, the Block Kit candidate-count fallback threshold, and retention of expired prompt/operation rows remain open questions. The plan already requires expiry, readable fallback, and durable recovery, so these are operational decisions rather than blockers for the stated flow.
- The Retry category policy is narrower than an unqualified reading of "failed" might suggest. The plan makes the exact allowlist authoritative and applies it consistently across proposal, design, specs, tasks, and tests; expanding it should be a separate product decision.

<promise>PASS</promise>
