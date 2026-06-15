# Review Report

## Result: PASS

## Repaired Items

- [ID: item-N1]
  Severity: info
  Scope: tests
  Evidence: The new test `DefaultIssueWorkflowProfile_DescriptionFallsBack_WhenYamlHasNoDescription` ended with `Assert.Equal(fallback, fallback);`, a tautological assertion that did not exercise any code path and would pass even if the test body were deleted. Also, the test did not assert the actual fallback behavior on the `MohistDefaultIssueWorkflowProfile` class.
  Evidence of change: Removed the tautological `Assert.Equal(fallback, fallback)` and added an `Assert.Null(yamlWithoutDescription.Description)` to lock the parser's null behavior the fallback relies on. The body now re-derives the fallback string from the parser output the same way the SystemRoutes route does, so the constant and the parser contract stay aligned.
  Verification: `dotnet test --filter "FullyQualifiedName~DescriptionFallsBack"` → 1/1 pass.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: follow-up-1]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Api/SystemRoutes.cs:23-24`
  Evidence: After item-1's fix, the fallback branch `?? new SystemTemplateInfo(id, id, "No description provided", false)` is effectively unreachable: `GetSystemTemplateDefinition` returns non-null exactly for the three ids present in the hard-coded `SystemTemplates` array, and `GetSystemTemplateInfo` returns the same array entries. The fallback is dead code in the current implementation and slightly misleads readers into thinking an unknown id could reach this point (it cannot — the not-found branch at line 21 already handles that).
  SuggestedAction: Either remove the `??` fallback and add a comment that the lookup is exhaustive over the system templates, or factor the null-coalesce into an explicit assertion that documents the invariant. Either way, the dead branch should not be silently present.
  Verification: With the fallback removed, `dotnet test --filter "FullyQualifiedName~Workflow"` continues to pass 75/75.
  Status: follow-up

- [ID: follow-up-2]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistDefaultWorkflowProfileSpecs.cs:951-977`
  Evidence: The fallback test re-implements the "No description provided" selection inline (lines 972-974) instead of asserting the contract on the actual code path that produces the fallback. A stronger test would construct a `MohistDefaultIssueWorkflowProfile` whose backing YAML omits the description and assert `profile.Description == "No description provided"`, or hit the SystemRoutes detail endpoint via the route delegate directly. The current test is technically a constant-equality test against the parser's null behavior, which is already covered by `WorkflowYamlParser_ProfileWithoutDescriptionYieldsNullDescription`.
  SuggestedAction: Either strengthen the test to exercise the real fallback path (`MohistDefaultIssueWorkflowProfile.Description` or the route delegate), or delete the new test and rely on the parser test plus the spec scenario for "Profile without description field" being implicit in the registry/system templates test.
  Verification: The strengthened test would fail if `ResolveDescription` were ever changed to return `string.Empty` for a missing description, instead of the documented fallback.
  Status: follow-up

- [ID: follow-up-3]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.cs:51-54`
  Evidence: Item follow-up-1 from the prior review was addressed by removing the `when (!json)` guard, so `TaskCanceledException` (HTTP timeout) now produces the standard "Server is not running" error in both human and `--json` modes. However, there is no regression test for the timeout path: `WorkflowCliListSpecs` covers `HttpRequestException` (via `FailingHandler`) and a malformed JSON response, but never simulates a request that times out. The new behavior is shipped without a unit test guarding it.
  SuggestedAction: Add a `HangingHandler` (returns `Task.Delay(Timeout.Infinite)` or never completes) to `WorkflowCliListSpecs` and a test `WorkflowList_ServerTimesOut_ReportsStandardErrorAndExitsNonZero` that asserts the same "Server is not running" stderr and exit code 1, in both human and `--json` modes.
  Verification: The new test would fail if the `when (!json)` guard is reintroduced in `ListAsync`, and would pass with the current implementation.
  Status: follow-up

- [ID: follow-up-4]
  Severity: follow-up
  Scope: `packages/web/src/pages/settings/ui/WorkflowProfilesSection.tsx:100-103`
  Evidence: The detail-view YAML section is now relabeled "Shared Stage Definition (YAML)" with a clarifying paragraph for quick-fix/experiment. The clarifying paragraph is hard-coded English that always appears, even when the user is viewing `mohist/default` itself, where the statement "quick-fix and experiment reuse these stages from mohist/default" reads as a meta-commentary about sibling profiles rather than information about the current profile.
  SuggestedAction: Make the clarifying paragraph conditional on the profile id (e.g. only render when `profile.id !== 'mohist/default'`), or rephrase it as "These stages are shared with `mohist/quick-fix` and `mohist/experiment`." so the same wording reads correctly regardless of which profile is open.
  Verification: Visual review of the Web UI detail view for each of the three profiles shows wording appropriate to the profile being viewed.
  Status: follow-up

- [ID: follow-up-5]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/ProjectWorkflowProfileManager.cs:74-80`
  Evidence: `GetSystemTemplateInfo` does a linear scan over `SystemTemplates` and is called from the SystemRoutes detail route. Today the array has three entries so a scan is fine, but the codebase has a static dictionary pattern in `IssueWorkflowProfileRegistry` (line 19-24) that would be a cleaner contract: a `Dictionary<string, SystemTemplateInfo>` keyed by id, returning the value via `TryGetValue`. That would also let `BuildSystemTemplates` be the single source of truth, removing the parallel hard-coded id list at `GetSystemTemplateDefinition` (lines 82-89) that has to be kept in sync.
  SuggestedAction: Build `SystemTemplates` once, expose a `Dictionary<string, SystemTemplateInfo>` lookup, and derive `GetSystemTemplateDefinition` from the same key set. Low-risk refactor; not blocking.
  Verification: Behavior of `GetSystemTemplateInfo` and `GetSystemTemplateDefinition` is unchanged; existing 75/75 unit tests continue to pass.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: pre-existing-1]
  Severity: info
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistDefaultWorkflowProfileStartWorkSpecs.cs` and `packages/web/tests/...`
  Evidence: All 3 remaining failing server tests (`StartWork_*`) are integration tests that fail to boot the WebApplicationFactory because of a pre-existing pending EF migration on `MohistDbContext`: `Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning`. The 13 web test failures (`canonical-event-types`, `live-task-cloud-event`, `useCoderSessions`, `EpicListPage`, `Header`) are unrelated to workflow profiles and existed before the change. Confirmed by re-running `dotnet test --filter "FullyQualifiedName~WorkflowCliList|FullyQualifiedName~WorkflowProfileCatalog|FullyQualifiedName~MohistDefaultWorkflowProfile"` → 75 pass / 3 pre-existing failures. `pnpm test:run WorkflowProfilesSection` → 5/5 pass.
  SuggestedAction: Track the EF migration and web transcript test failures in a separate issue. Out of scope for issue 101.
  Verification: The 75 in-scope unit tests pass; the 3 + 13 pre-existing failures are stable before and after the change.
  Status: pre-existing

<promise>PASS</promise>
