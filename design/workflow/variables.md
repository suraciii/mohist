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
  "vars": { "agent": { "model": "gpt-5" } },
  "stages": {
    "check": { "vars": { "agent": { "variant": "high" } } }
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

```text
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

```text
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

```text
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

| Later value | Result |
|---|---|
| Field is absent | Inherit the existing value |
| object | Merge recursively by field |
| scalar | Replace the existing value |
| array | Replace the complete array; do not merge by element |

The root of `vars` and each `stages.<stage>.vars` must be an object. Merge does not modify a source
resource. A persisted Variables document does not accept a `null` value.

### Writes

The three Variables resources use the same methods and body semantics. The address determines only which
scope is modified:

| Scope | Variables resource |
|---|---|
| Project | `/api/projects/{projectRef}/variables` |
| Issue | `/api/projects/{projectRef}/issues/{number}/variables` |
| Run | `/api/workflow-runs/{workflowRunId}/variables` |

- `GET` reads the Variables stored in that scope. It does not resolve across scopes.
- `PUT` replaces the scope value with a complete Variables document.
- `PATCH` deep-merges a partial Variables document into the scope. `null` is only a deletion instruction.
  It removes the field from the target scope, so the field inherits from the preceding scope again. `null`
  is not persisted.

Effective Variables are a separate read-only resource under Run:

```text
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

Effective Variables enter task `with`, task-level `expect`, or another template-enabled declaration only
through an explicit `${{ vars.* }}` reference. Template evaluation occurs at the Runner execution entry
point before it calls the Action. It does not occur during dispatch. The Action sees only input that the
Runner has rendered and validated. It cannot read the Variables resource again.

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
    agent: { model: sonnet, variant: medium }
  stages:
    check:
      vars:
        agent: { variant: high }

issueVariables:
  vars:
    agent: { model: gpt-5 }
    review: { strict: true }
  stages:
    check:
      vars:
        agent: { variant: xhigh }

runVariables:
  vars:
    change: { prNumber: 42 }

effectiveWorkflowVariables:
  agent: { model: gpt-5, variant: medium }
  review: { strict: true }
  change: { prNumber: 42 }

effectiveStageVariables:
  agent: { model: gpt-5, variant: xhigh }
  review: { strict: true }
  change: { prNumber: 42 }
```

Merge process:

| Applied source | `agent.model` | `agent.variant` |
|---|---|---|
| Project Workflow Variables | `sonnet` | `medium` |
| Issue Workflow Variables | `gpt-5` | `medium` |
| Effective Workflow Variables | `gpt-5` | `medium` |
| Project `check` Stage Variables | `gpt-5` | `high` |
| Issue `check` Stage Variables | `gpt-5` | `xhigh` |
| Effective Stage Variables | `gpt-5` | `xhigh` |

Run does not override `agent`. The Project's `check` Stage Variables first override the `medium` value in
Effective Workflow Variables. The Issue value for the same field then overrides it with `xhigh`.

### Live adjustment

| Time | Action | Model used by task |
|---|---|---|
| 1 | The model in Project Variables is `model-a`; dispatch task-1 | `model-a` |
| 2 | Change the model in Project Variables to `model-b` | task-1 is unchanged |
| 3 | Dispatch task-2 | `model-b` |
| 4 | retry task-1 | `model-b` |

## Status

Implemented: Project, Issue, and Run Variables resources with common PUT and PATCH semantics; `null` only
as a deletion instruction and never persisted; shape validation at the write boundary, including rejection
of a non-object root; dispatch carrying only the original declarations and an immutable attempt snapshot;
common rendering at the Runner execution entry point; and task `setVars` projection through a Run Variables
PATCH.

The former open question is resolved. The "Dispatch snapshot persistence" section in
[`task-dispatch.md`](task-dispatch.md) defines attempt snapshot semantics, including immutability and
byte-for-byte replay on redelivery, and its storage lifecycle, including discard at terminal state instead
of full persistence with Run State. Audit needs do not justify retaining complete per-attempt snapshots.

## `WorkflowRunProfile` row/table name: historical misnomer

The C# row type, DbSet, and database table are deliberately named
`WorkflowRunProfileRow`, `WorkflowRunProfiles`, and `WorkflowRunProfiles`, even
though they store Variables rather than a Profile. This is a historical
misnomer, not a second domain meaning of Profile.

Decision: keep them. The cosmetic rename would require an EF Core
migration rewriting a live production table plus coordinated down/up scripts, for
zero behavioral gain. This decision keeps the misnomer explicit instead of
letting readers infer a second responsibility. When the table is next
restructured for a behavioral reason, such as normalization or archival, rename
the row and DbSet in the same change.

The `Run-scoped Variables` table/row rename is rejected **only** on cost/benefit
grounds; the rename is correct in target. The current persisted
`VariableBundle` JSON shape and ETag behavior are not affected by the type-name
keep and remain unchanged.
