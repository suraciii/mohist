## Why

Coder Agent Tab lets users pick a model, but the opencode runner ignores the selection (see exploration report). The root cause is a shape mismatch: `ProjectWorkflowProfile` stores agent config inside the generic `Variables` bundle (where the UI writes), while `IssueWorkflowProfile` stores it in special-purpose `AgentConfig` / `StageAgentConfigs` fields that the runner never reads. Issue creation (T1) then has to special-case agent merging, and `BuildVariables` re-merges at runtime — every layer hard-codes knowledge of one variable name. We fix it now because the asymmetry is the source of the bug and #113 only patches the symptom.

## What Changes

- **BREAKING** (interface) Remove the agent-specific special-casing from the issue workflow profile surface: the dead `IIssueWorkflowProfile.BuildVariables(globalAgentConfig)` / `BuildStageVariables(globalStageAgentConfigs)` methods and their private helpers. Both `IssueWorkflowProfile` and `ProjectWorkflowProfile` SHALL store agent config exclusively inside the shared `VariableBundle` type (`vars` + `stages`), with no field-name special-casing. (Both DB rows already store only `Variables`, so no storage-field removal is needed; `AgentConfig` / `Model` / `StageModels` on the read model remain as derived projections.)
- `agent` becomes an ordinary variable key across all three layers: global `config.jsonc` (`vars.agent`, `stages` always empty), `ProjectWorkflowProfile.Variables`, and `IssueWorkflowProfile.Variables`.
- Replace the agent-specific T1 merge at issue start (`IssueGrain.StartWorkflowAsync`) with a single generic `MergeVariables(projectVars, globalVars)` (reusing `VariableBundle.MergeAll`). The merge is symmetric: `vars` and each `stages[X].vars` use the same project-priority / global-fallback pattern.
- `BuildVariables` SHALL return the already-merged `issueWorkflowProfile.Variables` directly and add context vars (`mohist`, `issue`, `project`, …). It SHALL NOT perform runtime agent merging.
- Stage agent dispatch SHALL read `issue.Variables.Stages[stage]?.Vars?.Agent` falling back to `issue.Variables.Vars.Agent` — ordinary variable lookups, no cross-layer resolution.
- `config.jsonc` is expressed in memory as a `VariableBundle` (`vars.agent` + empty `stages`), since stage names are project-specific and cannot be configured globally.
- No frontend/API payload change is required: the issue-level `PATCH /issues/{n}/workflow-profile/variables` endpoint already takes a generic `VariableBundle`, and the web client already writes `vars.agent` / `stages.X.vars.agent`. (The separate `POST/PATCH /api/issues` issue-entity `model` / `stageModels` fields are a different, out-of-scope path.)
- One-way data migration is expected to be a no-op verification: persistence is already unified, so existing `IssueWorkflowProfile` rows already carry agent config at `Variables.vars.agent` / `Variables.stages`. The change adds a storage-integrity verification with a reversible defensive-copy fallback in case any row is found with agent data outside `Variables`.

## Capabilities

### New Capabilities

(None — the change removes special-casing and unifies into the existing `VariableBundle` concept already owned by `workflow-config`.)

### Modified Capabilities

- `workflow-config`: `IssueWorkflowProfile` loses its `AgentConfig` / `StageAgentConfigs` special fields and SHALL use the same `VariableBundle` type as `ProjectWorkflowProfile`. Adds the generic 3-layer (global → project → issue) Variables merge performed at issue creation: project values win, global fills gaps, and the merge pattern is identical for top-level `vars` and each `stages[X].vars`. `config.jsonc` is exposed in memory as a `VariableBundle` with empty `stages`.
- `workflow-engine`: Model resolution no longer runs a runtime fallback chain in `BuildVariables`. The effective agent model is fixed at issue creation by the T1 Variables merge; `BuildVariables` returns the pre-merged bundle directly, and per-stage agent dispatch reads ordinary variable keys from `issue.Variables.Stages[stage]` / `issue.Variables.Vars`.

## Impact

- `packages/server/.../Issue/Services/WorkflowProfiles/IIssueWorkflowProfile.cs` + `MohistDefaultIssueWorkflowProfile.cs` — remove the dead agent-specific methods (`BuildVariables(globalAgentConfig)` / `BuildStageVariables(globalStageAgentConfigs)`) and their private helpers.
- `packages/server/.../Infrastructure/Config/ConfigService.cs` — add `GetVariables()` exposing global `config.jsonc` as a `VariableBundle` (`vars.agent`, empty `stages`).
- `packages/server/.../Issue/Grains/IssueGrain.cs` (`StartWorkflowAsync` / `BuildIssueVariables`) — fold global + project `VariableBundle`s into the issue `Variables` at T1, plus context vars.
- `packages/server/.../Workflow/Services/WorkflowProfileManager.cs` (`LoadVariablesAsync`) — read the issue layer directly instead of re-merging the project layer at runtime.
- `packages/server/.../Issue/Services/IssueQuerier.cs` (`ApplyIssueWorkflowVariables`) — unchanged in behavior; `AgentConfig` / `Model` / `StageModels` remain derived projections of the (now T1-merged) bundle.
- No DB schema change; `IssueWorkflowProfileRow` / `ProjectWorkflowProfileRow` already store only `Variables`. No frontend change; `entities/issue/api/client.ts` already writes `vars.agent` / `stages.X.vars.agent` to the already-generic `workflow-profile/variables` endpoint.
- Storage-integrity verification (no data move expected) with a reversible defensive-copy fallback, for existing `IssueWorkflowProfile` rows.
- Tests: add specs asserting symmetric merge of `vars`/`stages`, project-over-global precedence, no runtime merge, and stage dispatch from `issue.Variables.Stages`; keep `ConfigServiceSpecs` / `VariableScopeSpecs` / `MohistDefaultWorkflowProfileSpecs` green.
- No external dependencies change; no new packages.
