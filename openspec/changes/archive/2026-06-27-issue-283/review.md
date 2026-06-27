# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:334, packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:417, packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicAutoDoneSpecs.cs:161
  Evidence: The accepted rule is "Epic ready to complete" only when the epic still has linked issues and no open linked issue. `EpicProgress.IsReadyToComplete` encodes that correctly as `linked.Count > 0 && !linked.Any(IsOpen)` in packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs:64, but the grain readiness paths do not use that predicate. `ReconcileAfterTerminalInternalAsync` and `TryAutoMarkDoneAsync` only test `open.Count == 0`, and `ComputeOpenLinkedNumbersAsync` returns an empty set for `links.Count == 0`; therefore an idle/running epic with no linked issues still auto-transitions to `done`. The retained test `AutoMarkDoneIfReadyAsync_NoLinkedIssues_TransitionsToDone` asserts that divergent behavior, so the test suite locks in a mismatch with the issue acceptance criterion that auto-completion, manual Mark Done, and detail ready-to-done use the same rule. [disallowed:product-behavior-change]
  SuggestedAction: Route grain readiness through a shared computation that includes both linked-count and open-linked checks, or otherwise pass enough linked issue context into the aggregate guard so empty epics are not considered ready when the read model reports `readyToMarkDone=false`. Update the empty-epic auto-done/reconcile tests to assert no transition, and add a regression test proving `EpicProgress.IsReadyToComplete([])` and grain auto-done agree.
  Verification: `npm test` passed (server: 2867 passed / 14 skipped; web workspace: 2414 passed / 1 skipped; runner workspace: 662 passed / 23 skipped). `npm run typecheck -w packages/web` passed. `npm run test:run -w packages/web` passed (2414 passed / 1 skipped). `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true` passed (392 passed). These commands verify the current snapshot but do not cover the required empty-epic shared-readiness behavior because the existing server test expects the wrong outcome.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs
  Evidence: The new overload `EpicProgress.IsTerminal(LinkedIssueDto)` uses issue terminal semantics (`done`/`completed`/`cancelled`), while existing overloads `IsTerminal(EpicStatus)` and `IsTerminal(string)` use epic terminal semantics (`done`/`closed`). The parameter types prevent runtime ambiguity, but the same method name now means different terminal sets in one class.
  SuggestedAction: Consider renaming the linked-issue predicate to `IsLinkedIssueTerminal` or documenting the overload boundary at call sites if future work expands terminal states.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: workflow artifacts under openspec/changes/issue-283/
  Evidence: The workflow artifacts exist as plan/build/check evidence and are not product deliverables. Their presence is expected during this Mohist workflow and was not treated as a candidate failure.
  SuggestedAction: None.
  Status: out-of-scope

<promise>FAIL</promise>
