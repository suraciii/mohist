# Task-list Action

## `mohist/task-list`

Loads a task list file and adds its tasks to the current Workflow execution.

The task list is the one machine-readable plan artifact the Workflow consumes.
The planning Agent may organize all other planning and design material freely;
only this file is read by the engine.

### Schema

```json
{
  "tasks": [
    {
      "id": "T-001",
      "title": "Extract the notification-channel abstraction",
      "goal": "What to implement and why, in a few sentences.",
      "acceptance": ["verifiable criterion"],
      "refs": ["PLANS/DESIGN.md#abstraction"]
    }
  ]
}
```

- `id`, `title`, and `goal` are required non-empty JSON strings. `id` and
  `title` are structural; `id` identifies the generated task.
- Array order is the execution order. There is no dependency graph, priority,
  or per-task execution configuration; the Profile fixes the execution Action
  for every generated task.
- `goal`, `acceptance`, and `refs` are rendered into the generated task's
  prompt as text. The Workflow never verifies acceptance criteria
  mechanically; verification lives in explicit verify Tasks, in Check Stage
  evidence, and in the approver's judgment.
- Entries must not declare `uses` or `expect`; the Action rejects them.

### Inputs

- `path` is the required Workspace-relative path to the task list file.
  Absolute paths and traversal outside the Workspace are rejected.
- `task` is required and supplies defaults for every generated task.
  `task.uses` is a required literal Action name; a generated task cannot
  choose its own execution.
- `buildPrompt` is internal prompt text sourced from the current Project's
  `build-task` Prompt. It is not exposed in the public Action catalog.

The file is Workspace-local. When the Workspace directory was rebuilt the
file is gone with it, and the recovery is the existing
`mo run rerun --from-stage plan`, which regenerates it. There is no artifact
restore channel.

### Outputs

The output field `loaded` is the number of tasks added to this run.

### Business Error Codes

- `missing-source` means the task list file does not exist. Recover by
  rerunning from the plan Stage.
- `invalid-input` means the task list file failed schema validation.

### Example

```yaml
- id: load-tasks
  uses: mohist/task-list
  with:
    path: PLANS/tasks.json
    task:
      uses: mohist/agent
      with:
        name: mohist/builder
        session: build
```

Every generated task inherits the materialized `task.uses` and receives a
validated snapshot of its entry's `goal`, `acceptance`, and `refs` in its
prompt. Later mutation or deletion of `PLANS/tasks.json` cannot change tasks
already added to the Workflow run.
