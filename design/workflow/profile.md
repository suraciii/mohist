# Workflow Profile

profile = **template** (which `WorkflowDefinition`) + **variables** (`VariableBundle`).

Prompts NOT in profile → `prompt-management.md`.
Action input/output → `actions.md`. Builtin workflows → `builtin-workflows/`.

## Architecture

```
Issue → Workflow (one-way)
WorkflowGrain → IWorkflowProfileProvider (port, Workflow-only types)
                    ▲
            WorkflowProfileProvider (adapter, live reads config)
                    │
    global · project · issue · run profiles · project templates
```

- Workflow zero Issue dependency. Resolution in adapter, live (no snapshot).
- `WorkflowRun` stores execution state + profile identity. No profile body. No `RuntimeVariables`.
- `TaskRun.Output` = `JsonElement?` (matches `WithInput`).

## VariableBundle

`{ vars, stages: { "plan": { vars } } }`. Set = replace. Patch = deep merge.

## Profile layers

```
project_workflow_profile    ProjectId, DefaultTemplateId, Variables
issue_workflow_profile      IssueId, SourceTemplateId, Template, Variables
workflow_run_profile        WorkflowRunId, Variables
```

Run profile = highest priority. Stores runtime facts (`vars.change.id`, etc.). Not part of `WorkflowRun` aggregate — linked by `WorkflowRunId`, profile service reads/writes.

## Merge

Three-phase merge. Template lane: fallback selection, no deep merge. Variable lane: layered deep merge. Stage overlay: final deep merge of stage variables.

```
Template lane (fallback, no deep merge):
  issue custom template? → else issue source template? → else project default? → else system default
  → CurrentTemplateVariables

Variable lane (layered deep merge):
  global → project → issue → run
  → ProfileVariables

Effective:
  deepMerge(CurrentTemplateVariables, ProfileVariables) → WorkflowEffectiveVariables

Stage:
  deepMerge(WorkflowEffectiveVariables.vars, WorkflowEffectiveVariables.stages[stage].vars)
  → WorkflowStageEffectiveVariables
```

Deep merge: recursive object, latter wins on conflict. `null` values in storage layer are ignored (no null-overwrite semantic).

## ExpandTaskWith

```
for (k,v) in taskWith:
  "${{...}}" whole-string → vars replace, else keep (runner expands)
  object && k∈vars → deepMerge (vars overwrite)
  else → preserve
```

Whole-value expansion preserves the resolved JSON type. This is how Workflow variables
select OpenCode options without creating a second configuration path:

```yaml
variables:
  agent:
    model:
      providerID: anthropic
      id: claude-sonnet-4

tasks:
  - uses: mohist/opencode
    with:
      prompt: ${{ prompts.proposal }}
      options: ${{ vars.agent }}
```

After expansion, `options` is an object in Action Input. The action must not read the
effective variable bundle or merge `vars.agent` again.

Task-level `expect` uses the same template lookup rules but is expanded separately. It never
deep-merges into `with` and never becomes Action Input.

## Runtime writes: setVars

Task success → action output projected to run profile via `setVars`:

```yaml
setVars:
  change.id: output.changeId
```

- Left = path under `vars`. Right = JSON path in action output.
- Only patches `workflow_run_profile.Variables`. Never project/issue profile. Never `WorkflowRun` execution state.
- Runner executes: extracts from output → `PATCH /api/workflow-runs/{id}/workflow-profile/variables` → then reports task complete. Failure = task failed.

## Read API

```
GET /workflow-runs/:id/variables/effective           → WorkflowEffectiveVariables.vars
GET /workflow-runs/:id/variables/effective?stage=X   → WorkflowStageEffectiveVariables
GET /workflow-runs/:id/variables/effective/:keyPath  → value at keyPath
```

## Write API

```
system templates    GET /workflow-templates/system
project templates   /projects/:p/workflow-templates
project profile     /projects/:p/workflow-profile
issue profile       /projects/:p/issues/:n/workflow-profile
run profile         /workflow-runs/:id/workflow-profile
effective           /workflow-runs/:id (/yaml, /variables/effective)
```
