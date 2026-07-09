# Task Recovery

After task execution, runner executor matches `when` expressions against action output fields, constructs recovery tasks, returns them via `addTasks`. Engine inserts mechanically.

Recovery is part of task completion, not post-failure remediation. Engine never understands recovery semantics.

Matching is independent of task success/failure. A successful task whose output matches `when: promise=FAIL` still triggers recovery.

## Design

- Workflow engine: generic. Only knows stage/task/check/completed/failed. Never knows "recovery."
- Recovery is a task top-level property: `recovery.budget` + `recovery.handlers`. Engine passes through.
- Matching in runner executor: `when` expression matches any field in action output. Action knows nothing about recovery.
- Recovery tasks are real workflow tasks: visible in graph/timeline/status.
- Recovery = completed: current task produced recovery tasks as follow-up work.

## Structure

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    prNumber: ${{ vars.github.pr.number }}
  recovery:
    budget: 2
    handlers:
      - when: errorCode=base-moved
        tasks:
          - id: recover:rebase
            uses: mohist/rebase
            with: { baseBranch: ${{ repository.baseBranch }} }
          - id: recover:push
            uses: mohist/push
            with: { source: ${{ workspace.branch }}, target: ${{ workspace.branch }} }
        retrySelf: true
```

- `budget`: max auto-recovery attempts. Default 0.
- `handlers`: ordered, first-match.
- `when`: `field=value` match on any action output field.
- `tasks`: user-defined recovery tasks.
- `retrySelf`: runner constructs self-retry task with `budget - 1`, appends to tasks.

## Division of labor

| Layer | Does |
|---|---|
| workflow YAML | declares budget, handlers (when, tasks, retrySelf) |
| action | returns normal output. Zero recovery awareness |
| runner executor | matches `when`, checks budget, constructs `addTasks` |
| engine | mechanically inserts `addTasks` |

## Runner executor flow

```
result = action.execute()
output = parseJSON(result.output)
handler = recovery.handlers.find(h => matchesWhen(h.when, output))

if handler && recovery.budget > 0:
    addTasks = handler.tasks + (retrySelf ? retryTask(budget-1) : [])
    return completed + addTasks

if result.success:
    return completed
return failed
```

## WorkResult

```json
{
  "status": "completed",
  "addTasks": [
    { "id": "recover:rebase", "uses": "mohist/rebase", "with": {...} },
    { "id": "merge-pr", "uses": "mohist/merge-github-pr", "with": {...}, "recovery": {"budget": 1, ...} }
  ]
}
```

- `completed` + `addTasks`: engine inserts tasks into current stage.
- `completed` (no `addTasks`): normal completion.
- `failed`: workflow failed.

## Server behavior

```
result.completed
  → mark task completed
  → addTasks non-empty? → AddRuntimeTasks
  → Advance

result.failed
  → mark task failed → stage failed → workflow failed
```

## What is removed

Replaces `onFailure`: server-side matching, engine-managed budget (`RemainingRecoveries`), `TaskFailureAction`, `TaskFailureCase`, failed-task state for recovered tasks — all gone. Recovery logic and budget live entirely in the runner.
