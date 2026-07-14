# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting | missing-obvious-guards
  Evidence: `GenericSessionPage.test.tsx` used `screen.getByText('Completed')` to assert the session status badge. After the Coder Session layout change, `StatusBadge` is rendered both in the session header and in the sticky title, so the text `Completed` appears twice and the query matched multiple elements, causing the test to fail. Scoped the assertion to the `session-header` region using `within(screen.getByTestId('session-header')).getByTestId('session-status-badge')` so the header badge is asserted independently of the sticky title duplicate.
  Verification: `npx vitest run src/pages/session/ui/GenericSessionPage.test.tsx` — 28 passed.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Api/ProjectEventsRoutes.cs:60-99` and `packages/server/src/Mohist.Server/AgentOps/Services/ProjectEventFeedAssembler.cs:258`
  Evidence: The `ProjectEventDto` exposes `IssueNumber` for workflow-run and agent-session events, but `FromIssue` never populates it, so issue-state events carry `IssueNumber: null`. The Web projection falls back to parsing `Subject` (CloudEvents subject) as the issue number, which works in current seeding because tests set `Subject` to the issue number. If a producer ever emits an issue event with a non-numeric subject, the Activity entry will lose its issue link. Populating `IssueNumber` from the joined `Issues.Number` column in `LoadIssueEventsAsync` would make the DTO consistent with the acceptance criterion that events include issue number context.
  SuggestedAction: Pass `issue.Number` from the `LoadIssueEventsAsync` join into `ProjectEventEnvelope.FromIssue` and through to `ProjectEventDto`.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/pages/session/ui/SessionDetailShell.tsx:489` and `packages/web/src/pages/session/ui/SessionDetailShell.tsx:712`
  Evidence: `StatusBadge` is rendered in both the `SessionHeader` and the `StickySessionTitle`, both using `data-testid="session-status-badge"`. The duplicate test id forces tests to scope to one region or use `getAllByTestId`, and the duplicated status text is announced twice to screen readers. The sticky title is intentionally compact, but the duplicate test id and status text are unnecessary.
  SuggestedAction: Keep the sticky title's status badge but change its `data-testid` to `session-sticky-status-badge` (or remove the test id) and consider `aria-hidden` for the duplicate badge text so it is not re-announced.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/pages/activity/ui/ActivityPage.tsx:188-190`
  Evidence: `StatusBar` receives `completed` and `failed` counts from `evidenceCounts`, which only counts `ActivityEvent.outcome` values set on recorded events. Snapshot entries (session, waiting, runner) do not have `outcome`, so completed/failed sessions discovered only via the live activity snapshot are not reflected in the StatusBar. Meanwhile `active` and `waiting` still come from `useActivityCards`, which is the older snapshot feed. This mixes two counting schemes.
  SuggestedAction: Derive completed/failed counts from the same source as active/waiting (either extend snapshot entries to carry terminal outcome or use `useAgentActivity.summary` directly), or document the intentional difference.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Api/ProjectEventsRoutes.cs:83`
  Evidence: `ProjectEventDto.Origin` is produced by `envelope.Origin.ToString().ToLowerInvariant()`, yielding `issue` / `workflowrun` / `agentsession`, while `SourceAggregateKind` is hyphenated (`issue` / `workflow-run` / `agent-session`). The Web projection defensively accepts both forms, but the wire contract is inconsistent. A single lowercase-hyphenated vocabulary would be cleaner and less error-prone for future consumers.
  SuggestedAction: Normalize `Origin` to the same hyphenated vocabulary as `SourceAggregateKind` (e.g., `workflow-run`, `agent-session`).
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: `packages/web` Vitest worker heap pressure
  Evidence: Running the full `packages/web` Vitest suite (`npm run test:run -w packages/web`) with `NODE_OPTIONS=--max-old-space-size=8192` still triggers a V8 "Ineffective mark-compacts near heap limit" OOM in a single worker, causing "Worker exited unexpectedly" even though every test that completed passed (4653 tests passed, 0 failed, 10 tests in one file were not reached). This reproduces with or without the reviewed change and is independent of the touched files. The targeted test files for this change all pass in isolation.
  SuggestedAction: Consider lowering Vitest's default worker count or raising the worker heap limit in a separate infrastructure change; this is not a defect in the issue-402 code.
  Status: pre-existing

<promise>PASS</promise>
