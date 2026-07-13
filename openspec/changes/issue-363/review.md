# Review Report

## Result: FAIL

Post-repair verification passed: `npm run build` and `npm test` (2,889 server specs, 1,390 server unit tests, 24 passing architecture tests, 875 CLI tests, 4,596 web tests, and 1,007 runner tests).

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: dead-code-removal
  Evidence: `ReconcileCandidate` retained unused `Status` and `DispatchSnapshot` fields after live-state revalidation replaced snapshot use.
  Verification: `npm run build`; `npm test`
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: small-test-expectation-update
  Evidence: `EpicEventPublishSpecs` claimed to cover every EpicEvent variant and catalog entry but omitted the new `EpicStartAttemptFailed` variant.
  Verification: `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --filter "FullyQualifiedName~EpicEventPublishSpecs"` (14 passed)
  Status: resolved

## Blocking Items

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:142-154,807-826,1023-1068`; `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:47-105`
  Evidence: The new link and start-failure recovery handlers depend on `EpicIssueLinked` and `EpicStartAttemptFailed`, yet state is committed before `PersistEpicEventsAsync` and that helper deliberately catches append failures. A failed append leaves a command-path start failure permanently running-but-idle; a crash or failed append between link commit and inline recompute loses the only proposed recovery trigger. The event store already exposes an overload that appends into the caller's DbContext. [disallowed:data safety and recovery behavior]
  SuggestedAction: Persist recovery events atomically with the affected epic transition, then add fault-injection specs for event-append failure and the post-commit crash window.
  Verification: Force the epic-event append to fail and assert the transition rolls back or has a durable recovery record; restart after a committed link and assert the recompute still occurs.
  Status: unresolved

- [ID: item-4]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Domain/Issue.Prerequisites.cs:17-24`; `packages/server/src/Mohist.Server/Events/Subscriptions/EpicAutoDoneHandler.cs:19-118`
  Evidence: Removing a prerequisite emits `IssuePrerequisiteRemoved`, which can make a linked backlog issue startable in a running-but-idle epic. The candidate subscribes only to completed, cancelled, and undraft events, so this transition never invokes recompute after the sweep is deleted. This was a readiness transition the prior periodic scan covered. [disallowed:product behavior and event orchestration]
  SuggestedAction: Add a durable prerequisite-removal recompute trigger for the owning active epic and specify the readiness transition.
  Verification: Start an epic with a member blocked only by a prerequisite, remove that prerequisite, dispatch the event, and assert the member starts.
  Status: unresolved

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Runner/Grain/RunnerGrainConcurrencySpecs.cs:262-330,442-505`; `design/testing.md:53-59`
  Evidence: The timeout test blocks AgentJob before it calls Runner, so Runner has no tracked work when `CloseoutLostAsync` enumerates it; the asserted closeout is conditional and the reciprocal `ReportResultAsync` cycle is never exercised. The suite also uses `TestWait.ForAsync` wall-clock timeouts and `Task.Delay`, violating the explicit no-wall-clock acceptance criterion.
  SuggestedAction: Add deterministic grain-test signals that pause after Runner owns the work and while the same AgentJob retries assignment; assert the closeout report and retry both settle without wall-clock polling.
  Verification: Run the new real-Orleans scenario with only awaitable signals and FakeTimeProvider advancement.
  Status: unresolved

- [ID: item-6]
  Severity: warning
  Scope: `openspec/changes/issue-363/{proposal.md,design.md,tasks.json,specs/}`; epic event subscriptions
  Evidence: The reviewed implementation adds `EpicDraftChangedHandler`, prerequisite reverse lookup, `EpicIssueLinkedHandler`, `EpicStartRetryHandler`, and a new public event type. The approved artifacts instead specify link-time recompute and state that cross-aggregate event-to-command semantics do not change (`tasks.json:77,86`), with no scenarios for the new event contracts. This prevents the artifacts from serving as an accurate merge and regression specification. [disallowed:architectural and product-contract judgment]
  SuggestedAction: Update the proposal, design, tasks, and delta specs to define the additional event triggers, their reliability contract, and their non-goals before merging.
  Verification: Review the updated artifacts against the handler subscriptions and event catalog; add scenario coverage for each documented trigger.
  Status: unresolved

## Follow-up Items

No follow-up items.

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs:17,157-205`
  Evidence: Per-handler retry counts and backoff live only in the singleton `_states` dictionary. A process restart resets failures to attempt one, so repeated restarts can indefinitely defer dead-lettering. `git blame` attributes this to `fd8496067`, an ancestor of `master`.
  SuggestedAction: Persist per-handler delivery state and add a dispatcher-restart test.
  Status: pre-existing

- [ID: item-8]
  Severity: info
  Scope: server architecture and spec suites
  Evidence: The passing full suite skipped 12 tests: 3 architecture tests and 9 server specs; none belongs to the candidate changes.
  SuggestedAction: Track skipped tests with their owning work.
  Status: pre-existing

<promise>FAIL</promise>
