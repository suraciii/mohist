## Why

`architecture.md` says builtin workflow content belongs in `*.workflow.yaml`, not generic code. Yet generic C# has grown four pieces of knowledge about specific builtin workflows: a hardcoded stage-name list that fabricates ghost stages in custom profiles, an HTTP-layer special-case on two concrete names, an entire extra variable-precedence layer + persistence column existing solely to seed one `archive` key, and a full recovery definition hand-built inside an API route helper. This couples generic mechanisms to builtin content, produces user-visible artifacts (phantom metric rows, alphabetical stage ordering) that the Definition already corrects elsewhere, and creates two authors for workflow-language structures that should have one.

## What Changes

- **Quality stage set/order comes from the Definition.** Delete the `QualityStageOrder = ["plan","build","check","integrate"]` constant in `IssueMetricsQuerier`. The set and order of stages in quality windows derive from the bound Definition's `Stages` list (as the stage-duration path already does), with cross-profile aggregation grouping by stage id in Definition order rather than degrading to alphabetical.
- **No phantom stages.** Quality windows no longer emit rows for stages absent from a Run's Definition. Built-in profile metrics are unchanged.
- **Dispatch context decision leaves the HTTP layer.** Remove the `stage == "plan" && uses == "mohist/opencode"` special-case in `RunnerRoutes`. Parent issue context availability is governed by the Action's input contract (or attached unconditionally); it is no longer decided during HTTP response assembly by matching concrete names. Parent issue context availability does not decrease.
- **Variables collapse to three layers.** Remove the `DefaultVars` / `DefaultStages` fourth layer from `VariableBundle`, the `EnsureArchiveDefaultAsync` / `ArchiveDefaultKey` seed path, and the "explicit write clears default" protocol. The `mohist/archive-change` Action defines its own behavior when `archiveHint` is absent, preserving today's user-visible result. Run startup no longer injects `vars.archive`.
- **Recovery has one author.** `design/workflow/recovery.md` first decides whether API-triggered one-shot task injection and Profile-driven recovery are one mechanism or two (spec-first). Accordingly, the rebase recovery definition (including the hardcoded `mohist/opencode`) moves into builtin workflow content, or one-shot task injection gets its own name/representation and stops reusing `RecoveryDefinition`. `IssueRoutes.Helpers` no longer constructs workflow-definition structures.

## Capabilities

- `quality-stage-metrics`: How quality metric windows determine which stages appear and in what order — sourced from the Run-bound Definition, with cross-profile aggregation rules; covers ghost-stage removal and Definition-order preservation.
- `dispatch-parent-context`: When and how parent issue context is attached to a work dispatch, moved out of HTTP response assembly and decoupled from concrete stage/action name matching.
- `workflow-variables-defaults`: The variable precedence model, collapsing the four-layer `VariableBundle` (with `DefaultVars`/`DefaultStages`) back to the documented three Project → Issue → Run layers, including removal of the `archive` default seed and its clear-on-explicit-write protocol.
- `rebase-recovery`: The definition and ownership of recovery for API-triggered rebase — whether it is the same mechanism as Profile-driven recovery or a distinct one-shot task injection, and where its definition lives.

## Impact

- **Server (`packages/server`):**
  - `Issue/Services/IssueMetricsQuerier.cs` — `QualityStageOrder` removal, stage-order source change, cross-profile aggregation rewrite.
  - `Api/RunnerRoutes.cs` — dispatch-context special-case removal.
  - `Workflow/Domain/VariableBundle.cs` — drop `DefaultVars`/`DefaultStages`, `ClearDefaultsCoveredByExplicit`, related merge/resolve logic.
  - `Workflow/Services/WorkflowRunVariablesStore.cs` — remove `EnsureArchiveDefaultAsync`, `ArchiveDefaultKey`, `DefaultVariables` read/write paths.
  - `Workflow/Services/WorkflowProfileManager.cs` — remove default-layer merge (`MergeRunScopedVariables` default branches).
  - `Workflow/Grains/WorkflowGrain.cs` — remove `EnsureArchiveDefaultAsync` calls on run startup.
  - `Api/IssueRoutes.Helpers.cs` / `IssueRoutes.Rebase.cs` — remove `BuildRebaseRecovery()` and C#-constructed `RecoveryDefinition`.
  - **BREAKING** — persistence: delete the `DefaultVariables` column on `WorkflowRunProfiles` with an EF Core migration.
- **Runner (`packages/runner`):** `mohist/archive-change` Action — define explicit behavior for absent `archiveHint` (no regression in visible result).
- **Design docs (`design/`):** `design/workflow/recovery.md` — resolve one-mechanism-vs-two question (spec-first); `design/workflow/variables.md` requires no change (it already describes three layers; the implementation gap closes).
- **Tests:** spec coverage for custom-stage-name metrics (Definition order, no phantom rows), builtin-profile metric parity, runner dispatch context, archive-action missing-hint behavior, and rebase recovery; unit tests updated for the slimmed `VariableBundle`.
- **Web/CLI:** no change (confirmed `DefaultVars` never surfaces outside the server).
