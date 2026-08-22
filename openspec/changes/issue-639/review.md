# Review: issue-639

## Verdict

FAIL — two must-fix problems remain: the ordinary Workflow input empty-receipt path is blocked before the outbox can confirm consumption, and the Server SpecTests assembly is left red by the changed boundary.

## Re-review disposition

- **Previous MF-1 — Workflow-session pure-activity boundary:** fixed. `AgentSessionGrain.AppendRuntimeEventsAsync` now uses persisted `SourceKind == "workflow"`, rejects current-binding unattributed non-activity and mixed batches before append, and the new no-turn grain/API tests cover the required cases.
- The findings below are new implementation/integration problems found after that disposition.

## Must-fix findings

### MF-1 — Valid empty Workflow input responses are rejected by `ServerConnection`

**Where:** `packages/runner/src/server/connection.ts:501-525`, in `workflowAgentSessionRuntimeEvents`.

The Workflow runtime-events route can validly return HTTP 2xx with `[]` for a `session.input` whose server-side binding was already consumed or not accepted. The Server route explicitly returns `Results.Ok(Array.Empty<RunnerRuntimeEventReceipt>())` for that case in `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs` (the `!receipt.WorkflowBindingAccepted` branch).

However, `ServerConnection.workflowAgentSessionRuntimeEvents` rejects that response whenever the request contained events:

```ts
if (submitted > 0 && payload.length !== submitted)
  throw new Error(`session runtime events acceptance mismatch ...`)
```

Consequently the Runner delivery adapter never receives the valid empty receipt array. The outbox's two-consecutive-empty confirmation path is therefore unreachable for ordinary `workflow-session` `session.input` records; it sees a malformed/failing delivery instead of two valid empty 2xx responses. This leaves the exact stale input records described by the issue retryable forever and violates the acceptance criterion that `session.input` settles after two consecutive valid 2xx empty-receipt responses. It also violates the convergence spec scenario **“A lost Workflow input acknowledgement is confirmed consumed”** and the design/T-003 requirement to preserve empty receipt arrays through the connection and delivery adapter.

The connection method must allow and return a valid empty array for this response shape while retaining validation for malformed responses and preserving the existing count/identity checks for non-empty responses. Add a connection/outbox regression test that delivers two empty Workflow input responses through the real adapter boundary and verifies already-consumed settlement.

### MF-2 — The Server SpecTests assembly is red after the shared boundary change

**Where:** `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1697-1711`.

The current `SourceKind == "workflow"` gate is now exercised by many existing Server fixtures and callers that create Workflow-labelled sessions and submit unattributed non-activity events through the session route/direct grain API. The full Server SpecTests run currently fails **48 of 3005 tests**, with failures such as:

- `WorkflowSessionSpecs.GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder`
- `WorkflowSessionSpecs.GivenMohistPromptAndTerminalFailure_WhenIssueWorkflowSessionEventsAreQueried_ThenRawEventsReturnInSequence`
- `GenericAgentSessionCanonicalFollowupApiSpecs.IssueSessionMetadata_ExposesFollowupInputAndTurnStatus`
- `AgentSessionReconnectRecoveryGrainSpecs.ReconcileMissingBinding_UnknownSession_SettlesIdleAndRebindsAtomically`
- `AgentSessionGrainPersistSuccessSpecs.Persistence_SavesBoundRuntimeEventsAndTranscript`

The failures are caused by the new `InvalidOperationException` at line 1711, not by compilation or infrastructure. The focused new boundary tests pass, but the changed shared grain contract has not been reconciled with the surrounding route fixtures and direct grain callers, so the repository's Server verification is not green. This violates the review requirement that changed behavior be covered and verified and leaves the change incomplete relative to the issue's Server integration/spec-test acceptance criterion.

Repair the affected route/fixture boundaries without weakening the issue's fail-closed rule: Workflow-labelled sessions must continue to reject unattributed non-activity or mixed batches, while legitimate generic/session-follow-up tests and callers must submit the appropriate complete identity or use a non-Workflow session fixture. Re-run the full Server SpecTests assembly and resolve all failures attributable to this change.

## Review dimensions

- **Issue basis: checked, no issue.** The issue acceptance criteria were reread before judging the implementation: current-binding activity-only acceptance, preserved Workflow attribution, bounded refusal settlement, double-empty input/cleanup settlement, warn-once retention behavior, retry preservation, and live delivery progress.
- **Coverage: FAIL.** The new no-turn pure-activity boundary coverage is present, but the ordinary Workflow input empty-array path has no end-to-end regression coverage and is blocked by the connection's acceptance-count check. The full Server suite also has 48 failures.
- **Correctness: FAIL.** MF-1 prevents the required already-consumed settlement for ordinary `workflow-session` input records. MF-2 leaves the shared Server behavior incompletely integrated with existing callers and tests.
- **Consistency with the surrounding codebase and plan artifacts: FAIL.** The Server boundary matches the revised design, but the surrounding Server SpecTests and direct callers have not been brought into that contract; the Runner connection simultaneously contradicts the design by rejecting the valid empty input response.
- **Tests: FAIL.** Runner focused verification passed: the six relevant suites ran 71/71 and both Runner TypeScript typechecks passed. The full Server SpecTests command
  `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --filter 'FullyQualifiedName~WorkflowAgentSessionExecutionBoundaryApiSpecs|FullyQualifiedName~AgentSessionGrainInputBoundarySpecs|FullyQualifiedName~AgentSessionRuntimeEventSpecs' --no-restore -p:SkipWebBuild=true`
  still reports 48 failures out of 3005, so the changed Server behavior is not fully verified.

## Observations

- The prior pure-activity finding is properly addressed: current-binding active and idle observations are accepted without Workflow attribution, while no-turn non-activity and mixed batches are rejected before mutation.
- The cleanup receipt-array route/connection/delivery path appears internally consistent: new cleanup returns one `session.cleanup` receipt and an idempotent replay returns `[]`; the focused cleanup tests pass.
- The deterministic 4xx allowlist, three-refusal per-key settlement, persistence ordering, two-empty cleanup settlement, retention warning edge tracking, and saturated-group Runner tests showed no additional must-fix issue in the executed focused suites.

<promise>FAIL</promise>