# Review Report

## Result: FAIL

The post-repair candidate preserves stable AgentSession identity, source parity, idle recovery conflicts, and the runtime wire rename. CLI, runner, Web, and server unit gates pass. The full server spec gate fails 3/2,790 tests, and the unresolved findings below affect session audit safety, command delivery, and user-visible state.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test fixture
  Evidence: `packages/runner/tests/runner-host-lifecycle.spec.ts:144` constructed the now-required `SessionCommandRequest` without `operationId`, so `npm run typecheck:tests -w packages/runner` failed with TS2741. Added the operation id.
  Verification: `npm run typecheck:tests -w packages/runner && npm test -w packages/runner -- runner-host-lifecycle.spec.ts` passed (10 tests).
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: test expectation
  Evidence: `packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionSpecs.cs:332` still asserted the removed physical-session rebind contract. It now asserts `agent_session_attach_conflict` and preservation of `acp-1`.
  Verification: `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj -p:SkipWebBuild=true --no-restore --filter 'FullyQualifiedName~AgentSessionSpecs'` passed 28/28.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: test determinism
  Evidence: `AgentJobGrainSpecs.cs:454` advanced fake time past the AgentJob timeout before Reset, allowing the timeout callback to make the session active. The idle advance now occurs before the short-timeout job starts.
  Verification: `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj -p:SkipWebBuild=true --no-restore --filter 'FullyQualifiedName=Mohist.Server.SpecTests.Specs.Agent.Grain.AgentJobGrainSpecs.DelayedGenericJobFailure_AfterReset_DoesNotCloseTheReplacementRuntime'` passed.
  Status: resolved

## Blocking Items

- [ID: item-4]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs`
  Evidence: `CloseGenericSessionAsync` returns without appending `session.closed` whenever a job has a `RunnerId` but no recorded `RuntimeSessionId` (`:670-675`). A completed AgentJob can therefore leave its AgentSession nonterminal and without audit evidence. This deterministically fails `AgentSessionLaunchRoutesSpecs.cs:407` and both generic transcript-axis specs (`GenericAgentSessionTranscriptAxisSpecs.cs:91,225`); the full server spec run failed 3/2,790. [disallowed:AgentJob/session terminal behavior and audit semantics]
  SuggestedAction: Make terminal close reporting safe for an unbound session while retaining the stale-binding guard that prevents an old job from closing a Reset replacement. Require and validate the attach-to-job binding before accepting reports, or persist a binding generation that can distinguish an unbound session from a replaced one.
  Verification: Report a completed and a failed AgentJob before runtime attachment, after normal attachment, and after Reset. Each original session must gain exactly one terminal fact; a delayed old-runtime close must not close the replacement.
  Status: unresolved

- [ID: item-5]
  Severity: blocking
  Scope: follow-up SignalR delivery
  Evidence: Both HTTP routes await `InvokeAsync` (`IssueRoutes.Sessions.cs:140`, `AgentSessionFollowupRoutes.cs:114`), while `followup-handler.ts:140-149` awaits `connection.prompt`. `connection.prompt` is the full prompt-completion promise (`actions/acp/liveness.ts:81-89`), so a follow-up request remains open for the complete agent turn instead of acknowledging accepted delivery. The existing runner test does not await the handler response, so it cannot detect the wait.
  SuggestedAction: Return the delivery acknowledgement once the follow-up has been accepted for execution, keep prompt completion asynchronous, and surface later execution failures through the established session events.
  Verification: Keep `connection.prompt` pending, await the registered `ReceiveFollowup` handler, and assert `{ accepted: true }` before resolving the prompt for both workflow and generic targets.
  Status: unresolved

- [ID: item-6]
  Severity: blocking
  Scope: `packages/runner/src/runtime/session-command-journal.ts`
  Evidence: `parseJournal` accepts any object-valued `operations` field (`:136-145`), including `[]` or nested arrays. The load loop then treats it as an empty valid journal, and `session-command-handler.ts:77-97` can execute a previously reserved Compact or Reset after restart. This violates the required fail-closed, no-blind-replay recovery protocol. The only corrupt-journal test covers invalid JSON, not parseable invalid shapes. [disallowed:data safety and recovery protocol]
  SuggestedAction: Validate the outer and per-session maps as plain records and reject any invalid entry or shape by marking the journal unavailable. Add restart tests for array-shaped and malformed nested maps with zero runtime-handler calls.
  Verification: Persist each malformed but parseable shape, restart the journal, invoke a matching Reset, and assert `unavailable` without invoking the runtime handler.
  Status: unresolved

- [ID: item-7]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Api/AgentSessionCancelRoutes.cs`
  Evidence: Cancel checks `EnsureRuntimeSessionPresentAsync` before its documented no-runner `not-cancellable` paths (`:76-93`). A minted but unopened AgentSession therefore receives `409 runtime_session_missing` rather than the honest `200 { state: "not-cancellable" }` response promised by the route contract.
  SuggestedAction: Resolve terminal/no-runner outcomes before requiring a live runtime binding; reserve the missing-runtime conflict for targets that can actually be dispatched to a runner.
  Verification: Cancel an unbound Agent-launch session and assert `not-cancellable`; cancel a bound session with a missing runtime and assert the Reset hint.
  Status: open

- [ID: item-8]
  Severity: warning
  Scope: runtime-session history UI
  Evidence: `CompactionLineageLink.tsx:56-62` presents `?rt=` links as navigation between runtime sessions, but `useIssueSessionDataSource.tsx:174-207` and `useGenericSessionDataSource.ts:90-121` neither include that parameter in transcript query keys nor request/filter transcript data by runtime binding. Both links render the same full logical-session transcript, so the selected historical runtime is not actually viewed.
  SuggestedAction: Define and implement the historical transcript contract, then make `rt` select a server-filtered transcript or a deterministic client-side binding segment. Add predecessor/successor navigation assertions for both source pages.
  Verification: Create two runtime bindings with distinct transcript events, open each lineage link, and assert only the selected binding's turn range is rendered.
  Status: open

- [ID: item-9]
  Severity: warning
  Scope: follow-up composer mutations
  Evidence: `useIssueSessionDataSource.tsx:274-276` and `useGenericSessionDataSource.ts:165-167` call React Query `mutate`, which returns `void`. `SessionFollowupComposer.tsx:56-64` consequently treats the call as successful, clears the text, and shows `Sent` before an HTTP rejection can reach its error path.
  SuggestedAction: Return `mutateAsync` (or an equivalent promise) from both data sources and preserve the draft on rejection. Add rejection coverage for workflow and generic follow-up routes.
  Verification: Make each follow-up request return 409/503 and assert the composer retains its input, exposes the error, and does not render `Sent`.
  Status: open

- [ID: item-10]
  Severity: warning
  Scope: workflow session cache reconciliation
  Evidence: `useIssueSessionDataSource.tsx:183-187` invalidates `['issues', issueNumber, 'coder-sessions']`, but `useCoderSessions.ts:18` uses `['issues', issueNumber, projectId, 'coder-sessions']`; the recovery invalidation never matches. It also omits the workflow-run session query (`useWorkflowRunSessions.ts:28`), and generic recovery invalidates only detail/transcript queries (`useGenericSessionDataSource.ts:99-102`). Reset can therefore leave lists showing the old runtime binding.
  SuggestedAction: Invalidate the exact project-scoped session-list and workflow-run query keys for the affected source, with focused query-client assertions.
  Verification: Seed every affected list query, complete Compact and Reset, and assert each query is invalidated and refetches the new runtime binding.
  Status: open

- [ID: item-11]
  Severity: warning
  Scope: issue-session cancel UI and CLI surface
  Evidence: `useCancelSessionMutation.ts:11-20` has no success invalidation or handling for the API's `not-cancellable` terminal-state response, unlike the generic mutation. `SessionDetailShell.tsx:700-702` closes the dialog on every settlement, leaving a stale running page without feedback. Separately, `MohistCliCommands.Issue.Session.cs:47-52` omits `cancel` even though the issue-scoped API endpoint exists (`IssueRoutes.Sessions.cs:176-199`) and `mo agent session cancel` exists.
  SuggestedAction: Reconcile issue-session queries and display the returned cancel state; add `mo issue session cancel <number> <name>` or explicitly revise the documented command-surface contract.
  Verification: Exercise `cancelled`, terminal, and `not-cancellable` responses in the issue page, then verify CLI routing and output for a workflow session.
  Status: open

- [ID: item-12]
  Severity: warning
  Scope: `docs/actions/opencode.md`
  Evidence: The changed implementation-gap note says the new Session identity and Session operation semantics are still unimplemented (`:135-140`), contradicting `design/agent-execution.md:170-174` and the issue-407 CLI documentation. This gives users conflicting guidance about the delivered logical-session contract.
  SuggestedAction: State that issue-407 delivered logical identity and command routing while issue-409 still owns native OpenCode SDK execution.
  Verification: Review the implementation-gap notes in `docs/actions/opencode.md`, `docs/cli-reference.md`, and `design/agent-execution.md` for one consistent issue-407/#409 boundary.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-13]
  Severity: info
  Scope: `docs/cli-reference.md:200`
  Evidence: The reference lists `mo issue session get`, but the command registered by `MohistCliCommands.Issue.Session.cs:61` is `show`. The line is unchanged from `master`; the current docs delta did not introduce it.
  SuggestedAction: Change `get` to `show` in a documentation maintenance change.
  Status: pre-existing

- [ID: item-14]
  Severity: info
  Scope: `docs/epics.md:30,57`
  Evidence: The page correctly says `p0-p3` at line 30 but says `p0-p4` in its field table. CLI validation accepts `p0|p1|p2|p3`.
  SuggestedAction: Correct the table to `p0-p3`.
  Status: pre-existing

<promise>FAIL</promise>
