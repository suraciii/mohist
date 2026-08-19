---
status: implemented
---

# Workflow Variables

Workflow Variables are resources that are independent of WorkflowProfile. Project, Issue, and
WorkflowRun can each store Variables. The system merges them in a deterministic order and produces
Effective Variables for the current Stage.

This document defines only the Variable resource shape, merge rules, and effect timing. See
[`profile.md`](profile.md) for Profile selection and structure,
[`task-dispatch.md`](task-dispatch.md) for template namespaces, and
[`actions.md`](actions.md#setvars) for projection from Action output to Run Variables.

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

- `vars` contains Workflow Variables that apply to all Stages.
- `stages.<stage>.vars` applies only to the specified Stage.
- Project Variables provide shared values for Workflows in the Project.
- Issue Variables override or add to Project Variables for one Issue.
- Run Variables store dynamic values for one WorkflowRun. A task `setVars` writes here.

WorkflowProfile can reference a Variable, but it does not own, declare, or restrict Variable keys. A
Variable affects execution only when a Profile, task, check, recovery, or Prompt references it.

```text diagram
Workflow merge:
  Project.vars -> Issue.vars -> Run.vars -> Effective Workflow Variables

Stage merge:
  Effective Workflow Variables
    -> Project.stages[current].vars
    -> Issue.stages[current].vars
    -> Run.stages[current].vars
    -> Effective Stage Variables

Later sources override earlier sources.
Both Effective Variable results are read-only, derived, and not stored.
```

- **Effective Workflow Variables:** The Stage-independent result after merging `vars` in Project,
  Issue, and Run order.
- **Effective Stage Variables:** The result after starting with Effective Workflow Variables and then
  merging the current Stage's `stages.<stage>.vars` in Project, Issue, and Run order.

Both kinds of Effective Variables are read-only derived values. They are not persisted separately.

## Semantics

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

The complete priority order, from lowest to highest, is:

```text diagram
project.vars
-> issue.vars
-> run.vars
-> Effective Workflow Variables
-> project.stages[current].vars
-> issue.stages[current].vars
-> run.stages[current].vars
-> Effective Stage Variables
```

Variables for the current Stage are more specific than Workflow Variables from any scope. Therefore, they
always apply after Effective Workflow Variables. Among Stage Variables, Run overrides Issue, and Issue
overrides Project.

### Merge

An absent field inherits the existing value. An object merges recursively by
field. A scalar replaces the existing value. An array replaces the complete
array and never merges by element.

The root of `vars` and each `stages.<stage>.vars` must be an object. Merge does not modify a source
resource. A persisted Variables document does not accept a `null` value.

### Writes

The three Variables resources use the same methods and body semantics. The
address selects Project (`/api/projects/{projectRef}/variables`), Issue
(`/api/projects/{projectRef}/issues/{number}/variables`), or Run
(`/api/workflow-runs/{workflowRunId}/variables`) scope.

- `GET` reads the Variables stored in that scope. It does not resolve across scopes.
- `PUT` replaces the scope value with a complete Variables document.
- `PATCH` deep-merges a partial Variables document into the scope. `null` is only a deletion instruction.
  It removes the field from the target scope, so the field inherits from the preceding scope again. `null`
  is not persisted.

Effective Variables are a separate read-only resource under Run:

```text literal
GET /api/workflow-runs/{workflowRunId}/variables/effective
GET /api/workflow-runs/{workflowRunId}/variables/effective?stage={stage}
GET /api/workflow-runs/{workflowRunId}/variables/effective/{keyPath}
```

Without `stage`, the resource returns Effective Workflow Variables. With `stage`, it returns Effective
Stage Variables.

The Project and Issue settings surfaces can modify both `vars` and `stages`. Task `setVars` is not a
separate API. The Runner projects Action output into a PATCH body that contains only `vars`, then calls the
Run Variables resource:

```json
{ "vars": { "change": { "prNumber": 42 } } }
```

Task `setVars` does not generate a `stages` parameter, so it modifies only the Run's Workflow Variables.
The Run Variables resource still lets other callers modify `stages` explicitly.

### Changes

- Each time the Server dispatches a task, it resolves Effective Stage Variables for the current Stage and
  sends the result with dispatch as part of the immutable attempt snapshot. See
  [`task-dispatch.md`](task-dispatch.md) for attempt snapshot semantics and evaluation timing.
- After an attempt is dispatched, its snapshot remains unchanged for the lifetime of that attempt. A later
  Variables change affects only tasks that are not yet dispatched and later attempts, including retry,
  recovery continuation, and rerun-from-stage. A dispatched attempt does not read the latest Variables.
- A task that is not yet dispatched uses the latest Variables. A retry is a new dispatch, so it carries the
  Effective Stage Variables from the retry time.
- Task `setVars` runs after the Action returns successfully and before the task reports completion. If any
  output projection fails, Run Variables remain unchanged and the task fails.

Profile-owned template declarations receive Effective Variables only through an explicit
`${{ vars.* }}` reference. A manifest-owned engine input is separate: Runner derives that declared
Action input from the immutable dispatch snapshot under
[`task-dispatch.md`](task-dispatch.md#engine-sourced-action-inputs), without adding a profile
reference. Template evaluation occurs at the Runner execution entry point before it calls the
Action. The Action sees only input that Runner has rendered and validated; it cannot read the
Variables resource again.

Runtime context such as `workflow.*`, `stage.*`, `issue.*`, and `repository.*`, plus
`tasks.<id>.outputs.*` and `prompts.*`, are separate namespaces. They do not participate in the Variables
merge.

Invalid Variables, an impossible `setVars`, and other semantic errors must be rejected at the write
boundary. The operation must return a domain error and keep the original value unchanged. It must not
silently ignore the error or expose only a parser stack trace.

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
model with `model-b`, while the Project variant remains `variant-a`. The
Project's `check` Stage Variables then select `variant-b`, and the Issue Stage
Variables replace it with `variant-c`.

### Live adjustment

1. Project Variables select `model-a`, so task-1 is dispatched with `model-a`.
2. The Project value changes to `model-b`; task-1 remains unchanged.
3. Task-2 is dispatched with `model-b`.
4. A retry creates a new attempt for task-1 and uses `model-b`.

## Status

Implemented: Project, Issue, and Run Variables resources with common PUT and PATCH semantics; `null` only
as a deletion instruction and never persisted; shape validation at the write boundary, including rejection
of a non-object root; dispatch carrying only the original declarations and an immutable attempt snapshot;
common rendering at the Runner execution entry point; and task `setVars` projection through a Run Variables
PATCH.

The dispatch snapshot lifecycle is defined in
[`task-dispatch.md`](task-dispatch.md). The historical persistence-name decision
is recorded in
[`../decisions/workflow-run-profile-naming.md`](../decisions/workflow-run-profile-naming.md).
