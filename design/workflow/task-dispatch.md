# Task Dispatch

This document is the sole authority for when task input templates are evaluated. `tasks[*].with`,
task-level `expect`, and declared `artifacts` are part of the Workflow declaration. Server sends
them in their original form with the dispatch and does not expand templates in advance. Runner
expands them uniformly at the execution entry point before invoking an Action. Server-sourced
context is immutable at dispatch; Runner appends materialization facts exactly once before it
creates the final execution snapshot for an attempt.

## Rendering Boundary

Template evaluation happens only at the Runner execution entry point before an Action call, and
does **not** happen during Server dispatch:

- Server persists the original `with`, `expect`, and `artifacts` declarations with the task; it
  does not expand templates.
- Every wire dispatch carries those original declarations plus an immutable Server context
  snapshot for that attempt:
  - Effective Stage Variables, resolved at dispatch and frozen under
    [`variables.md`](variables.md);
  - Project Prompt bodies, loaded by key at dispatch;
  - runtime context: `workflow.runId`, `stage.name`, `work.*`, `issue.*`, Repository resource
    facts, `tasks.<id>.outputs.*`, and Workspace identity, but no host filesystem paths;
  - failure context, `failure.*`, for a recovery task when applicable.
- Runner materializes the bound Workspace and Repository, appends `workspace.path`,
  `repository.path`, and `repository.branch`, and freezes that augmented context as the execution
  snapshot. It then renders local inputs and artifact declarations from the original declaration.
  Rendering creates a new structure. It does **not** modify the dispatch, a persisted task
  definition, an Action `addTasks` definition, or a retry source.
- After rendering, Runner performs manifest validation, resolves `working-directory` by real path
  with ancestor-symlink confinement, and invokes the Action. The Action receives one rendered and
  validated input channel. It does **not** receive raw input, a Variables resource, or the complete
  dispatch context.

Once Runner creates an attempt's augmented execution snapshot, it remains unchanged throughout
that attempt. Later changes to Variables, Prompts, Profile Definition, a Stage overlay, or local
materialization affect only tasks not yet dispatched and later attempts, including retry, recovery
continuation, and rerun-from-stage. Rematerialization cannot continue an in-flight attempt with new
filesystem facts; it supersedes that attempt and creates a later attempt after preparation.

## Template Expression Rules

Runner applies the following evaluation rules to every attempt during rendering; dispatch does not
participate in evaluation:

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

The [Template Expressions](../../docs/workflow-definition.md#template-expressions) product
reference is authoritative for author-visible interpolation and `\${{` escaping syntax.

After rendering, `expect` remains a Workflow-owned completion contract and does not enter the
Action input channel.

## Deferred Rendering

`render: deferred` is declared on an input field in an Action manifest. Runner preserves a
deferred field unchanged, including internal `${{ ... }}`, through manifest validation and the
Action call. Fields not declared deferred are recursively expanded under the rules above,
including nested objects and arrays. An Action can read retained internal templates only from a
deferred field. No input channel exposes raw `with`, raw `expect`, a Variables resource, or the
complete dispatch context.

Runner resolves the Workspace and target Repository checkout exactly once for
each attempt. It appends those facts to the execution snapshot, then evaluates
the engine-reserved `working-directory` field, defaulting to `workspace.path`,
and provides the confined real path as `ActionContext.workDir`. An Action must
not select a directory again from runtime context; the context values are
visible template facts, not a second execution entry point.

If a persisted WorkItem's `uses` or `with` violates the selected Action's static input contract, or
if the attempt context cannot resolve a template, Runner must return the deterministic
`invalid-input` failure. A claimed TaskRun must report that failure using the exact
`workerId + workId`; poll redelivery must not retry the same deterministically invalid input.

## Prompt Body Evaluation

A Prompt body is not persisted task input. At dispatch, Server loads the body identified by
`prompts.<key>` into the attempt snapshot. During rendering, Runner evaluates `${{ ... }}` inside
the snapshotted body using the same syntax and failure semantics as `with` and `expect`. Redelivery,
retry, and rerun each reread and render from their own snapshot, so the Prompt body used by one
attempt is bound to that attempt's dispatch time.

## Effective Variables Resolution

See [`variables.md`](variables.md) for Variables resources, cross-scope merging, and dynamic
effect semantics. At dispatch, Server resolves Effective Stage Variables for the current Stage and
freezes them in the attempt snapshot. Runner does not read a Variables resource or fetch newer
values after dispatch. `vars.*` appears exactly once during an attempt: in that attempt's snapshot.

Runner expands `${{ failure.* }}` in place while constructing a recovery task because only Runner
holds the triggering task's output; see [`recovery.md`](recovery.md). Other expressions, including
unbound `vars.*` in a recovery task, remain in the original declaration and are expanded uniformly
during that attempt's rendering stage.

## Dispatch Context

The [Template Expressions](../../docs/workflow-definition.md#template-expressions) product
reference is authoritative for author-visible namespaces. This table adds only the implementation
source and evaluation timing:

| Variable | Source | Timing |
|---|---|---|
| `workflow.runId` | dispatch | fixed at dispatch |
| `stage.name` | dispatch | fixed at dispatch |
| `work.*` | dispatch; includes `id`, `type`, `title`, and `attempt` | fixed at dispatch |
| `work.approvalFeedback.*` | tasks produced only by ApprovalFeedback; includes `id`, `stage`, `createdAt`, `summary` | fixed at dispatch |
| `issue.*` | Issue context; includes `projectId`, `number`, `title`, and `body` | fixed at dispatch |
| `repository.name`, `repository.gitUrl`, `repository.baseBranch` | target Repository reference from Issue; resolved from the Project Repository resource | fixed at dispatch |
| `repository.path`, `repository.branch` | target checkout and Workflow branch produced by Runner materialization | fixed in the augmented execution snapshot |
| `workspace.name` | Workspace selected through the Issue or AgentSession binding | fixed at dispatch |
| `workspace.path` | Workspace root produced by Runner materialization | fixed in the augmented execution snapshot |
| `vars.*` | Effective Stage Variables | resolved and frozen in the attempt snapshot at dispatch |
| `tasks.<id>.outputs.*` | previous task output | fixed at dispatch |
| `prompts.<key>` | Project Prompt body, read by key | loaded into the snapshot at dispatch; evaluated during Runner rendering |
| `failure.output` | output of the task that triggered recovery; expanded when Runner constructs the recovery task | available only to recovery tasks |
| `failure.error.code` | error code of the task that triggered recovery | available only to recovery tasks |
| `failure.error.message` | actionable error text from the task that triggered recovery | available only to recovery tasks |

Runtime context, Workflow Variables, and Project Prompts are independent namespaces. Their sources
and evaluation timing remain distinct in the attempt snapshot. See
[`../runner.md`](../runner.md) for the complete dispatch and report flow.

Effective Variables appear only under `vars`; variable keys are not copied to bare top-level names.
Runtime context is neither written back to nor merged into Variables. `work.approvalFeedback` exists
only on a task produced by that feedback and is absent from ordinary tasks. An OpenSpec directory
is not runtime context. A Profile and Prompt express it directly as
`openspec/changes/issue-${{ issue.number }}`.

## Dispatch Snapshot Persistence

An attempt snapshot is the contract actually dispatched. Its content semantics are defined above;
this section defines its **storage lifecycle**:

- The Server snapshot never changes after first dispatch; the first write wins. Poll redelivery
  returns that exact original snapshot. On the owning Runner, the active attempt also retains its
  augmented materialization facts so redelivery cannot render against a different path or branch.
- The snapshot needs to be available only while the attempt is Running, after dispatch and before
  a terminal report. It expires immediately when the attempt becomes Completed, Failed, or
  Cancelled, or when a later attempt supersedes it.
- The snapshot is **not** stored as part of the complete WorkflowRun State. It is stored separately
  from run State and loaded separately when redelivery needs it. Run State contains only the facts
  required for arbitration and does not copy dispatch payloads. Historical attempt snapshots do
  not exist.
- Check attempts persist and reuse snapshots under the same rule as Task
  attempts. Reconstructing templated Check input is allowed only as a new
  attempt, never as redelivery of the same attempt.
- Reassignment after Runner loss does not reuse the old augmented snapshot. The old attempt is
  superseded, Workspace preparation completes on the replacement Runner, and a later attempt gets
  new materialization facts while retaining the same stable Repository resource properties.
- Content deduplication within a snapshot, such as referencing Prompts by key or pruning `tasks`
  output on demand, is a rendering-content optimization and does not alter these lifecycle rules.
  See [`variables.md`](variables.md).

### Status

Active Task-attempt snapshots are stored outside WorkflowRun State. The first dispatch fixes the
snapshot, redelivery reuses it, and terminal or superseding transitions remove it. Startup removes
orphaned snapshots. Check dispatch currently bypasses this store and reconstructs its input; Check
snapshot persistence and same-attempt redelivery stability above are target behavior. Arbitration
therefore remains based on current execution facts instead of payload history.

## Validation Timing

Catalog validation during Profile save or update checks only constant inputs against the Action
contract: unknown `uses`, unknown input keys, missing `required` inputs, and constant type
mismatches. For an input containing a template expression, it checks only the key name. Dispatch no
longer expands templates on Server. Runner renders expressions and then applies manifest value,
type, and required-field validation. A failure makes the attempt fail with `invalid-input`, and the
Action is not called.

If dispatch of a persisted WorkItem finds that its `uses` names a retired Action, Runner still
rejects it during dispatch. Manifest validation finds the tombstone and fails with its guidance as
a non-retryable error.

### Parent Context for a Sub-Issue Plan

When mapping internal `WorkDispatch` to an HTTP poll response, the API route may append the current
title and body of the parent Issue. It does so only when `workType = task`, `stage = plan`, `uses`
belongs to the explicit Inline Agent Action set, currently `mohist/opencode` and `mohist/pi`, and
the current Issue still has a resolvable parent. Checks, other Stages, other Actions, AgentJob, and
ordinary Issues receive no parent context.

Parent context is optional execution context in the HTTP dispatch response. It does not enter
Workflow `WorkDispatch`, WorkflowRun metadata or state, task `with`, Variables, or Prompts, and does
not add a template-expression namespace. Runner only forwards it. On each applicable execution,
the selected Inline Agent Action places the JSON-encoded parent title and body as read-only context
before the resolved task Prompt and explicitly identifies the current sub-Issue body as the
authority for delivery scope. Without parent context, the resolved Prompt remains unchanged.

Repository does not enter a WorkflowRun snapshot or Run Variables. Issue stores only the resource
name of its target repository. Dispatch uses that reference to read the Project Repository
resource. An incomplete Issue prevents changes to the target Repository's Git URL or base branch,
so every dispatch in one WorkflowRun reads stable execution properties without requiring the run
to copy them. Runner adds `repository.path` and `repository.branch` only after materialization and
never copies those values into `workspace` or `vars`. Redelivery on one active attempt reuses the
same augmented facts; rematerialization creates a later attempt as specified above. See
[`../repositories.md`](../repositories.md) for the complete rules.
