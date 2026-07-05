# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test-cleanup
  Evidence: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Services/IssueTitleLookupSpecs.cs:147` had a misleading cross-project negative assertion: the foreign issue was number 1, but the assertion checked key 2 for value `Foreign #1`. Tightened it to assert that no returned title value is `Foreign #1`, preserving the test intent without changing product behavior.
  Verification: `dotnet test "packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj" --filter "FullyQualifiedName~IssueTitleLookupSpecs" -p:SkipWebBuild=true` passed: 10 passed, 0 failed.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Sessions/Services/TranscriptReductions.cs`; `openspec/changes/issue-370/specs/transcript-reductions/spec.md`
  Evidence: The relocated event-summary reduction documents and specifies ordering by `(sequence, id)`, but the implementation only applies `.OrderBy(e => e.Sequence)`. Concrete evidence: `TranscriptReductions.cs:42` says summaries are computed over events ordered by `(sequence, id)`, `TranscriptReductions.cs:56` only orders by `Sequence`, and `transcript-reductions/spec.md:24-27` requires parts projected and ordered by `(sequence, id)`. This was not repaired because adding `ThenBy(e => e.Id)` would be a product behavior change from the pre-relocation code path, while changing the spec/comment would be an architectural/spec decision about the intended contract. [disallowed:behavior/spec-contract ambiguity]
  SuggestedAction: Decide the intended contract. If `(sequence, id)` is intended, add `.ThenBy(e => e.Id)` and a regression test with duplicate sequence values. If preserving pre-change behavior is intended, change the spec and product XML doc to say sequence-only ordering, and add a duplicate-sequence regression test proving the preserved behavior.
  Verification: `rg -n "OrderBy\(e => e\.Sequence\)|ordered by <c>\(sequence, id\)</c>|order by \(sequence, id\)" "packages/server/src/Mohist.Server/Sessions/Services/TranscriptReductions.cs" "openspec/changes/issue-370/specs/transcript-reductions/spec.md"` shows the mismatch. `npm test`, `npm run typecheck -w packages/web`, and `npm run test:run -w packages/web` all pass, so this is a spec/coverage gap rather than a failing-suite issue.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowActivityQuerier.cs`
  Evidence: `WorkflowActivityQuerier.ListActiveAgentsAsync` was part of the sibling accessor migration (`WorkflowActivityQuerier.cs:41-46` now uses `record.Label(...)` and `record.IssueNumber()`), but there is no behavior coverage for this changed consumer. `rg "WorkflowActivityQuerier|ListActiveAgentsAsync|ActiveAgentDto" packages/server/tests` only finds the DI registration row in `MigratedServicesRegistrationSpecs.cs:94`. The accessor itself is unit-tested, but the workflow activity read path has no regression test proving workflow and agent-launch active-agent projections still emit the same label-derived fields after the refactor.
  SuggestedAction: Add focused specs for `ListActiveAgentsAsync` covering at least one workflow active session and one `agent-launch` active session, asserting project id, issue number, workflow run id, work id/type, stage, agent id/name, and active progress fields.
  Verification: `rg "WorkflowActivityQuerier|ListActiveAgentsAsync|ActiveAgentDto" packages/server/tests` shows no behavior spec. Existing gates still pass: `npm test` passed (server 3830 passed / 13 skipped; web 4295 passed / 1 skipped; runner 908 passed), `npm run typecheck -w packages/web` passed, and `npm run test:run -w packages/web` passed.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: issue wording
  Evidence: The issue body says `AgentSessionQuerier` has 14 `internal static` members, while the implemented/proposal partition and current code prove the moved surface was 13 members. This count drift is in the issue text, not the candidate product code. Current snapshot evidence: `rg -n "internal static" "packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs"` returns no matches, and `rg -n "AgentSessionQuerier\.(Build|Load|To|Issue|Label|Annotation|Reconcile|Labels)\b" "packages/server/src"` returns no matches.
  SuggestedAction: No product change required.
  Status: out-of-scope

<promise>FAIL</promise>
