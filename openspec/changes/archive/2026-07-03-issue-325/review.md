# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: cleanup
  Evidence: `using Mohist.Server.Infrastructure.Events;` in `IssueQuerier.cs:7` is unused. `EventCatalog.ReverseDns.*` constants were the sole consumer and moved to `IssueMetricsQuerier`. No remaining references to any type from this namespace in the file.
  Verification: `rg "EventCatalog|ReverseDns" --include "*IssueQuerier.cs"` returns no results.
  Status: resolved (removed the orphan import; build re-verified with 0 errors, 0 warnings).

- [ID: item-2]
  Severity: info
  Scope: dead-code-removal
  Evidence: `IssueQuerier.ResolveIssueRepository` at `IssueQuerier.cs:223` is never called. It is a leftover from when `_resolver` (`IssueRepositoryResolver`) was injected into `IssueQuerier`'s constructor. After the `ToInfo` → `BuildInfo` consolidation, all callers go through `IssueReadModelLoader.BuildInfo`, which instantiates a local `IssueRepositoryResolver`.
  Verification: `rg "IssueQuerier\.ResolveIssueRepository" --include "*.cs"` returns no results outside the definition.
  Status: resolved (removed the dead method; build re-verified with 0 errors, 0 warnings).

## Blocking Items

None.

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `IssueReadModelLoader.cs` and `IssueMetricsQuerier.cs`
  Evidence: `DeserializeRun(string json)` is duplicated identically in `IssueReadModelLoader.cs:357` and `IssueMetricsQuerier.cs:1660` (same 3-line `try/catch` body). `LoadWorkflowStatesAsync` (both the `IReadOnlyCollection<string>` and `IReadOnlyCollection<IssueReadModel>` overloads) is duplicated in `IssueReadModelLoader.cs:177,190` and `IssueMetricsQuerier.cs:1375,1402` with near-identical implementations. Design D4 stated "The helper methods it needs (`LoadWorkflowStatesAsync`, ...) move to the loader" but the metrics querier cannot call them because the methods are `private` on the loader. Making them `internal` or exposing them on the loader's API surface would eliminate the duplication.
  SuggestedAction: Expose `LoadWorkflowStatesAsync` and `DeserializeRun` on `IssueReadModelLoader` as `internal` (or a shared static helper) so `IssueMetricsQuerier` can delegate instead of re-implementing.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `IssueQuerier.cs` usings
  Evidence: At least `using Mohist.Server.Infrastructure.Events;` is known-unused in `IssueQuerier.cs` (item-1 repaired). `using Mohist.Server.Workflow.Domain.Run;` (`WorkflowRun` type) may also be orphan after `DeserializeRun` and `ComputeStageProgress` moved to the loader. `using Mohist.Server.Workflow.Services;` may have narrower-than-before usage now that `MohistDefaultWorkflowProjection`, `WorkflowStatusMapper`, and `WorkflowStageProgress` all reside in the loader. The project has `TreatWarningsAsErrors` but `IDE0005` (unused using) may not be enabled.
  SuggestedAction: Audit all `using` directives on `IssueQuerier.cs` against its remaining code surface; remove any that no longer have consumers. Enable `IDE0005` at warning level project-wide.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `IssueQuerier.cs` constructor and fields
  Evidence: The `_effectiveProfileResolver` and `_projectProfileManager` fields on `IssueQuerier` are now used only by the two instance `ToInfo` overloads (lines 214-221), and transitively by `GetInfoAsync`. Before the split these served both the instance `ToInfo` wrappers and all the metrics preludes. After the split their remaining surface is small: `GetInfoAsync` → `ToInfoAsync` → `ToInfo(issue, project, templateId, disabledIds)` → `_effectiveProfileResolver.Resolve(...)`. `_projectProfileManager` is used only for `GetDisabledWorkflowProfileIdsAsync` in `ToInfoAsync`. The single-issue `GetAsync` path delegates mapping to `_loader.ToReadModel(await ToInfoAsync(...))` and projection to `_loader.ApplyProjectionsToSingleAsync(...)`. The design is defensible but the fields are now thin wrappers for the loader path — worth reviewing whether `GetInfoAsync` and one `ToInfo` could live on the loader as well.
  SuggestedAction: If acceptable, move the instance `ToInfo` overloads to `IssueReadModelLoader` and have `GetInfoAsync` delegate through the loader; this would remove `_effectiveProfileResolver` and `_projectProfileManager` from `IssueQuerier`, further narrowing its dependency set.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: `IssueRoutes.Metrics.cs:29` and `IssueRoutes.ApprovalMetrics.cs:22`
  Evidence: Both route handlers call `DateTimeOffset.UtcNow` (the wall clock) instead of the injected `TimeProvider`. The delivery-time, quality, and stage-duration routes use `timeProvider.GetUtcNow()`. This inconsistency is pre-existing: the diff shows only the `IssueQuerier` → `IssueMetricsQuerier` rename and type-reference repointing.
  SuggestedAction: Inject `TimeProvider` in both routes to eliminate real-time dependency, aligning with the project's testing principle of "no wall clock."
  Status: pre-existing

<promise>PASS</promise>
