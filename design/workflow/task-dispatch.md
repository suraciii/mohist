# Task Dispatch

Task dispatch is Workflow-owned work. `mohist/agent` tasks enter the durable AgentJob launch boundary described in
[`../agent-execution.md`](../agent-execution.md).

This document is the sole authority for evaluating task input templates. `tasks[*].with` and task-level `expect`
remain Workflow declarations. Server dispatches them unchanged. Runner evaluates them once at the execution
entry point against an immutable attempt snapshot. Runner never receives a Profile template expression:
`uses` is a literal Action name before dispatch.

## Design Drivers

- Server must preserve the authored Workflow declaration. It must not freeze rendered values into a TaskRun or
  dispatch payload.
- One Runner entry point must apply the same rendering and validation rules to ordinary, redelivered, retried,
  recovery, and rerun attempts.
- An attempt must use one immutable snapshot. Later Variable or Prompt changes affect only work that has not
  been dispatched.
- Actions receive one rendered, validated input channel. They do not receive raw declarations, resources, or
  the complete dispatch context.

## Model

A dispatch contains the original `with` and `expect` declarations plus a context snapshot for that
attempt. The snapshot contains Effective Stage Variables, Prompt bodies loaded by key, fixed Workflow and
runtime facts, and recovery failure facts when applicable.

The Prompt body, Effective Stage Variables, runtime context, and failure context are immutable for the
attempt. Runner creates a new rendered structure before manifest validation and the Action call. It never
mutates the original declaration, persisted task definition, `addTasks` definition, or retry source.

### Rendering Boundary

Server persists original `with` and `expect` declarations with the task. It does not expand templates.
Every wire dispatch carries those declarations and an immutable snapshot containing:

- Effective Stage Variables, resolved at dispatch and frozen under [`variables.md`](variables.md).
- Project Prompt bodies, loaded by key at dispatch.
- Runtime facts: `workflow.runId`, `workflow.verification.command`, `stage.name`, `work.*`, `issue.*`, `repository.*`, `tasks.<id>.outputs.*`, and `workspace.*`.
- `failure.*` facts for a recovery task.

Before manifest validation and the Action call, Runner renders local inputs from the original `with` and
`expect` against that snapshot. Runner then validates the manifest, resolves `working-directory`, and invokes the
Action. The Action receives only rendered and validated input. No input channel exposes raw `with`, raw
`expect`, a Variables resource, or the complete dispatch context. `expect` remains a Workflow-owned
completion contract and does not enter the Action input channel.

```text diagram
+-----------------+    +---------------+    +-------------------+    +--------+
| Server dispatch +--->| Runner render +--->| Manifest validate +--->| Action |
+-----------------+    +---------------+    +-------------------+    +--------+
```

Once dispatched, the snapshot does not change. Later Variable or Prompt changes affect only Tasks not yet
dispatched and later attempts, including retry, recovery continuation, and `rerun-from-stage`. A Profile edit affects
only future WorkflowRuns because the current Run uses its complete bound Definition.

## Semantics

### Template Expression Rules

Runner applies these rules to every attempt during rendering. Server dispatch does not evaluate expressions:

```text literal
${{ path }} occupies the whole value  -> replace it and preserve the JSON type
another resolvable expression         -> replace it from dispatch context
${{ prompts.<key> }}                  -> body was loaded by Project Prompt key at dispatch;
                                        evaluate it with the same syntax as `with` / `expect`
expression embedded in a string       -> convert the value to text and interpolate it;
                                        unresolved or object/array value -> task fails
any unresolved whole expression       -> task fails
ordinary value                         -> preserve it unchanged
```

The [Template Expressions](../../docs/workflow-definition.md#template-expressions) product reference is
authoritative for author-visible interpolation and `\${{` escaping.

### Deferred Rendering

`render: deferred` is declared on an input field in an Action manifest. Runner preserves a deferred field unchanged,
including internal runtime `${{ ... }}`, through manifest validation and the Action call. Fields without that
declaration are recursively expanded, including nested objects and arrays. An Action can read retained
internal templates only from a deferred field.

Runner resolves the Workspace exactly once for each WorkItem and provides it as `ActionContext.workDir`. An Action must not
select another directory from `variables.workspace.path`. That value is dispatch context, not a second execution entry point.

If a persisted WorkItem violates the selected Action's static `uses` or `with` contract, or if the
attempt snapshot cannot resolve a template, Runner returns deterministic `invalid-input`. The claimed TaskRun
reports that failure with the exact `workerId` and `workId`. Poll redelivery must not retry the same
deterministically invalid input.

### Prompt Body Evaluation

A Prompt body is not persisted task input. At dispatch, Server loads the body identified by `prompts.<key>` into the
snapshot. Runner evaluates `${{ ... }}` inside that body during rendering with the same syntax and failure rules
as `with` and `expect`.

Redelivery, retry, and rerun each use their own dispatch snapshot. The Prompt body for an attempt is therefore
bound to its dispatch time.

### Effective Variables Resolution

[`variables.md`](variables.md) defines Variables resources, cross-scope merging, and dynamic effects. Server
resolves Effective Stage Variables at dispatch and freezes them in the snapshot. Runner does not read a
Variables resource or fetch newer values after dispatch. `vars.*` appears exactly once during an attempt, in
that snapshot.

Runner expands `${{ failure.* }}` while constructing a recovery task because it holds the triggering task's output.
Other expressions, including unbound `vars.*` in that task, remain in the original declaration and are
expanded during the new attempt's rendering. See [`recovery.md`](recovery.md).

### Dispatch Context

The [Template Expressions](../../docs/workflow-definition.md#template-expressions) product reference defines
author-visible namespaces. Dispatch fixes `workflow.runId`, `stage.name`, `work.*`, `issue.*`, `repository.*`, and prior
`tasks.<id>.outputs.*` facts. Tasks produced by Approval Feedback also receive `work.approvalFeedback.*` facts.

Effective Stage Variables under `vars.*` and Project Prompt bodies under `prompts.<key>` enter the snapshot at
dispatch. Runner evaluates Prompt bodies during rendering and resolves `workspace.*` at the execution entry point.
Only recovery tasks receive `failure.output`, `failure.error.code`, and `failure.error.message`.

Runtime context, Workflow Variables, and Project Prompts are independent namespaces with distinct sources and
timing. Effective Variables appear only under `vars`; keys are not copied to bare top-level names. Runtime
context is not written to or merged into Variables. `work.approvalFeedback` exists only on a task produced by that feedback
and is absent from ordinary tasks. Plan-artifact paths are not runtime context. A Profile and Prompt express
them directly, for example `PLANS/PLAN.md`. See [`../runner.md`](../runner.md) for the complete dispatch and report flow.

### Dispatch Snapshot Persistence

An attempt snapshot is the contract actually dispatched. Its lifecycle is:

- The first write wins. Poll redelivery returns the exact original snapshot without rendering it again.
- The snapshot exists only while the attempt is Running, after dispatch and before a terminal report. It
  expires when the attempt becomes Completed, Failed, or Cancelled, or when a later attempt supersedes it.
- The snapshot is separate from WorkflowRun State. State contains arbitration facts and does not copy dispatch
  payloads. Historical attempt snapshots do not exist.
- A check dispatch does not persist a snapshot. Redelivery reconstructs it through the ordinary translation
  boundary.
- Content deduplication inside a snapshot, such as Prompt keys or on-demand task-output pruning, may reduce
  rendering content without changing this lifecycle. See [`variables.md`](variables.md).

### Validation Timing

Catalog validation during Profile save or update checks constant inputs against the Action contract: unknown
`uses`, unknown input keys, missing required inputs, and constant type mismatches. For a template
expression, it checks only the key name. Server dispatch does not expand templates.

Runner renders expressions and then applies manifest value, type, and required-field validation. A failure
returns `invalid-input` and does not call the Action.

If a persisted WorkItem names a retired Action, Runner rejects it during dispatch. Manifest validation finds
the tombstone and returns its guidance as a non-retryable error.

### Parent Context for a Child-Issue Plan

A child-Issue Plan `mohist/agent` task may receive the current parent Issue title and body as optional read-only
context. Other Stages, Actions, AgentJobs, and ordinary Issues do not receive it. Parent context is not
persisted in WorkflowRun State, task input, Variables, or Prompts, and it creates no template namespace. The
current child-Issue body remains authoritative for delivery scope.

WorkflowRun stores its bound Repository snapshot and write-once Pull Request identity. Neither is a Run
Variable. The Issue's Repository binding supplies the snapshot at start, and dispatch uses that stable
Repository context. The first `github.pr.number` carrier through the Workflow grain records Pull Request identity. The
same number is accepted again. A conflicting number is rejected. See [`../repositories.md`](../repositories.md).

## Status

Active attempt snapshots are stored outside WorkflowRun State. The first dispatch fixes the snapshot,
redelivery reuses it, and terminal or superseding transitions remove it. Startup removes orphaned snapshots.
Arbitration therefore depends on current execution facts instead of payload history.
