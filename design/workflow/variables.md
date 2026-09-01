# Workflow Variables

Workflow Variables are resources independent of Workflow Profile. Project, Issue,
and WorkflowRun can each store Variables. Mohist merges them in deterministic
scope and Stage order to produce Effective Variables for the current Stage.
This document defines the resource shape, merge rules, and effect timing.

See [`profile.md`](profile.md) for Profile selection and structure,
[`task-dispatch.md`](task-dispatch.md) for template namespaces, and
[`actions.md`](actions.md#setvars) for Action-output projection into Run
Variables.

## Design Drivers

- Project, Issue, and Run values use one resource shape and one merge algorithm.
- More specific scopes override less specific scopes. Stage values override
  Workflow values regardless of scope.
- Effective Variables are derived and read-only. They are never stored as a
  second source of truth.
- A dispatched attempt uses an immutable Variables snapshot. Later changes
  affect only future dispatches and new attempts.
- `setVars` writes only Run Workflow Variables. It cannot alter execution
  context or Stage Variables.

## Model

Project, Issue, and WorkflowRun Variables use the same shape:

```json
{
  "vars": { "agent": { "model": "model-a" } },
  "stages": {
    "check": { "vars": { "agent": { "variant": "variant-a" } } }
  }
}
```

- `vars` applies to all Stages.
- `stages.<stage>.vars` applies only to that Stage.
- Project Variables provide shared values for Workflows in the Project.
- Issue Variables override or add to Project Variables for one Issue.
- Run Variables store dynamic values for one WorkflowRun. A task `setVars`
  writes here.

A Workflow Profile may reference Variables, but it does not own, declare, or
restrict their keys. A Variable affects execution only when a Profile, task,
check, recovery, or Prompt references it.

The diagram abbreviates Project, Issue, and Run as `P`, `I`, and `R`; `WF` and
`ST` mean Effective Workflow and Effective Stage Variables:

```text diagram
+ Workflow merge --------------------------------+
|                                                |
|+--------+    +--------+    +--------+    +----+|
|| P.vars +--->| I.vars +--->| R.vars +--->| WF ||
|+--------+    +--------+    +--------+    +----+|
+------------------------------------------------+

+ Stage merge ------------------------------------------------+
|                                                             |
|+----+    +---------+    +---------+    +---------+    +----+|
|| WF +--->| P.stage +--->| I.stage +--->| R.stage +--->| ST ||
|+----+    +---------+    +---------+    +---------+    +----+|
+-------------------------------------------------------------+
```

Later sources override earlier sources. Effective Workflow Variables merge
`vars` in Project, Issue, Run order. Effective Stage Variables start with that
result and merge `stages.<stage>.vars` in the same order. Both results are
read-only and derived.

## Semantics

### Resolution

Resolution merges Workflow Variables first and then the current Stage Variables:

```text literal
resolve(currentStage, project, issue, run):
  result = {}

  for variables in [project, issue, run]:
    result = merge(result, variables.vars)

  effectiveWorkflowVariables = result

  if currentStage is null:
    return effectiveWorkflowVariables

  for variables in [project, issue, run]:
    result = merge(result, variables.stages[currentStage].vars)

  effectiveStageVariables = result
  return effectiveStageVariables
```

The priority order from lowest to highest is:

1. `project.vars`
2. `issue.vars`
3. `run.vars`
4. `project.stages[current].vars`
5. `issue.stages[current].vars`
6. `run.stages[current].vars`

Stage Variables are more specific than Workflow Variables from every scope.
Among Stage Variables, Run overrides Issue, and Issue overrides Project.

### Merge

An absent field inherits the existing value. An object merges recursively by
field. A scalar replaces the existing value. An array replaces the complete
array and never merges by element.

The root of `vars` and each `stages.<stage>.vars` must be an object. Merge does
not modify a source resource. A persisted Variables document cannot contain a
`null` value.

### Writes

The three Variables resources use the same methods and body semantics. The
address selects Project (`/api/projects/{projectRef}/variables`), Issue
(`/api/projects/{projectRef}/issues/{number}/variables`), or Run
(`/api/workflow-runs/{workflowRunId}/variables`) scope.

- `GET` reads the Variables stored in that scope. It does not resolve scopes.
- `PUT` replaces the scope value with a complete Variables document.
- `PATCH` deep-merges a partial Variables document into the scope. `null` is
  only a deletion instruction. It removes the field from the target scope so
  the field inherits from the preceding scope again. `null` is not persisted.

Effective Variables are a separate read-only Run resource:

```text literal
GET /api/workflow-runs/{workflowRunId}/variables/effective
GET /api/workflow-runs/{workflowRunId}/variables/effective?stage={stage}
GET /api/workflow-runs/{workflowRunId}/variables/effective/{keyPath}
```

Without `stage`, the resource returns Effective Workflow Variables. With
`stage`, it returns Effective Stage Variables.

Project and Issue settings can modify both `vars` and `stages`. Task `setVars`
is not a separate API. Runner projects Action output into a PATCH body that
contains only `vars`, then calls the Run Variables resource:

```json
{ "vars": { "change": { "prNumber": 42 } } }
```

Task `setVars` never generates a `stages` parameter, so it modifies only Run
Workflow Variables. Other callers may modify Run `stages` explicitly.

### Changes

- Each Server task dispatch resolves Effective Stage Variables for the current
  Stage and includes the result in the immutable attempt snapshot. See
  [`task-dispatch.md`](task-dispatch.md) for snapshot timing.
- The snapshot remains unchanged for the attempt lifetime. A later Variables
  change affects tasks not yet dispatched and later attempts, including retry,
  recovery continuation, and rerun-from-stage.
- A task not yet dispatched uses the latest Variables. A retry is a new dispatch
  and uses the Variables resolved at retry time.
- `setVars` runs after a successful Action and before task completion. If any
  projection fails, Run Variables remain unchanged and the task fails.

Profile-owned templates receive Effective Variables only through an explicit
`${{ vars.* }}` reference. Runner evaluates templates before calling the
Action. The Action sees rendered, validated input and cannot read Variables
again.

`workflow.*`, `stage.*`, `issue.*`, and `repository.*`, plus
`tasks.<id>.outputs.*` and `prompts.*`, are separate namespaces. They do not
participate in the Variables merge. In particular,
`workflow.verification.command` is a Project-owned startup fact. Built-in
verification reads the value frozen on WorkflowRun binding. A `ci.verify`
Variable must not override or configure that command.

Invalid Variables, an impossible `setVars`, and other semantic errors are
rejected at the write boundary. The operation returns a domain error and keeps
the original value unchanged. It must not ignore the error or expose only a
parser stack trace.

## Examples

### Scope and stage override

```yaml
stage: check

projectVariables:
  vars:
    agent: { model: model-a, variant: variant-a }
  stages:
    check:
      vars:
        agent: { variant: variant-b }

issueVariables:
  vars:
    agent: { model: model-b }
    review: { strict: true }
  stages:
    check:
      vars:
        agent: { variant: variant-c }

runVariables:
  vars:
    change: { prNumber: 42 }

effectiveWorkflowVariables:
  agent: { model: model-b, variant: variant-a }
  review: { strict: true }
  change: { prNumber: 42 }

effectiveStageVariables:
  agent: { model: model-b, variant: variant-c }
  review: { strict: true }
  change: { prNumber: 42 }
```

Run does not override `agent`. Issue Workflow Variables replace the Project
model with `model-b`, while the Project variant remains `variant-a`. Project
`check` Stage Variables select `variant-b`, and Issue Stage Variables replace it
with `variant-c`.

### Live adjustment

1. Project Variables select `model-a`, so task-1 is dispatched with `model-a`.
2. The Project value changes to `model-b`; task-1 remains unchanged.
3. Task-2 is dispatched with `model-b`.
4. A retry creates a new attempt for task-1 and uses `model-b`.

## Status

Implemented: Project, Issue, and Run Variables resources with common PUT and
PATCH semantics; `null` only as a deletion instruction and never persisted;
shape validation at the write boundary, including non-object root rejection;
dispatch carrying original declarations and an immutable attempt snapshot;
common rendering at the Runner execution entry point; and `setVars` projection
through a Run Variables PATCH.

The dispatch snapshot lifecycle is defined in
[`task-dispatch.md`](task-dispatch.md). The historical persistence-name
decision is recorded in
[`../decisions/workflow-run-profile-naming.md`](../decisions/workflow-run-profile-naming.md).
