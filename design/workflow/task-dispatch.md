# Task Dispatch

## task.with expansion

`tasks[*].with` has `${{ }}` template expressions. WorkflowGrain expands them at dispatch.

```
${{ path }}  →  resolved variable value
non-template  →  kept as-is
```

Expanded JSON objects deep-merge with resolved vars (vars win, task-level stays).

## Dispatch context

Available in `with` expressions:

| Variable | Source |
|---|---|
| `workflow.runId` | dispatch |
| `stage.name` | dispatch |
| `work.id` | dispatch |
| `issue.number` | dispatch |
| `repository.*` | dispatch |
| `workspace.*` | dispatch |
| `vars.*` | WorkflowStageEffectiveVariables |
| `tasks.<id>.outputs.*` | previous task output |
| `prompts.<key>` | Project Space prompt (runner resolves at execution time) |

Dispatch context is not profile variables. It exists only in the dispatch payload.

Full dispatch/report flow: see `scheduling.md`.
