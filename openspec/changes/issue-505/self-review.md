# Self-Review: issue-505

## Artifacts Reviewed

- `proposal.md` — motivation, four capabilities, impact
- `design.md` — four group decisions, risks, migration plan, open questions
- `tasks.json` — four independent tasks (T-001 through T-004)
- `specs/quality-stage-metrics/spec.md`
- `specs/dispatch-parent-context/spec.md`
- `specs/workflow-variables-defaults/spec.md`
- `specs/rebase-recovery/spec.md`

Cross-checked against the issue body (Done When A/B/C/D groups) and the actual codebase.

## Issue Coverage Check

Every Done When checklist item maps to a spec requirement and a task:

| Done When item | Spec | Task |
|---|---|---|
| A: `QualityStageOrder` deleted, Definition-sourced | quality-stage-metrics req 1 | T-001 |
| A: no phantom stages | quality-stage-metrics req 1 | T-001 |
| A: cross-profile Definition order, not alphabetical | quality-stage-metrics req 3 | T-001 |
| A: spec coverage | quality-stage-metrics scenarios | T-001 |
| B: special-case removed | dispatch-parent-context req 1 | T-002 |
| B: attachment via contract or unconditional | dispatch-parent-context req 2 | T-002 |
| C: archive absent-hint behavior + spec | workflow-variables-defaults req 3 | T-003 |
| C: `EnsureArchiveDefaultAsync` / `ArchiveDefaultKey` deleted | workflow-variables-defaults req 2 | T-003 |
| C: `DefaultVars` / `DefaultStages` deleted | workflow-variables-defaults req 1 | T-003 |
| C: clear-on-write protocol deleted | workflow-variables-defaults req 1 | T-003 |
| C: column dropped + migration | workflow-variables-defaults req 4 | T-003 |
| C: `variables.md` needs no change | proposal Impact | — (no change needed) |
| D: `recovery.md` decides one vs two | rebase-recovery req 2 | T-004 |
| D: recovery moves to workflow content | rebase-recovery req 1 | T-004 |
| D: `IssueRoutes.Helpers` stops constructing definitions | rebase-recovery req 1 | T-004 |
| server build + full tests | all tasks | T-001–T-004 |
| web typecheck + test | — | **see finding 3** |

All Non-Goals are respected by the design and specs.

## Codebase Accuracy Check

Verified against the actual code:

- `QualityStageOrder` at `IssueMetricsQuerier.cs:35` — confirmed; `BuildWindow` at `:1274`, `Concat` at `:1287`, `OrderBy` at `:1285`. All accurate.
- `ResolveProjectStageOrderAsync` at `:1588` — confirmed; already called by the stage-duration path at `:877`, not by the quality path. `GetQualityAsync` (`:459`) has `db` and `projectId` in scope — the design's approach to thread stage order into `BuildWindow` is feasible.
- `RunnerRoutes.cs:568` (`stage == "plan"`) and `:569` (`uses == "mohist/opencode"`) — confirmed; the if-block spans `:566`–`:571` as the design states.
- `EnsureArchiveDefaultAsync` lives on `WorkflowRunVariablesStore` (not `WorkflowRunProfileManager` as the issue body says) — the artifacts correctly cite `WorkflowRunVariablesStore`.
- `VariableBundle` 4-field record at `:22`–`26` — confirmed; `DefaultVars`/`DefaultStages` confirmed.
- `WorkflowRunProfileRow.DefaultVariables` at `:27`, EF config `DefaultVariables.IsRequired().HasDefaultValue("{}")` — confirmed.
- Migration `20260722000000_AddWorkflowRunProfileDefaults.cs` exists with symmetric Up/Down — confirmed.
- `BuildRebaseRecovery()` at `IssueRoutes.Helpers.cs:102` — confirmed; constructs `RecoveryDefinition` with `Uses: "mohist/opencode"` at `:121`.
- `WorkflowDefinitionParser.TopLevelKeys = { "approval", "stages" }` at `:72` — confirmed; `BuildRecovery` at `:389` — confirmed.
- `WorkflowDefinitionRules.Apply` iterates only `definition.Stages` and `definition.Approval?.Feedback` — confirmed (`:44`–`71`).
- `DefaultVars` never surfaces in web/cli — confirmed by the proposal's grep claim.

## Findings

### 1. LOW — Design Group A: contradictory claim about matching stage-duration path

Design line 34 prescribes "Append observed stages absent from the Definition, in their original (insertion) order — **not alphabetical**." Design line 38 then claims this "matches the stage-duration path's existing behavior (`IssueMetricsQuerier.cs:1055`–`1058`)." But the stage-duration path at `:1057` uses `.OrderBy(s => s, StringComparer.Ordinal)` — alphabetical. The claim is inaccurate.

The spec (`quality-stage-metrics` req 3) only says extras "SHALL be appended after the Definition-ordered stages" without mandating their internal order, so any deterministic order is acceptable. The implementer should disregard the "matching" claim and pick whichever deterministic order they prefer.

### 2. LOW — Design Group D: `WorkflowDefinitionRules` validation gap for `recoveries`

The design says to parse `recoveries` entries with the existing `BuildRecovery` logic, but `WorkflowDefinitionRules.Apply()` (`WorkflowDefinitionRules.cs:39`–`72`) only iterates `definition.Stages` and `definition.Approval?.Feedback`. Recovery templates in the new `Recoveries` dictionary would bypass the rules validator's task-ID uniqueness checks and structural validation. Since the `recoveries` entries are curated builtin content (not user-provided), the correctness risk is very low, but the design should call out that the rules validator needs a loop over `definition.Recoveries` to keep validation complete.

### 3. LOW — Tasks: web typecheck + test gate not in acceptance criteria

The issue's Done When requires "web typecheck + test 绿." The proposal correctly states web/CLI has no change, so web tests will pass without modification. However, none of the four tasks list web typecheck or web test verification in their acceptance criteria. The implementer should run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` as a final gate to satisfy the Done When.

### 4. LOW — Design Group D: rebase route profile-resolution mechanism unspecified

The design says "the rebase route resolves the run's bound profile, reads `definition.Recoveries["rebase-conflicts"]`" without specifying how. The route currently injects `WorkflowQuerier`, whose `GetDefinitionYamlAsync` returns only the simplified `WorkflowStructure` (stage IDs + approval flags), not the full `WorkflowDefinition`. The implementer will need to add a method or inject `WorkflowProfileManager`/`IssueWorkflowProfileRegistry` to access the full definition. This is a normal implementation detail but the design could be more specific.

## Consistency Check

- Specs use correct heading levels (`### Requirement` / `#### Scenario`). Every requirement has at least one scenario. No delta headers. No cross-spec references.
- Tasks map to specs via `spec` field anchors. Acceptance criteria supplement (don't duplicate) spec scenarios.
- Task dependency graph is valid: all four tasks have empty `dependsOn`, priorities are strictly ordered, no cycles. The four groups touch disjoint file sets (verified), so independence is real.
- Proposal Capabilities (4) match spec files (4) match task spec-references (4).
- Design decisions are internally consistent with specs and proposal.

## Verdict

The plan is ready to build. All four findings are LOW severity — none blocks implementation. Finding 1 is a wording inaccuracy the implementer can disregard. Findings 2 and 4 are implementation details the design under-specified but that are straightforward to resolve. Finding 3 is a missing verification step that the Done When gate already requires.

<promise>PASS</promise>
