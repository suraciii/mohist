# Self-Review — issue-456 (cycle 3)

Reviewing the updated `proposal.md`, `specs/`, `design.md`, `tasks.json` against
issue 456 and the current code. Reviewer only; no files changed other than this
one.

This is the third cycle. Cycles 1 (B1/B2/S1/S2/S3) and 2 (B1'/B2'/M1) were
fixed. Re-verifying the fixes hold and checking for any remaining build-blockers.

## Verdict

**PASS.** The plan is internally consistent, technically accurate against the
current code, and ready to build.

## Fixes from prior cycles — verified holding

- **D2 (task/artifact wiring + EventMap prerequisite).** Confirmed against code:
  `TaskStarted`/`TaskCompleted`/`TaskFailed`/`ArtifactRecorded` are subscribed
  (`canonical-event-types.ts:15-18,87-91`) but absent from both `ROUTE`
  (`handle-event.ts:237-274`) and `EventMap` (`entities/issue/@x/events.ts:5-37`).
  D2 correctly identifies both gaps: it adds the `ROUTE` entries (invalidating
  `['issues']` per the `stageHandler` pattern) AND specifies extending `EventMap`
  with the four entries first, since `ROUTE` is typed
  `Partial<Record<EventName, DomainHandler>>` with `EventName = keyof EventMap`.
  The note to extend `EventMap` rather than cast preserves the
  `_AssertRouteSubscribes` guard. Migration step 3 and T-001 (description, output,
  notes) all carry the prerequisite. This resolves the cycle-1 AC-delivery gap
  (B1) and the cycle-2 compile gap (B1').
- **D7 (blocked-causes matrix).** Honest and accurate. Confirmed the timeline
  poll (line 158) and the workspace-status poll (line 175) are the only
  `refetchInterval`s in `entities/issue/api/queries.ts`; D1 removes only the
  timeline one, so drift/convergence-driven blocks continue to surface via the
  retained workspace-status poll. Removing the timeline poll does not regress
  drift detection (the timeline query's refetch never cascaded to the issue
  query that carries `drift`/`convergence` anyway). D7's "no worse than today"
  framing is correct.
- **D3 (nudge) + scope.** `RuntimeSummary` includes `'approval-required'` and
  `'blocked'`; the global toast helpers suppress for `viewedIssue`
  (`run-lifecycle-toast.ts:12,29`), so the page-owned edge-triggered nudge is the
  sole viewed-issue toast (exactly-once) and the `failed`-exclusion is a
  recorded decision matching the AC. T-002 locks it with a dedicated AC.
- **D4/D5/D6 (reading stability, reconnect signal, mobile parity).** Grounded
  accurately (`IssueDetailPage.tsx:215` first-load-only guard; identity-stable
  list keys; `LiveTaskState` shape at `live-task.tsx:4-8`; `reconnectVersion` at
  `events-hub.ts:109,139,171`).
- **Framing consistency (cycle 2 B2').** Context line 7, line 9, Non-Goals line
  23, and proposal Risk line 31 are all reconciled with D2. The Server/runner/CLI
  bullet's "no event routing changes" reads correctly in its scoped context
  (server-side). No remaining internal contradiction in the plan artifacts.
- **Specs.** Both spec files are normative, testable, scenario-covered, and
  consistent with the design (Requirement 1 prose now says "as those transitions
  occur"; Scenario 3 is state-based). Tasks DAG, priority ordering, anchor slugs,
  and AC test-verification are valid (T-001=8 ACs, T-002=9 ACs).

## Non-blocking observation (for the builder's awareness, not a fix requirement)

### N1. Broad `['issues']` invalidation for task events also refetches the board

The task/artifact handler reuses the `stageHandler` pattern of invalidating the
broad `['issues']` prefix. `useIssues` (the board query, `queries.ts:86`) sits
under that prefix and has no `refetchInterval` of its own, so it will gain
refetches on task events it did not get before (stage events already trigger
this). This is defensible — task transitions are low-frequency, the board
refetch is cheap, and consistency with `stageHandler` keeps one invalidation
model — and the design's over-refetch risk note covers the detail-page side. If
the builder wants to avoid touching the board entirely, a targeted invalidation
(`['issues', issueNumber, projectId]` + the `'workflow-timeline'` key) using the
`issueNumber` the task payload carries would scope the refetch to the viewed
issue. Either choice satisfies the spec; flagging only so the call is
deliberate.

## Summary

The plan is ready to build. The two prior blocking defects — task completions
not appearing live (cycle 1), and the ROUTE entries not compiling without an
`EventMap` extension (cycle 2) — are both resolved at the root, with accurate
code references, sound alternatives, an honest blocked-causes trade-off, a
recorded nudge-set scope, and valid tasks. The one remaining note (N1) is a
defensible design choice, not a defect.

<promise>PASS</promise>
