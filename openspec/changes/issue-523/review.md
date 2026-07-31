# Review

## Findings

### [P1] Queued turns are treated as idle and the wrong non-terminal turn is exposed

`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs:573` derives `recoveryAvailable` only from `Session.Status.Activity`. An accepted follow-up can leave the session activity as `idle` while its turn is still queued (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:1126-1134`), while the recovery command guard rejects any pending follow-up (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:899-905`). The Web also requires `activity === active` before exposing either turn-control handle (`packages/web/src/pages/session/data/useUnifiedSessionDataSource.tsx:111-115,208-221`), so a queued turn has Compact/Reset enabled but no Cancel control. When an executing turn and a later queued turn coexist, `CurrentTurnId` selects the last queued turn (`AgentSessionQuerier.cs:744-747`), preventing the active executing turn from receiving Stop. Controls and recovery availability must be derived from the authoritative non-terminal turn/pending state, with the executing turn taking precedence for Stop.

### [P1] The initial unified transcript request can show superseded runtime history

`packages/web/src/pages/session/data/useUnifiedSessionDataSource.tsx:98-100` starts the transcript query before the summary has supplied `runtimeSessionId`. `unifiedSessionTranscriptQueryOptions` enables that query without a binding (`packages/web/src/entities/coder-session/api/client.ts:45-53`), and `AgentSessionQuerier.LoadTranscriptAsync` interprets a null binding as all runtime turns (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs:874-893`). On a fresh page load or immediately after Reset, the unscoped response can therefore render prior-runtime turns before the binding-scoped response arrives, violating the requirement to exclude stale runtime content. The query must wait for the current binding or the server must resolve the current binding when no query parameter is supplied.

### [P1] Terminal Job/Turn results are not rendered by the unified page

`AgentTurnRecord.Result` contains the authoritative message, output, failure, category, and exit code (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.cs:552-584`), but `AgentTurnObservationDto` exposes only id, sequence, input IDs, and status (`packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs:273-277`). The unified shell uses `launchObservation` only to display generic guidance (`packages/web/src/pages/session/ui/SessionDetailShell.tsx:271-277`) and never renders `turnResult`, Job output, or the terminal result fields; Workflow sessions do not have a launch observation at all. A completed or failed Job/Turn can consequently appear as an idle Session with no latest conclusion, so the page does not distinguish the Job result from the continuing Session or satisfy the result/evidence acceptance criteria.

### [P1] Reset and Compact evidence disappears after reload

The Server persists context-reset and compaction transcript parts when recovery succeeds (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:239-275,908-999`), but `SessionTranscriptBuilder` handles only text, reasoning, tools, status, and session activity (`packages/server/src/Mohist.Server/Sessions/Services/SessionTranscriptBuilder.cs:43-92`). The unified summary also has no reset lineage or compaction-history fields (`packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs:364-387`). `SessionRecoveryActions` only closes the dialog and invalidates queries after success (`packages/web/src/widgets/coder-session/ui/SessionRecoveryActions.tsx:166-170`), and the page has no post-reset marker. After a reload, users cannot see that later work starts from a new runtime context or inspect recorded compactions, contrary to the tracking and context-maintenance specs.

### [P1] Recovery command failures do not reconcile the Session view

Both recovery mutations call the page callback only from `onSuccess`; their `onError` handlers merely set an inline error (`packages/web/src/widgets/coder-session/ui/SessionRecoveryActions.tsx:138-175`). The callback is the only place the unified data source invalidates summary, transcript, and list queries (`packages/web/src/pages/session/ui/SessionDetailShell.tsx:255-262` and `useUnifiedSessionDataSource.tsx:125-137`). The Server explicitly returns 409 for state races and 503 for runner failures (`packages/server/src/Mohist.Server/Api/AgentSessionRecoveryRoutes.cs:93-108,139-161`). After such a rejection the page can keep showing the old idle state and enabled controls, instead of recomputing availability from authoritative state as required.

### [P1] Failed follow-up requests skip authoritative refresh

`useUnifiedSessionDataSource.sendFollowup` invalidates unified queries only after `mutateAsync` resolves (`packages/web/src/pages/session/data/useUnifiedSessionDataSource.tsx:155-174`). Its catch path retains the idempotency key and rethrows without reconciliation. The canonical Server route returns non-2xx conflicts for races such as an active follow-up, stop, or unknown activity (`packages/server/src/Mohist.Server/Api/AgentSessionFollowupRoutes.cs:282-292`). Those failures leave the summary, transcript, and Session lists stale and provide no refreshed authoritative state, violating the requirement that rejected, failed, and unknown command outcomes converge.

### [P1] Workflow task Session chips are plain text in production

`TaskItem` can resolve a stable Session ID only when the optional `workflowSessionsHook` is supplied (`packages/web/src/widgets/issue-workflow/ui/TaskItem.tsx:246-249`); otherwise `TaskSessionChip` deliberately renders a span instead of a link (`TaskItem.tsx:112-128`). The production Issue page renders `<WorkflowView issue={issue} readOnly />` without dependencies (`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:509-512`), so the hook is never passed through `WorkflowView`/`StepList`. Task-associated Session names therefore no longer navigate to `/sessions/:sessionId`, leaving one of the promised Session entry points unusable.

### [P1] The required `npm test` gate currently fails on a stale baseline

The branch deletes `packages/web/tests/SessionPageHeader.spec.tsx` but leaves its 1006-line allowance in `scripts/node-test-file-budget-baseline.json:38`. Running `npm test` reaches Web `check:test-boundaries` and fails with `has a baseline allowance ... but no active test file at that path`. This directly violates T-001's `npm test` acceptance criterion even though the Vitest suite itself passes.

### [P1] Browser tests still exercise removed Session routes

The route migration removed the Issue-scoped and generic detail routes, but browser tests still navigate/assert them at `packages/web/tests/browser/issue-decision-surface.spec.ts:426,677`, `packages/web/tests/browser/workflow-sessions-responsive.spec.ts:272`, and `packages/web/tests/browser/coder-session-compact-viewport.spec.ts:297,414`. A targeted browser run produced four failures; the actual implementation navigated to `/sessions/{sessionId}` while the tests expected `/issues/{number}/workflow/sessions/{name}` or `/agent-sessions/{id}`. These tests must be migrated with the route change; otherwise the browser validation suite cannot pass.

### [P2] Resolved model selection is not ordered across turns

`ToTranscriptProjectionsInSequenceOrder` sorts parts only by each part's sequence and ID (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs:896-902`). Part sequences are allocated per turn (`packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionTranscriptStore.cs:117-142`), while `TranscriptEventSummaryProjector` applies model events in enumeration order (`packages/server/src/Mohist.Server/Sessions/Services/TranscriptEventSummaryProjector.cs:30-34`). With multiple turns in the current runtime, a later part from an older turn can overwrite the resolved model from the newer turn, causing the unified page to show the wrong model. The ordering must include turn sequence before part sequence and have a multi-turn regression test.

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm run check:fsd -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 5082 tests in 384 files.
- `npm test` failed at Web `check:test-boundaries` because of the stale baseline above.
- The targeted browser run failed four tests on the stale route expectations above.

<promise>FAIL</promise>
