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

- `id` (required) and `title` are structural. `id` identifies the generated
  task.
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
- `task` is required and supplies defaults for every generated task.
  `task.uses` is required and is resolved by the Profile before the Action
  runs.
- `items` is the optional top-level path to the task list. It defaults to
  `tasks`.
- `buildPrompt` is optional text used to build each task prompt.

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
      uses: ${{ profile.agentAction }}
      with:
        options: ${{ vars.agent }}
        prompt: ${{ prompts.build-task }}
```

Every generated task inherits the materialized `task.uses` and receives its
entry's `goal`, `acceptance`, and `refs` in its prompt.
