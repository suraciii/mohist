# Review Report

## Result: FAIL

Reviewed the live issue, proposal, design, tasks, delta specs, and the current candidate through `b685b1b94`, plus the local review repair below. The producer-side lineage snapshots, migration path, causal Epic propagation, binding recovery, catalog declarations, web reader, runner, and affected tests were inspected.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: missing-obvious-guards
  Evidence: After a state-only save failure, `WorkflowGrain` marks the activation reload-required. `StartAsync` did not enforce that guard, so a paused run could resume and persist the stale in-memory lineage. Added `RejectIfRunReloadRequired()` before `StartAsync` mutates the run and a regression spec covering the failed lineage save followed by `StartAsync`.
  Verification: `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter "FullyQualifiedName~WorkflowGrainStateSaveFailureSpecs"` passed (3 tests); `npm test` passed.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/app/providers/model/event-envelope.ts`
  Evidence: `mergeIssueLineage` uses stamped `extensions.issue` and `extensions.issueid` only when the payload lacks identity. The live timeline then routes on that merged payload. The regression test deliberately supplies payload issue `42` and stamped envelope issue `99`, and asserts that `42` wins. This violates the issue boundary that routing reads producer-stamped envelope lineage rather than `data`, and can place an event on the wrong issue timeline. [disallowed: public client-routing behavior]
  SuggestedAction: Preserve the original payload for display, but derive timeline routing identity from envelope extensions first, with the legacy-key fallback limited to historical envelopes.
  Verification: Send an envelope whose payload and extensions disagree; assert the timeline routes to the stamped issue while retaining the original payload unchanged.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Api/WorkflowControlGuard.cs`
  Evidence: The `awaiting-binding` case accepts every `ActiveOnly` action. Both issue-scoped and workflow-run-scoped routes therefore call `ResumeAsync`, `ApproveAsync`, `RequestChangesAsync`, or `PauseAsync`; each domain operation rejects that state, but those routes do not translate the exception to a conflict response. Only `StopAsync` catches `InvalidOperationException`. The added API coverage tests retry, rerun, rerun-from-stage, and stop, but omit these four invalid controls. [disallowed: public HTTP error contract]
  SuggestedAction: Represent stop separately in the control guard, reject all other controls during binding, and add issue-scoped and workflow-run-scoped 409 assertions.
  Verification: Exercise resume, approve, reject, and pause against an `awaiting-binding` run and assert conflict responses with no state or event changes.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: Epic-to-Issue affiliation redelivery during workflow binding
  Evidence: `IssueGrain.SetEpicAffiliationAsync` saves the Issue snapshot, then always calls `ApplyIssueLineageAsync` when `WorkflowRunId` exists. In the valid crash window after the Issue start commit but before workflow creation, that call targets a missing run and throws. Undelivered events are sorted by source, so `/mohist/epics/` events run before `/mohist/issues/` binding events; the dispatcher stops on the failed Epic handler and exhausts its retry budget before the binding event can create the run. The later binding event converges the lineage, but a routine recovery path creates a dead letter. [disallowed: durable retry behavior]
  SuggestedAction: Do not propagate to a workflow while the Issue binding is pending, or otherwise recognize the missing prepared run as an expected transitional state. Add an end-to-end dispatcher regression with the production retry budget.
  Verification: Seed an undelivered Epic affiliation event and `IssueWorkStarted` event with a pending binding and no workflow row; assert no dead letter and eventual Issue/Workflow lineage convergence.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs`
  Evidence: `CatalogOnlyTypes` exempts `workflow.run.retrying` and `workflow.run.rerunning`, but `WorkflowEventSerializer` has no producer for either type. This makes the produced-types coverage assertion pass by excluding two unproduced workflow entries. The design and task specification allow only `runner.disconnected` and `workflow.repair-scheduled` as catalog-only protocol types, so the conformance check no longer detects this catalog/producer drift.
  SuggestedAction: Remove the two unsupported catalog entries or introduce matching domain events, serializers, and production-path conformance coverage.
  Verification: Keep the produced-types assertion exact with only the two documented catalog-only exceptions.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

(none)

## Verification

- `git diff --check` passed after the local repair and review report update.
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter "FullyQualifiedName~WorkflowGrainStateSaveFailureSpecs"` passed: 3 tests.
- `npm test` passed: 865 CLI, 1,412 server unit, 2,824 server spec, 22 architecture, 4,656 web, and 1,016 runner tests.
- `npm run typecheck -w packages/web` passed.

<promise>FAIL</promise>
