# Self-Review — Issue 514 (Slack Agent Connection, Owner-only DM)

Reviewer reviewed `proposal.md`, `design.md`, `tasks.json`, and all five `specs/*/spec.md`
against the issue body and the authoritative `docs/agent-connections.md` /
`design/slack-agent-connection.md` / `design/agent-api.md`.

## Overall

The plan is structurally sound: the five capabilities in the proposal map 1:1 to the
five spec directories; every spec requirement is covered by at least one task; the task
graph is a valid DAG with priorities strictly increasing along every dependency; all nine
issue acceptance criteria are addressed somewhere; and the non-goals are respected on both
sides. The architecture decisions stay inside the placement rules in
`design/architecture.md:46-54`.

However, there are several concrete under-specifications and one unowned deliverable in the
central DM and Setup flows that should be resolved before implementation begins. A separate
fix task should address each.

## Must-fix problems

### P1. The server-side token-lease endpoint is an unowned deliverable

`design.md` D3 (line 61) and task T-009 both state the adapter "leases tokens from the
Server at startup/reconnect via a narrow token-lease endpoint." No task owns the **server
side** of that endpoint. T-006's output lists only `ingress`/`dispatch`/`deliveries` routes;
T-003's output is the Slack verify client. Without the lease endpoint the adapter cannot
start, yet no acceptance criterion verifies it exists.

**Fix:** add the token-lease route (authenticated by the operator token, returns the
decrypted App/Bot tokens for a Connection) to T-006's output and acceptance criteria, or
fold it into T-003. The route must be named and its auth model stated.

### P2. The ingress → dispatch handoff and classification ownership are underspecified

`design.md` D5 (lines 83-85) lists `POST .../ingress` and `POST .../dispatch` as peer
adapter-facing routes, but never defines (a) when the adapter calls each, (b) whether one
triggers the other server-side, or (c) who classifies an inbound DM as owner-task vs
claim-code vs non-owner-rejection vs ignored. D4 (line 73) puts owner-only gating inside
the `/dispatch` route, which implies the adapter already knows a DM is an owner task before
calling `/dispatch` — but owner determination is Server authority
(`slack-agent-connection.md:47`), so the adapter cannot pre-classify. This is the central
flow of the issue and is ambiguous; an implementer could reasonably put classification in
the adapter (violating the boundary) or build a two-round-trip ping-pong.

**Fix:** state one model explicitly. Recommended: `/ingress` accepts every normalized
envelope, performs the provider-inbox dedup (D6), classifies **server-side** (Setup
complete? sender == Owner? text matches a pending claim code?), and internally either
rejects, claims, or calls `LaunchConnectionAsync` — returning the classification result to
the adapter in the same response. Then `/dispatch` is not a separate adapter-facing route
(or its precise role is redefined). Update `slack-dm-dispatch` and `slack-connection-setup`
scenarios to match.

### P3. Whether `LaunchConnectionAsync` routes through the coordinator is ambiguous

D4 (line 71) says the new method is "mirroring how `LaunchMentionAsync` encodes its origin"
while also stating "the coordinator plan gains an optional `ConnectionOrigin` field" — and
the Risks section asserts the `(team, conversation, ts)` key delivers redelivery
idempotency. Whether `LaunchConnectionAsync` actually routes through
`AgentLaunchCoordinatorGrain` (the sole source of the redelivery → same-SessionInput
guarantee delivered by #512) is not stated crisply. "Mirroring `LaunchMentionAsync`" is
actively misleading if the mention path does not use the coordinator; an implementer
following that cue literally would not get redelivery idempotency, breaking
`slack-dm-dispatch` requirement 4 and issue acceptance criterion 7.

**Fix:** state explicitly that `LaunchConnectionAsync` routes through
`AgentLaunchCoordinatorGrain` keyed by the connection-derived idempotency key (i.e. it
shares the manual-launch idempotency machinery), and that this is what makes a redelivered
Slack message resolve to the same SessionInput. Clarify the "mirroring" wording to refer
only to the origin-in-key encoding pattern, not the call path.

### P4. The adapter-liveness / `WaitingForSlackService` transition mechanism is unspecified

`slack-connection-setup` requirement 3 and issue acceptance criterion 3 require the
Connection to sit in "Waiting for Slack service" while `mohist-slack` is offline and to
advance once it is available. D5 deliberately rejects a SignalR connection tracker, and the
adapter is HTTP-pull only — so the Server has no defined way to know the adapter is online
or offline. No design decision and no task defines a register/heartbeat route or an
offline-timeout. Without it, the SetupProgress state machine in T-003 cannot transition out
of `WaitingForSlackService`, and the "offline service preserves prior progress" scenario
has no defined inverse.

**Fix:** add a design decision specifying the liveness mechanism (e.g. the adapter
`POST`s a register/heartbeat on start and on a short interval; the Server marks the
Connection's adapter-side health offline after a missed-heartbeat timeout using injectable
time). Add the server-side heartbeat route to a task (T-006) and the adapter-side heartbeat
to T-009, with acceptance criteria.

### P5. One `slack-dm-dispatch` scenario is untestable

`slack-dm-dispatch/spec.md` requirement 5, second scenario ("No continuation semantics are
implied") ends with "this issue makes no commitment to continue, queue, or merge it into
the running turn." A spec scenario must state required behavior; "makes no commitment" is
not testable. The requirement's first scenario already specifies the actual behavior (a
second DM dispatches an independent launch), making the second scenario both redundant and
non-normative.

**Fix:** tighten the scenario to state the concrete behavior (each DM dispatches an
independent launch regardless of whether an earlier task is still running), or remove it.

## Minor notes (not blocking)

- **No scenario for "dispatch before Setup Complete / no Owner".** `slack-dm-dispatch`
  requirement 1 assumes an Owner exists; there is no explicit scenario that a DM dispatched
  before Setup is Complete is rejected with no Agent resources created. Adding one would
  close the loop with `slack-connection-setup` requirement 7.
- **Claim-code vs task DM classification** (how the Server distinguishes a DM whose text is
  a pending claim code from a task DM) is implied by T-005 but not stated in any spec
  scenario; a one-line scenario would make it explicit.
- **Mild spec overlap:** `mohist-slack-adapter` requirement 3 and `slack-connection-setup`
  requirement 4 both assert `mohist-slack` is a CLI-managed service. Harmless, but the two
  could be de-duplicated.
- The issue's "Runner 离线 → 排队" case (acceptance criterion 8) is satisfied by reusing the
  existing AgentJob queue rather than new work; this is fine but is not called out anywhere
  in the plan, which could confuse a reader expecting new Runner-offline detection.

## Verdict

The plan's skeleton — capabilities, spec coverage, task DAG, issue-criteria mapping,
non-goal discipline — is correct and complete. But P1-P4 leave the two central flows (DM
dispatch + Setup liveness) with unowned or ambiguous mechanisms, and P5 is a spec defect.
These are fixable without re-architecting. They should be fixed before building.

<promise>FAIL</promise>
