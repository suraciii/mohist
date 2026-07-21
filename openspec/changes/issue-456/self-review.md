# Self-Review — issue-456

Reviewing `proposal.md`, `specs/`, `design.md`, `tasks.json` against issue 456 and
the current code. Reviewer only; no files changed other than this one.

## Verdict

**FAIL.** The plan contains one material correctness defect that defeats the
issue's headline acceptance criterion, plus a proposal↔design inconsistency and
an under-analyzed area. These must be fixed before build.

## What is solid

- **Scope and capability split are correct.** Two capabilities map 1:1 to the two
  spec files; the non-goals (no new event types, no server/runner/CLI change, no
  push notifications, no page restructure) match the issue.
- **D4 (reading stability) is accurately grounded.** The loading guard is
  first-load only (`IssueDetailPage.tsx:215` gates on `isLoading || !issue`), and
  the page-critical lists do use identity-stable keys (`StageBar` `key={stage}`,
  `WorkflowSessionRow` `key={session.id}`, `InlineApproval` `key={task.taskId}`,
  `TaskItem` `key={summary.artifactId}`, `FeedbackHistory` `key={item.id}`). The
  "audit + lock with tests, don't rewrite" posture is justified.
- **D5 is accurate.** `LiveTaskState` is exactly
  `{ activeTaskId, activeTaskElapsedMs, rebaseConflict }` (`live-task.tsx:4-8`),
  `useLiveTask()` exists, and `useEventsConnection` already returns
  `reconnectVersion` (`events-hub.ts:109,139,171`). Surfacing the reconnect
  signal through `LiveTaskState` is feasible as described.
- **D3 (edge-triggered nudge off `decision.summary`) is the right shape.**
  `RuntimeSummary` includes `'approval-required'` and `'blocked'`
  (`runtime-types.ts:5-10`; produced by `derive-runtime-decision.ts:87,94,99,105`),
  and the global toast helpers do suppress for `viewedIssue`
  (`run-lifecycle-toast.ts:12,29`). The no-fire-on-mount and exactly-once
  reasoning holds.
- **Tasks split (one feature module per capability, T-002 depends on T-001) is
  valid.** DAG is acyclic, priorities are strictly ordered, every task has
  acceptance criteria including test verification. Spec anchor slugs in
  `tasks.json` match the actual `### Requirement:` headings.

## Blocking problems

### B1. D2's cascade claim is false for task events — the page will not live-update on task completion, breaking the headline AC

Design D2 (`design.md:41-45`) asserts: "The page therefore already receives
incremental, event-driven updates for every transition the spec names (stage,
task, approval, blocked) once D1 removes the competing timer. No new invalidation
keys are introduced." This is incorrect for **task** transitions.

Evidence:

- Task events are canonical and subscribed: `TaskStarted`/`TaskCompleted`/`TaskFailed`
  and `ArtifactRecorded` are defined (`canonical-event-types.ts:15-18`) and included
  in `EVENT_TYPES` (`canonical-event-types.ts:87-91`), so the hub delivers them and
  `LiveTaskProvider.handleEvent` receives them.
- They are **not routed**: the `ROUTE` table (`handle-event.ts:237-274`) has no
  `Task*` or `ArtifactRecorded` entries, and `AGENT_ACTIVITY_EVENT_NAMES`
  (`handle-event.ts:55-71`) does not include them. So `routeEvent` runs no domain
  handler for a task event and **no `['issues']` (or timeline) invalidation fires**.
- The page's task-progress UI reads **only** from the `useWorkflowTimeline` query
  cache: `StageBar` maps `timeline.stages[].tasks[].status`
  (`StageBar.tsx:48-59`); `WorkflowView` consumes the timeline hook
  (`WorkflowView.tsx:2,9-16`); `TaskItem` renders `task.status`.

Consequence: under D1 (remove the 5s poll) + D2 (no new invalidations), a
`TaskCompleted` event does **not** cause the page's task progress to update. The
page goes stale on task start/completion/failure until some other event that *is*
routed (a stage/workflow/approval event) happens to fire, or until the reconnect
catch-up runs. That directly violates:

- AC line 1 — "a task completion … appear without reload".
- `issue-detail-live-updates` Requirement 1, which lists "task starts, task
  completions", and its Scenario "A task completion appears without reload or
  full re-render".
- The user-voice claim that the page should "update the moment something happens".

Required fix direction (choose one; the plan must pick and reconcile the
non-goal — see B3):

1. Add client-side `ROUTE` entries for `TaskStarted`/`TaskCompleted`/`TaskFailed`
   (and likely `ArtifactRecorded`) whose handler invalidates the timeline/issue
   keys, i.e. **contradicting D2's "no new invalidation keys"**; or
2. Have the page merge task events locally via the `onTimelineEvent` bus into the
   task-progress UI (the pattern `useEventTimeline` already uses) — i.e.
   contradicting D2's "rely on the cascade only"; or
3. Narrow spec Requirement 1 / AC line 1 to drop task start/completion from the
   live-update guarantee — i.e. changing the issue's stated acceptance.

D2 must be rewritten whichever path is chosen, and the chosen path must be
reflected in a spec scenario that is actually achievable.

### B2. Proposal and design are inconsistent on whether new invalidations are allowed

The proposal (`proposal.md:23`) says the ingestion path is reused "with any added
keys needed to refresh issue detail data on stream events" — explicitly hedging
that new invalidations *may* be required. The design (`design.md:43`) then
forecloses that hedge: "No new invalidation keys are introduced." The design's
choice is the one that creates B1. These two artifacts must agree; given B1, the
proposal's hedge was the correct instinct and the design's blanket "no new keys"
must be withdrawn or qualified.

## Secondary observations (not blocking, but should be addressed when B1 is fixed)

### S1. "Blocked" coverage under D2/D3 is under-analyzed

`blocked` is a *derived* summary with several causes (drift needs-attention,
convergence blocking, rebase conflict, run failure). Only some coincide with
`['issues']`-invalidating events: run failure routes through `workflowRunHandler`
(`handle-event.ts:150-168`), but drift is surfaced via `useWorkspaceStatus` (which
keeps its own `refetchInterval`, `queries.ts:170-180`) and convergence lives on
the issue object. The nudge (D3) is edge-triggered off `decision.summary`, which
recomputes only when the underlying query data changes. The plan does not state
which data feed drives a transition *into* `blocked` in each case, so it is
unverified that a drift-induced block will both (a) appear live under D1/D2 and
(b) trip the nudge promptly. The fix for B1 should carry an explicit analysis of
the blocked-causes matrix and which feed invalidates each.

### S2. Spec wording implies a "blocked-state event" that does not exist

`issue-detail-live-updates` Requirement 1, Scenario 3 conditions on "an
approval-requested event **or a blocked-state event** … arrives over the live
event stream". There is no discrete blocked-state event; the design (D3) correctly
says blocked is derived. The scenario should be reworded to "the issue entering a
blocked state", consistent with the attention-nudges spec, to avoid
implementer/test confusion.

### S3. The nudge set excludes `'failed'` — confirm this is intended

`RuntimeSummary` also includes `'failed'` (`runtime-types.ts:9`), distinct from
`'blocked'`, and a run failure on a non-viewed issue currently raises a global
"encountered an error" toast (`handle-event.ts:164-167`). The attention-nudges
spec nudges only approval-waiting and blocked, so **after this change a viewed
issue that fails while the page is open receives no toast at all** (global path
stays suppressed for `viewedIssue`; page nudge does not cover `'failed'`). This
matches the issue's AC literally ("approval-waiting or blocked"), but the user
voice says "the four moments that need them". If a run failure is one of the
moments that need the owner, the nudge set is too narrow. Worth an explicit
product decision recorded in the proposal/design rather than left implicit.

<promise>FAIL</promise>
