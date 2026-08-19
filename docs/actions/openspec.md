# OpenSpec Actions

## `mohist/openspec-tasks`

Loads `tasks.json` and adds its tasks to the current Workflow execution.

### Inputs

- `path` is the required text path to `tasks.json`.
- `task` is required and supplies defaults for every generated task.
  `task.uses` is required and is resolved by the Profile before the Action runs.
- `items` is the optional top-level path to the task list. It defaults to
  `tasks`.
- `buildPrompt` is optional text used to build each task prompt.

### Outputs

The output field `loaded` is the number of tasks added to this run.

### Business Error Codes

- `missing-source` means the `tasks.json` file does not exist.
- `server-unavailable` means the Server cannot be reached.

### Example

```yaml
- id: load-tasks
  uses: mohist/openspec-tasks
  with:
    path: openspec/changes/issue-448/tasks.json
    task:
      uses: ${{ profile.agentAction }}
      with:
        options: ${{ vars.agent }}
    items: tasks
```

Every generated task inherits the materialized `task.uses`. Entries in
`tasks.json` must not contain `uses`; the Action rejects an override with
`invalid-input` instead of changing the Profile-selected Action. There is no
implicit `mohist/opencode` fallback.

## `mohist/openspec-artifacts`

Checks whether all required artifacts exist in an OpenSpec change directory.

### Inputs

`changeDir` is the required text path to the OpenSpec change directory.

### Outputs

The output contains the type identifier in `kind`, the resolved directory in
`changeDir`, the result in `present`, and any absent artifact paths in
`missing`.

### Business Error Codes

`artifacts-missing` means a required OpenSpec artifact does not exist.

### Example

```yaml
- id: verify-change
  uses: mohist/openspec-artifacts
  with:
    changeDir: openspec/changes/issue-448
```

## `mohist/archive-change`

Archives an OpenSpec change directory and commits the resulting move.

### Inputs

- `changeDir` is the required text path to the OpenSpec change directory.
- `archiveHint` is an optional Runner-owned retry destination. Workflow authors
  do not declare it in `with`. See
  [Task Dispatch](../../design/workflow/task-dispatch.md#engine-sourced-action-inputs)
  for its snapshot and retry semantics.

### Outputs

The output contains the type identifier, source, destination, and whether the
repository changed. When it creates a commit, it also reports the commit
message, SHA, output, and changed files. `noChange` reports an idempotent run
that needed no repository change.

### Business Error Codes

- `retry-safe` means the archive step can be retried safely.
- `partial-archive` means both source and archive directories contain files,
  so overwrite is refused.
- `missing-source` means the source change directory does not exist.
- `config-error` means the archive configuration is invalid.

### Example

```yaml
- id: archive-change
  uses: mohist/archive-change
  with:
    changeDir: openspec/changes/issue-448
```
