# Self-Review — issue-456 (cycle 2)

Reviewing the updated `proposal.md`, `specs/`, `design.md`, `tasks.json` against
issue 456 and the current code. Reviewer only; no files changed other than this
one.

This is the second cycle. Cycle 1 found B1/B2/S1/S2/S3; the operative decisions
(D2, proposal bullet 12, proposal Impact, tasks T-001/T-002) were updated to
address them. The fixes correctly target the root cause. Two residual issues
remain: one implementability gap in the B1 fix, and stale framing text that was
not propagated from the operative decisions to the surrounding paragraphs.

## Verdict

**FAIL.** Two bounded problem classes remain, both surgical to fix.

## What cycle 1 fixed correctly (verified)

- **D2 now wires task/artifact events.** The decision body correctly identifies
  that `TaskStarted`/`TaskCompleted`/`TaskFailed`/`ArtifactRecorded` are
  subscribed (`canonical-event-types.ts:15-18,87-91`) but absent from `ROUTE`
  (`handle-event.ts:237-274`) and `AGENT_ACTIVITY_EVENT_NAMES`
  (`handle-event.ts:55-71`), and that the page's task UI reads solely from the
  `useWorkflowTimeline` cache (`StageBar.tsx:48-59`, `WorkflowView.tsx:2,9-16`),
  so removing the 5 s poll without wiring would leave task completions stale.
  This is the correct root-cause fix and the local-merge / narrow-spec
  alternatives are properly rejected. Proposal Impact (line 23) and T-001
  (description, acceptance criteria, output, notes) are consistent with it.
- **D7 added the blocked-causes matrix** (S1): run failure / approval / stage are
  event-driven via D2; drift/convergence ride the retained `useWorkspaceStatus`
  poll. Honest and testable.
- **Spec scenario reworded** (S2): `issue-detail-live-updates` Scenario 3 now
  conditions on "the viewed issue enters an approval-waiting or blocked state".
- **Nudge-set scope recorded** (S3): proposal capability bullet, design D3, and
  T-002 (new AC: a `failed`-only transition does not toast) all state the
  approval+blocked scoping with rationale.
- **D4 (reading stability), D5 (reconnect signal via `LiveTaskState`), D6
  (mobile parity), D1 (remove poll)** remain accurately grounded
  (`IssueDetailPage.tsx:215`, `live-task.tsx:4-8`, `events-hub.ts:109,139,171`).
- Tasks DAG, priority ordering, spec anchor slugs, and AC test-verification are
  valid.

## Blocking problems

### B1'. The D2 fix is implementability-incomplete: adding task/artifact entries to `ROUTE` requires extending `EventMap` first, which the plan does not mention

`ROUTE` is typed `Partial<Record<EventName, DomainHandler>>`
(`handle-event.ts:237`), and `EventName = keyof EventMap`
(`entities/issue/@x/events.ts:40`). `EventMap` (`events.ts:5-37`) lists
`Stage*`, `StageApproval*`, `WorkflowRun*`, `Issue*`, `AgentSession*`, and
`InboxItemPersisted` — but it does **not** list `TaskStarted`/`TaskCompleted`/
`TaskFailed`/`ArtifactRecorded`. Those names are in `REVERSE_DNS_EVENT_TYPES`
and therefore in `EVENT_TYPES` (the subscription list, and the set the
`_AssertRouteSubscribes` guard checks against), but they are **not** keys of
`EventMap`, hence not valid `EventName` keys for the `ROUTE` literal.

Consequence: as written, `[REVERSE_DNS_EVENT_TYPES.TaskCompleted]: taskHandler`
in the `ROUTE` literal is a **compile error** (the key is not assignable to
`Partial<Record<EventName, DomainHandler>>`). An autonomous builder following
D2/T-001 would hit this immediately; the correct resolution is to extend
`EventMap` with the four entries (with their payload shapes, derived from the
event contract — task events carry `issueNumber`/`projectId` plus task identity,
mirroring how `buildTimelineLiveEvent`/`describe.ts` already consume them) and
*then* add the `ROUTE` entries. The plan must state this prerequisite
explicitly. Without it, a builder is nudged toward defeating the
`_AssertRouteSubscribes` guard with a cast rather than the intended typed entry.

Required fix: D2 and T-001 must include "extend `EventMap` in
`entities/issue/@x/events.ts` with `TaskStarted`/`TaskCompleted`/`TaskFailed`/
`ArtifactRecorded` payload shapes, then add the matching `ROUTE` entries."
T-001's `output`/`notes` should list the `events.ts` change alongside the
`handle-event.ts` change.

### B2'. Stale framing text still asserts the refuted "no changes needed" story (incomplete propagation of the B1/B2 fix)

The operative decisions were updated, but several surrounding sentences still
state the old (incorrect) position and now contradict D2 / the proposal body:

- `design.md:7` (Context) — "the 5 s timer is redundant in steady state: every
  workflow event that mutates the timeline already invalidates `['issues']`".
  This is the exact claim B1 refuted: task events mutate the timeline but do not
  currently invalidate `['issues']`. It directly contradicts D2, which adds the
  wiring. Should be softened to "redundant for the coarse-grained transitions
  (stage / run / approval / issue) which already invalidate; task-level
  transitions require the wiring D2 adds".
- `design.md:23` (Non-Goals) — "New event types, event-routing changes, or any
  server/runner/CLI change" lists "event-routing changes" as a flat non-goal.
  This contradicts D2 (client-side `ROUTE` entries are added) and `proposal.md:12`
  (which re-scopes the boundary to server-side). It should read "no server-side
  event-routing/subscription changes and no new event types", matching the
  proposal's clarified boundary.
- `design.md:9` (Context) — "no event types, routing, DTOs, or persistence
  change". The bare "routing" has the same ambiguity and should be qualified to
  "server-side routing".
- `proposal.md:31` (Risk) — "Mitigated by reusing the existing event-stream
  ingestion and query-invalidation path unchanged". "unchanged" is now
  inaccurate: D2 adds task/artifact `ROUTE` entries to the invalidation path.
  Drop "unchanged" or note the path gains task/artifact entries per D2.

These are the same class of internal inconsistency cycle 1 blocked on (B2); the
fix updated the operative paragraphs but not the framing around them. They are
small, surgical edits, but they must be made so the design/proposal read as one
coherent position rather than two contradictory ones.

## Minor observations (non-blocking)

### M1. Spec Requirement 1 prose vs D7 on "blocked"

`issue-detail-live-updates` Requirement 1 prose lists "blocked states" among the
transitions applied "incrementally as those events arrive". D7 clarifies blocked
is partly poll-driven (drift/convergence), not purely event-driven. The
requirement's scenario (Scenario 3, reworded in S2) is state-based and
satisfiable, so this is wording only — but "as those events arrive" could be
softened to "as those transitions occur" so the prose matches D7 and the
scenario.

## Summary

The plan's operative core is now correct: the headline defect (task completions
not appearing live) is addressed at the right layer (client-side invalidation
routing for already-received events), with sound alternatives, an honest
blocked-causes matrix, a recorded nudge-set scope, and valid tasks. What remains
is (1) one missing compile prerequisite (`EventMap` extension) without which the
D2 fix does not typecheck, and (2) a handful of framing sentences that still
contradict the updated decisions. Both are quick, localized fixes.

<promise>FAIL</promise>
