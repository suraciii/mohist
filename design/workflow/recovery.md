# Task Recovery

After task execution, the Runner executor matches `when` expressions against the
`{ output, error }` result context, builds recovery tasks, and returns them through `addTasks` for
mechanical insertion by the engine. Recovery is part of how a task completes, not remediation only
after failure. Explicit matching is independent of task success or failure: successful output that
matches `when: output.promise=FAIL` also triggers recovery. The default handler, which omits `when`,
handles only results that contain an error, including final failures produced by the executor after
the Action completes.

See the product reference for author-visible syntax and semantics, including budget, first match,
`retrySelf`, and manual retry opening a new round:
[`recovery`](../../docs/workflow-definition.md#recovery-failure-recovery). This document defines the
execution mechanism.

## Responsibilities

- The engine remains generic. It understands only Stage, task, check, completed, and failed; it
  never understands recovery. To the engine, `recovery` is an opaque task attribute.
- Recovery configuration remains read-only from YAML through Runner. Remaining budget is
  per-attempt execution state outside configuration, stored in `recoveryRemaining`, not a modified
  copy of the configuration.
- Matching happens in the Runner executor. An explicit `when` matches any path in result context;
  the final handler without `when` matches only when an error exists. An Action has no recovery
  awareness.
- Recovery tasks are real Workflow tasks and appear in the graph, timeline, and state.
- The task that triggers recovery ends as completed because it produced later work.
- When Runner constructs a handler task, it expands only `${{ failure.* }}` references bound to the
  triggering attempt. Other expressions remain in the new task declaration; see
  [`task-dispatch.md`](task-dispatch.md).
- `retrySelf` must copy the triggering attempt's original dispatch declaration, not this Action
  execution's rendered input. It deep-copies `with`, task-level `expect`, artifacts, `setVars`,
  recovery configuration, and task identity. Only `recoveryRemaining` is decremented as separate
  state. A later dispatch can therefore expand `${{ vars.* }}` against its own Variable snapshot,
  rather than freezing values resolved by the triggering attempt into the retry.
- A new attempt expands its declaration against its own context snapshot.

| Layer | Responsibility |
|---|---|
| Workflow YAML | Declares `budget` and `handlers`, with optional `when`, `tasks`, and `retrySelf` |
| Action | Returns output or an error and has no recovery awareness |
| Runner executor | Matches explicit `when` first, then the default handler when an error exists; maps explicit `null` to the full `budget`, clamps a numeric value to the declared range, and builds `addTasks` from the original declaration |
| Engine | Mechanically inserts `addTasks`, passes `recoveryRemaining` as opaque per-attempt state, and reconstructs a manual retry only from definitional fields |

## Remaining Budget (`recoveryRemaining`)

The `recovery` configuration remains read-only. The number of recoveries left in the current round
is execution state outside configuration and flows with the task in `recoveryRemaining`:

```text diagram
YAML budget: 2 --> TaskRun ------------> WorkItem / dispatch --> Runner tryRecovery
                   Recovery (read-only)                          null -> budget
                   RecoveryRemaining                                  |
                         ^                                             | match and remaining > 0
                         |                                             |
                         +-- RuntimeTaskInput <-- addTasks <------------+
                             retrySelf: original declaration,
                             recoveryRemaining = remaining - 1
```

- Runner `tryRecovery` is the sole read/write authority. The engine only passes this field through
  and never reads its value.
- Explicit `null` starts a new round and receives the full `budget`. An absent field is malformed
  transport and receives ordinary-result handling; it must not reopen the budget.
- On the engine side, `recoveryRemaining` is not part of a task definition. During addTask intake,
  it passes to `TaskRun` as side-channel state, following the `causedByFeedbackId` precedent, and
  never enters `TaskDefinition`.

## Manual Retry Opens a New Round

Invariant: **a budget bounds one continuous round of automatic recovery; a manual retry opens a
new round with the full budget.**

A manual retry reconstructs the Task from its original declaration rather than from the previous
attempt's resolved input. This boundary lets corrected Variables and Prompts take effect without
turning execution output into future configuration. `with` and `expect` therefore remain Workflow
expressions, while execution state such as `recoveryRemaining` cannot enter the new declaration.
The new attempt starts with `recoveryRemaining = null`, so Runner opens a fresh round from the
declared `budget`. The failed attempt and its consumed budget remain unchanged for audit.

## A Stage Rerun Does Not Reuse TaskRun Identity

A `TaskRun` execution identity consists of Definition ID, Stage attempt, and task attempt. The first
Stage attempt retains the existing `{definitionId}.{taskAttempt}` format. Starting with the second,
it uses `{definitionId}.s{stageAttempt}.{taskAttempt}`. For example, the first build task is
`T-001.1`, a manual retry in the same Stage is `T-001.2`, and the first task after rerunning build is
`T-001.s2.1`. Definition IDs remain scoped to a Stage, so another Stage may use the same ID. If its
candidate TaskRun ID already exists in the WorkflowRun, the allocator appends the first available
`.runN` suffix. This preserves established IDs when there is no collision while keeping every
persisted TaskRun ID and Work ID unique within the run.

`rerun-from-stage` discards visible task history from the old Stage, but it cannot decrease the
Stage attempt or let a new TaskRun reuse an old identity. A Workflow AgentSession whose default
name is the Work ID is therefore always a new logical Session and cannot inherit the invalidated
attempt's physical binding or working directory. An explicit `session` name continues to define
its own reuse semantics through the Workflow Definition.

## Runtime Binding Repair Is Not Workflow Recovery

Before submitting new independent input, AgentSession can determine that its current Runtime
Session is confirmed missing and repair the physical binding under
[`agent-execution.md`](../agent-execution.md#runtime-session-missing-recovery). This happens before
the Action produces either a successful or failed result. When repair succeeds, the original
TaskRun attempt continues without creating a recovery task, decrementing `recoveryRemaining`, or
requiring manual Retry. Only if repair fails does the Action pass a normalized error to the recovery
and manual retry semantics in this document.

WorkflowRun must neither decide nor implement Runtime binding repair. Runner reports Runtime facts,
Session arbitrates and persists the binding, and Workflow interprets only the final Action result.

## Runner Executor Flow

```text literal
result = action.execute()
context = { output: parseJSON(result.output), error: result.error }
if recoveryRemaining is absent:
    return ordinary result
remaining = recoveryRemaining is null
    ? recovery.budget
    : clamp(recoveryRemaining, 0, recovery.budget)
handler = recovery.handlers.find(h => h.when && matchesWhen(h.when, context))
    ?? (result.error ? recovery.handlers.find(h => h.when is absent) : null)

if handler && remaining > 0:
    handlerTasks = bindFailureReferences(handler.tasks, context)
    selfRetry = copyOriginalDeclaration(work)
    addTasks = handlerTasks with their own full recoveryRemaining
        + (retrySelf ? selfRetry with recoveryRemaining = remaining - 1 : [])
    return completed + addTasks

if result.error is absent:
    return completed
return failed
```

At most one default handler exists and it must be last, so it cannot shadow an explicit match. It
matches after the executor has formed the final failed result, which lets the same recovery path
handle failures found after an Action, such as a dirty workspace or invalid branch. A negative
value clamps to 0 and a value above the declared limit clamps to that limit. A matched handler
consumes one unit of budget; an unmatched result consumes none. The declaration is never modified.

A recovery handler template can read `${{ failure.output.* }}`, `${{ failure.error.code }}`, and
`${{ failure.error.message }}`. The message exists only to carry an actionable error into a
recovery task. A handler must not branch on the message.

## WorkResult

```json
{
  "status": "completed",
  "addTasks": [
    { "id": "recover:rebase", "uses": "mohist/rebase", "with": {...} },
    { "id": "merge-pr", "uses": "mohist/merge-github-pr", "with": {"options": "${{ vars.agent }}"}, "recovery": {"budget": 2, ...}, "recoveryRemaining": 1 }
  ]
}
```

- `completed` with `addTasks`: the engine inserts the tasks into the current Stage.
- `completed` without `addTasks`: normal completion.
- `failed`: Workflow fails.

## Engine Behavior

```text diagram
result.completed
  -> mark task completed
  -> addTasks non-empty? -> AddRuntimeTaskAttempts
  -> Advance

result.failed
  -> mark task failed -> Stage failed -> Workflow failed
```

## Status

Implemented in issue #465: both `retrySelf` and manual retry reconstruct the original declaration.
`with` and `expect` retain Workflow expressions, a new attempt expands them against its own context
snapshot, and `recoveryRemaining` is separate execution state that cannot enter reconstruction by
construction.

## One-Off Task Injection Uses the Same Recovery Mechanism as a Profile

An API-triggered rebase task carries a `RecoveryDefinition` and submits it to the engine under the
semantics in this document: budget, handlers, `when` matching, `retrySelf`, and the remaining-budget
behavior of manual retry. It has no behavioral or semantic difference from inline `task.recovery`
in a Profile; only the trigger differs, API route versus Runner executor. One mechanism is enough.
API-injected recovery remains a `RecoveryDefinition`, Runner still performs matching and decrements
remaining budget, and `addTasks` still follows the engine insertion path.

This rules out a separate representation for one-off injection with different budget semantics,
handler matching, or namespaces. Such a representation would duplicate every field defined here
without adding a requirement.

## Top-Level `recoveries`: Named Recovery Templates

One-off injection references a named recovery Definition from the Profile, rather than hard-coding
recovery content in an API route. The top-level `recoveries` key in Workflow YAML owns named
recovery templates:

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

- The `recoveries` map is part of WorkflowDefinition and round-trips with the rest of the Profile.
- A recovery trigger selects a named template from the Profile bound to the run. It does not build a
  second recovery definition in application code.
- Workflow content is the single author of recovery `uses`, Prompt references, budget, and handler
  order. Keeping application code limited to name selection lets a Profile upgrade change behavior
  in one place and prevents a copied recovery path from drifting to the wrong Prompt.
- Naming convention: template names are lowercase and hyphen-separated, such as
  `rebase-conflicts` and `plan-conflicts`. Each built-in YAML file declares the templates it needs.
  Extract cross-Profile sharing into a separate file only after a third Profile or a second shared
  template appears.
