# Task Recovery

Workflow recovery chooses which follow-up Action to schedule. AgentJob owns physical Agent execution recovery.
A recovery `mohist/agent` Action creates a new AgentJob.

The Runner executor matches `when` expressions against the `{ output, error }` result context, builds recovery tasks,
and returns them through `addTasks` for mechanical insertion by the engine. Recovery is part of task
completion, not only failure remediation. Explicit matching is independent of task success or failure:
successful output that matches `when: output.promise=FAIL` also triggers recovery. The default handler, which omits `when`,
handles only results with an error, including final failures produced after the Action completes.

Author-visible syntax and semantics, including budget, first match, `retrySelf`, and manual retry, are defined
in [`recovery`](../../docs/workflow-definition.md#recovery-failure-recovery). This document defines execution.

## Design Drivers

- The engine remains generic. It understands Stage, task, check, completion, and failure. It treats `recovery`
  as an opaque task attribute.
- Workflow YAML remains read-only during execution. Remaining budget is per-attempt state in `recoveryRemaining`, not a
  modified configuration copy.
- Recovery tasks are real Workflow tasks. They appear in the graph, timeline, and state.
- A task that triggers recovery is completed because it produced later work.
- Runner owns matching and task construction. Actions do not interpret recovery.

## Model

Workflow YAML declares `budget` and `handlers`, with optional `when`, `tasks`, and `retrySelf`. The Action
returns output or an error and has no recovery awareness. The engine inserts returned `addTasks` mechanically
and passes `recoveryRemaining` as opaque per-attempt state.

Runner matches explicit `when` handlers first and then the default handler when an error exists. It maps
explicit `null` to the full `budget`, clamps numeric values to the declared range, and builds `addTasks`
from the original declaration. A handler task receives its own full `recoveryRemaining`. A `retrySelf` copy receives the
remaining budget minus one.

Runner expands only `${{ failure.* }}` references while constructing a recovery task. Other expressions remain in the
new task declaration and are evaluated at that attempt's dispatch entry point. See
[`task-dispatch.md`](task-dispatch.md).

A `retrySelf` task copies the triggering attempt's original dispatch declaration, not this Action execution's
rendered input. The copy includes `with`, task-level `expect`, artifacts, `setVars`, recovery
configuration, and task identity. Only `recoveryRemaining` changes as separate state. A later dispatch can therefore
evaluate `${{ vars.* }}` against its own Variable snapshot instead of freezing values from the triggering attempt.
Every new attempt expands its declaration against its own context snapshot.

## Semantics

### Remaining Budget (`recoveryRemaining`)

A recovery budget bounds one continuous automatic-recovery round. `recoveryRemaining` travels with the task as execution
state:

```text diagram
+------+    +------+    +------+    +--------+    +-----+
| YAML +--->| Task +--->| Work +--->| Runner +--->| Add |
+------+    +------+    +------+    +----+---+    +--+--+
                ^                        |           |
                +------------------------+-----------+
```

Runner `tryRecovery` is the sole read and write authority for `recoveryRemaining`. The engine passes the field through and
never reads its value. On the engine side, the field is side-channel state during task intake, following the
`causedByFeedbackId` precedent. It never enters `TaskDefinition`.

An explicit `null` starts a new round with the full `budget`. An absent field is malformed transport and
receives ordinary-result handling. It must not reopen the budget. A matched handler consumes one unit of
budget. An unmatched result consumes none. The declaration is never modified.

### Manual Retry Opens a New Round

A manual retry reconstructs the Task from its original declaration and definitional fields. It starts with
`recoveryRemaining = null`, so Runner opens a new round from the declared `budget`. Corrected Variables and Prompts can
therefore take effect without turning execution output into future configuration. The failed attempt and its
consumed budget remain unchanged for audit.

### A Stage Rerun Does Not Reuse TaskRun Identity

A `TaskRun` identity consists of Definition ID, Stage attempt, and task attempt. The first Stage attempt
retains `{definitionId}.{taskAttempt}`. Later attempts use `{definitionId}.s{stageAttempt}.{taskAttempt}`. For example, the first build task is `T-001.1`, a manual
retry in the same Stage is `T-001.2`, and the first task after rerunning build is `T-001.s2.1`.

Definition IDs are scoped to a Stage. If a candidate TaskRun ID already exists in the WorkflowRun, the
allocator appends the first available `.runN` suffix. This preserves established IDs without collisions and
keeps every persisted TaskRun ID and Work ID unique within the run.

`rerun-from-stage` discards visible task history from the old Stage, but it cannot decrease the Stage attempt or reuse
an old identity. A Workflow AgentSession whose default name is the Work ID is always a new logical Session. It
cannot inherit the invalidated attempt's physical binding or working directory. An explicit `session` name
retains its own reuse semantics through the Workflow Definition.

### Runtime Binding Repair Is Not Workflow Recovery

Before submitting independent input, AgentSession may find that its Runtime Session is confirmed missing and
repair the physical binding under [`agent-execution.md`](../agent-execution.md#runtime-session-missing-recovery). Repair
occurs before the Action produces a result. A successful repair continues the original TaskRun attempt without
a recovery task, budget decrement, or manual Retry. A failed repair passes a normalized error into the
recovery and manual-retry rules here.

WorkflowRun neither decides nor implements Runtime binding repair. Runner reports Runtime facts, Session
arbitrates and persists the binding, and Workflow interprets only the final Action result.

### Runner Executor Flow

The Runner executor applies these rules in order:

1. Build `{ output, error }` from the Action result.
2. Return the ordinary result when `recoveryRemaining` is absent.
3. Give explicit `null` the full declared `budget`. Clamp numeric values to the declared range.
4. Match the first explicit `when` handler. If none matches and an error exists, match the default handler
   without `when`.
5. If a handler matches and budget remains, bind `${{ failure.* }}` in its tasks, copy the original declaration for
   `retrySelf`, and return completed with `addTasks`. Handler tasks receive full budget. The retry copy receives
   the remaining budget minus one.
6. Without a matching handler, return completed when there is no error and failed otherwise.

At most one default handler exists and it is last, so it cannot shadow an explicit match. It matches after the
executor forms the final failed result, including failures such as a dirty workspace or invalid branch.
Negative `recoveryRemaining` clamps to 0. Values above the declared limit clamp to that limit.

A recovery handler may read `${{ failure.output.* }}`, `${{ failure.error.code }}`, and `${{ failure.error.message }}`. The message carries an actionable error into
a recovery task. A handler must not branch on the message.

### WorkResult

```text literal
{
  "status": "completed",
  "addTasks": [
    { "id": "recover:rebase", "uses": "mohist/rebase", "with": {...} },
    { "id": "load-tasks", "uses": "mohist/task-list", "with": {"path": "PLANS/tasks.json"}, "recovery": {"budget": 2, ...}, "recoveryRemaining": 1 }
  ]
}
```

`completed` with `addTasks` makes the engine insert tasks into the current Stage. `completed` without `addTasks` is
normal completion. `failed` fails the Workflow.

### One-Off Task Injection Uses the Profile Mechanism

An API-triggered rebase task carries a `RecoveryDefinition` and uses the same budget, handler matching, `when`,
`retrySelf`, and remaining-budget semantics as inline `task.recovery`. Only the trigger differs: the API route selects
the recovery and the Runner executor applies it. Runner still matches and decrements the budget, and `addTasks`
still follows the engine insertion path.

One-off injection does not have a second representation or different namespaces. Recovery content remains a
`RecoveryDefinition`, so application code selects a name rather than copying `uses`, Prompt references, budget, or
handler order.

### Top-Level `recoveries`: Named Recovery Templates

The top-level `recoveries` key owns named recovery templates. A one-off trigger selects a template from the
complete Workflow Definition bound to the WorkflowRun. It does not read the current Profile or build a second
definition in application code.

```yaml
recoveries:
  rebase-conflicts:
    budget: 2
    handlers:
      - when: error.code=conflict
        tasks:
          - id: recover:resolve-rebase-conflicts
            title: Resolve rebase conflicts
            uses: mohist/agent
            with:
              name: mohist/builder
              session: check
              prompt: ${{ prompts.resolve-rebase-conflicts }}
        retrySelf: false
```

The `recoveries` map is part of WorkflowDefinition and round-trips with the Profile. Workflow content remains the
single author of recovery `uses`, Prompt references, budget, and handler order. A Profile edit therefore
changes future WorkflowRuns in one place.

Template names are lowercase and hyphen-separated, such as `rebase-conflicts` and `plan-conflicts`. Each built-in YAML file
declares the templates it needs. Extract cross-Profile sharing only after a third Profile or a second shared
template appears.

## Status

Implemented: `retrySelf` and manual retry reconstruct the original declaration. `with` and `expect` retain
Workflow expressions, new attempts evaluate them against their own context snapshot, and `recoveryRemaining` remains
separate execution state.
