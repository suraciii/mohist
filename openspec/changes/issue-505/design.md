## Context

`architecture.md` L58 states builtin workflow content belongs in `*.workflow.yaml`. Four sites in generic C# violate this boundary:

1. **`IssueMetricsQuerier.cs:35`** — `QualityStageOrder = ["plan","build","check","integrate"]` fabricates phantom stage rows and forces alphabetical ordering for custom stages. The stage-duration path (`:1054`–`1058`) already sources order from `ResolveProjectStageOrderAsync` (`:1588`), which reads `profile.Definition.Stages`.
2. **`RunnerRoutes.cs:566`–`572`** — `ToWorkDispatchResponseAsync` matches `stage == "plan" && uses == "mohist/opencode"` to decide parent-issue-context attachment during HTTP response assembly. `WorkDispatch` has no declarative flag; a test pins this (`RunnerPollParentContextMapperSpecs.cs:29`).
3. **`VariableBundle`** (`VariableBundle.cs:22`–`26`) — carries a fourth layer (`DefaultVars`/`DefaultStages`) plus a clear-on-explicit-write protocol, persisted in a `DefaultVariables` column (`WorkflowRunProfileRow.cs:27`). The sole consumer is `EnsureArchiveDefaultAsync` (`WorkflowRunVariablesStore.cs:59`), which seeds `archive: ""` at run startup — a value the runner's `readArchiveHint` (`openspec.ts:324`–`330`) already treats as absent.
4. **`IssueRoutes.Helpers.cs:102`** — `BuildRebaseRecovery()` hand-builds a `RecoveryDefinition` (including `Uses: "mohist/opencode"`) inside an HTTP route helper. The YAML parser has no reusable-recovery mechanism; all recovery is inline per-task (`WorkflowDefinitionParser.cs:72` `TopLevelKeys = { "approval", "stages" }`).

## Goals / Non-Goals

**Goals:**
- Generic code holds zero knowledge of specific builtin stage names, Action identifiers, or the `archive` variable key.
- Quality metrics derive stage set and order from the Definition; no phantom rows.
- Parent issue context attachment is not decided by string-matching in HTTP assembly.
- Variables resolve across exactly three scopes; the defaults machinery and its persistence column are gone.
- The rebase recovery definition has one author: workflow content.

**Non-Goals:**
- Variables scope-count changes (still three).
- Recovery budget / remaining-amount / failure semantics changes.
- Metrics dimension or display changes.
- Profile class rename or split (separate issue).
- A general reusable-recovery-template system beyond what rebase needs.

## Decisions

### A. Quality stage order mirrors the stage-duration path

Delete `QualityStageOrder`. Thread the Definition-sourced stage order into `BuildWindow` the same way `ComputeStageDuration` already receives it: the caller resolves `ResolveProjectStageOrderAsync(db, projectId)` and passes the resulting `IReadOnlyList<string>` to `BuildWindow`.

`BuildWindow` changes from prepending the 4 hardcoded names to:
1. Start with the Definition-ordered stages, filtered to only those actually entered (`accumulator.EnteredByStage.ContainsKey`).
2. Append observed stages absent from the Definition, in their original (insertion) order — not alphabetical.

This produces no phantom rows (entered=0 stages are filtered out) and preserves builtin parity (the builtin Definition lists `plan/build/check/integrate` in that order, and those stages are entered during normal runs).

Cross-profile aggregation: the project default profile's Definition provides the primary ordering. Observed stages from non-default profiles append after, matching the stage-duration path's existing behavior (`IssueMetricsQuerier.cs:1055`–`1058`).

**Alternative considered:** Collect stage order per-run from each run's bound Definition and merge. Rejected: the quality window is project-level; `ResolveProjectStageOrderAsync` already resolves the project's effective profile and is the established pattern. Per-run collection adds complexity for a case (mixed profiles in one window) that the stage-duration path already handles the same way.

### B. Parent issue context attached unconditionally for workflow tasks

Remove the `stage == "plan" && uses == "mohist/opencode"` guard. Attach parent issue context for **all** workflow-owned task dispatches (`OwnerKind == Workflow`, `WorkType == Task`) that carry a valid `projectId` and `issueNumber > 0`. The existing `resolveParentIssueContext` callback returns null for issues without a parent, so nothing is attached in that case.

This satisfies the spec's superset requirement: every dispatch that previously received context still does, plus any that were filtered out by the stage/uses match.

**Alternative considered:** Add a declarative `ConsumesParentIssueContext` flag to `WorkDispatch`, set from the Action's input contract at dispatch-assembly time (`WorkflowItemTranslator`). More architecturally pure — the Action declares its need — but requires threading Action capability metadata server-side (Action manifests live in the runner's `built-ins.ts`) and adding a new field to `WorkDispatch` despite the test pinning its shape. Overkill for a single consumer. Noted as a future refinement if more Actions need parent context selectively.

**Cost:** one extra `GetParentIssueContextAsync` DB call per non-plan task dispatch with a parent issue. Task dispatches are low-frequency (handful per issue). If profiling shows need, cache the resolution per poll batch.

### C. Delete the defaults layer entirely

Remove `DefaultVars` / `DefaultStages` from `VariableBundle`, leaving only `Vars` / `Stages`. This cascades:

- `VariableBundle`: drop fields, `HasDefaultContent`, `ClearDefaultsCoveredByExplicit`, all stripping/intersection helpers, default-aware merge/resolve branches.
- `WorkflowRunVariablesStore`: delete `EnsureArchiveDefaultAsync`, `ArchiveDefaultKey`, `GetDefaultVariablesAsync`, `BuildArchiveDefaultElement`, `HasArchiveKey`, `MutateVariablesAsync`'s defaults read/write.
- `WorkflowProfileManager.MergeRunScopedVariables`: simplify to `VariableBundle.MergeAll(project, issue, run)`; delete `MergeRunScopedVars` / `MergeRunScopedStages`.
- `WorkflowGrain`: delete the two `EnsureArchiveDefaultAsync(GrainKey)` calls (`:241`, `:256`).
- `WorkflowRunProfileRow`: delete `DefaultVariables` property.
- `MohistDbContext`: remove the `DefaultVariables` EF config (`:873`); add migration to drop the column.

The `archive` seed is a no-op: `readArchiveHint` (`openspec.ts:324`–`330`) returns null for empty/absent strings, and the action already computes a fresh destination when the hint is null. Removing the seed changes nothing the user sees. The runner spec test for absent-hint behavior formalizes this.

**Serializer compatibility:** Orleans `[property: Id(n)]` indices shift when fields are removed. AGENTS.md states no version compat is needed. Persisted JSON in the `Variables` column from old writes may contain `DefaultVars`/`DefaultStages` keys; System.Text.Json ignores unmapped members by default, so reads are safe.

### D. One mechanism: rebase recovery authored in workflow content

**Decision:** API-triggered one-shot task injection and Profile-driven recovery are the **same mechanism**. Both produce a `RuntimeTaskInput` carrying a standard `RecoveryDefinition`; the only difference is the trigger (API vs. runner executor). There is no behavioral or semantic distinction that warrants a parallel representation.

**How the content moves:** Add a top-level `recoveries` section to the workflow YAML schema:

```yaml
recoveries:
  rebase-conflicts:
    budget: 2
    handlers:
      - when: error.code=conflict
        tasks:
          - id: recover:resolve-rebase-conflicts
            title: Resolve rebase conflicts
            uses: mohist/opencode
            with:
              session: check
              prompt: ${{ prompts.resolve-rebase-conflicts }}
              options: ${{ vars.agent }}
        retrySelf: false
```

Parser change: add `"recoveries"` to `TopLevelKeys`, parse each entry with the existing `BuildRecovery` logic (`WorkflowDefinitionParser.cs:389`), producing `IReadOnlyDictionary<string, RecoveryDefinition>?` on `WorkflowDefinition`.

Both builtin YAMLs declare the `rebase-conflicts` template. The rebase route resolves the run's bound profile, reads `definition.Recoveries["rebase-conflicts"]`, and passes it as `Recovery` on the `RuntimeTaskInput`. `BuildRebaseRecovery()` is deleted. `BuildRebaseTaskWith(baseBranch)` stays — it constructs runtime input (`baseBranch` is dynamic), not workflow-definition structure.

`design/workflow/recovery.md` gets a section stating the one-mechanism conclusion and documenting the `recoveries` top-level key as the home for named recovery templates referenced by API injection.

**Alternative considered (two concepts):** Give one-shot injection its own representation distinct from `RecoveryDefinition`. Rejected: the rebase task's recovery is standard recovery — budget, handlers, when-matching, retrySelf. A parallel model duplicates these semantics for no gain.

**Alternative considered (C# service, not route helper):** Move `BuildRebaseRecovery` to a workflow service. Rejected: the issue's principle is that recovery's sole author is the workflow definition, not any C# code. A service still hardcodes `mohist/opencode` and prompt references.

## Risks / Trade-offs

- [Unconditional parent-context DB calls on every task dispatch] -> Low frequency; callback returns null for parentless issues. Cache per-poll batch if profiling demands.
- [Orleans serializer ID shift when removing VariableBundle fields] -> No version compat required (AGENTS.md). Fresh deployment; no rolling upgrade.
- [Old `Variables` JSON contains stale `DefaultVars`/`DefaultStages` keys] -> System.Text.Json ignores unmapped members by default; reads are safe. New writes omit the keys.
- [`recoveries` YAML schema addition is a parser change] -> Minimal: one new top-level key, reuses existing `BuildRecovery`. Round-trip serializer (`WorkflowYamlSerializer`) needs a corresponding emit path.
- [Rebase recovery duplicated in both builtin YAMLs] -> Small block (~10 lines). Acceptable duplication; each profile stays self-contained.
- [Quality metrics test expectations change] -> `BuildWindow` callers and accumulator tests need updating. Builtin-profile parity is pinned by a spec test to prevent regression.

## Migration Plan

Submit in four independent batches matching the issue's groups:

1. **Group A** (metrics): delete `QualityStageOrder`, thread stage order into `BuildWindow`, update accumulator/window tests, add custom-stage spec test.
2. **Group B** (dispatch): remove the stage/uses guard in `ToWorkDispatchResponseAsync`, update `RunnerPollParentContextMapperSpecs` to reflect unconditional attachment.
3. **Group C** (variables): slim `VariableBundle`, remove defaults machinery from store/manager/grain, drop `DefaultVariables` column + EF config, add migration `DropWorkflowRunProfileDefaults`, update all `DefaultVars`/`DefaultStages` unit + spec tests, add runner absent-hint spec test.
4. **Group D** (rebase): add `recoveries` to parser + model + serializer, declare `rebase-conflicts` in both builtin YAMLs, rewrite rebase route to source recovery from the definition, delete `BuildRebaseRecovery`, update `IssueRebaseRecoveryTests`, update `design/workflow/recovery.md`.

**Rollback:** each group is independently revertible via `git revert`. Group C's migration has a symmetric `Down` (re-add column with default `"{}"`); in practice, re-deploying the previous build re-adds the column.

## Open Questions

- Should `BuildRebaseTaskWith`'s `remote: "origin"` constant also migrate to workflow content? Currently scoped out as runtime input, but it is a builtin default. Defer to a follow-up if the boundary feels unclear after Group D lands.
- Should the `recoveries` top-level key support cross-profile sharing (e.g., a shared template file) to avoid duplicating `rebase-conflicts` in both YAMLs? Defer until a third profile or second shared recovery appears.
