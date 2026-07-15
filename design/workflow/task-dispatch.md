# Task Dispatch

## Task configuration expansion

`tasks[*].with` and task-level `expect` may contain `${{ }}` template expressions.
WorkflowGrain expands them at dispatch.

```
${{ path }}  →  resolved variable value
non-template  →  kept as-is
```

Expansion semantics (deep-merge with resolved vars, whole-value expressions keeping the
resolved JSON type) are specified with examples in [`profile.md`](profile.md). The rendered
`with` payload is the action's only variable/configuration input; actions do not read the
Workflow variable store again.

`expect` is expanded and dispatched separately as Workflow's task completion contract. It is
not inserted into `with` and is not part of a runtime-specific Action Input.

## Dispatch context

Available in `with` and `expect` expressions:

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

Full dispatch/report flow: see [`../runner.md`](../runner.md).
