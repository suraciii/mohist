# Self-Review (round 2) — Issue 514 (Slack Agent Connection, Owner-only DM)

Reviewer re-reviewed `proposal.md`, `design.md`, `tasks.json`, and all five `specs/*/spec.md`
after the round-1 fixes.

## Round-1 status

All five round-1 problems are resolved:

- **P1 (lease endpoint)** — D3/D5 now name the combined `POST .../adapter-session`
  (lease + heartbeat); T-006 owns it; T-009 calls it. Resolved.
- **P2 (ingress→dispatch handoff)** — D5 defines `/ingress` as the single classifying
  inbound route; the separate `/dispatch` adapter route is gone from D4, D5, D8, the
  migration plan, and both task outputs. Resolved.
- **P3 (coordinator routing)** — D4 now states `LaunchConnectionAsync` routes through
  `AgentLaunchCoordinatorGrain` like `LaunchIdempotentAsync`; the misleading "mirroring
  LaunchMentionAsync" wording is corrected. Resolved.
- **P4 (adapter liveness)** — D5 specifies heartbeat freshness against an injectable
  `TimeProvider` driving the `WaitingForSlackService` transition; T-003/T-009 carry it; a
  spec scenario covers advancing past Waiting. Resolved.
- **P5 (untestable scenario)** — `slack-dm-dispatch` req 5 scenario 2 now states concrete
  behavior (a follow-up DM dispatches its own independent launch). Resolved.

Tasks.json still validates (10 tasks, DAG, priorities, criteria, `passes:false`). All specs
retain correct 4-hashtag scenario structure.

## Must-fix problem found this round

### P6. The ingress classification shadows the owner-claim path — claim would never succeed

The owner claim is delivered as a DM whose text is the one-time claim code, and it is
processed by the `/ingress` classification (D5) at the **Claim owner** Setup step — which is
by definition **not yet Complete**. The current specification rejects that DM before it can
be recognized as a claim, in three mutually reinforcing places:

1. **`design.md` D5 (line 85) classification ordering** evaluates
   `(i) if the Connection's Setup is not Complete → reject` **before**
   `(ii) else if the DM text matches a pending owner-claim code → process the claim`.
   A claim code only exists once Setup has reached Claim owner (Setup ≠ Complete), so the
   claim DM always hits branch (i) and is rejected; branch (ii) is unreachable. The owner
   claim — issue acceptance criterion 4 — would not work as specified.

2. **`slack-dm-dispatch/spec.md` requirement 1, scenario "A DM dispatched before Setup is
   complete is rejected"** (lines 13-15) says any DM at a non-Complete Connection is
   rejected, with "before an Owner has been claimed" given as the example. This directly
   conflicts with `slack-connection-setup/spec.md` requirement 6, scenario "A claim-code DM
   is treated as a claim, not a task": at the Claim owner step the same DM is simultaneously
   "a DM at a Connection whose Setup is not yet Complete" (→ reject) and "a DM whose text
   matches a pending claim code" (→ process). The two specs contradict each other for the
   claim case.

3. **`tasks.json` T-006 acceptance** lists the three outcomes in parallel ("a DM dispatched
   before Setup is Complete is rejected …; a non-owner DM is rejected …; a DM whose text
   matches a pending claim code is processed as a claim") without stating precedence, so it
   does not disambiguate the conflict.

**Fix (all three locations):** establish that claim-code matching takes precedence over the
Setup-not-Complete gate — a pending claim code can only exist once Setup has reached Claim
owner, so checking it first is safe. Concretely:

- D5: reorder so claim-code matching is evaluated before the Setup-Complete rejection
  (e.g. `(i) DM text matches a pending, unused claim code → process claim (D7);
  (ii) else if Setup not Complete → reject; (iii) else if sender not Owner → reject;
  (iv) else dispatch`).
- `slack-dm-dispatch` req 1 scenario: tighten the WHEN so it does not swallow the claim case
  — e.g. "a DM that is not a valid pending claim code arrives at a Connection whose Setup is
  not yet Complete" (or scope the rejection to "before Setup has reached Claim owner").
- T-006 acceptance: state the precedence explicitly (claim-code match is evaluated before
  the Setup-not-Complete rejection).

This is a genuine logic defect in the central claim flow, not a wording nit; it must be
fixed before building.

## Minor note (not blocking)

- **Risk-section wording drift (`design.md` line 143).** The Risk bullet still says a
  second DM during a running task "is governed by the later issue, and this issue makes no
  continuation commitment", but P5 tightened `slack-dm-dispatch` req 5 to commit to concrete
  behavior (the follow-up DM dispatches its own independent launch). The independent-launch
  behavior is now specified here, not deferred; only continuous-conversation / current
  Session / New task remain deferred. Align the Risk bullet with the tightened spec.

## Verdict

Round-1 issues are cleanly resolved and no new structural problems were introduced, but the
ingress-classification ordering (P6) silently breaks the owner-claim flow and is
self-contradictory across the design and two specs. It must be fixed before the plan is
built.

<promise>FAIL</promise>
