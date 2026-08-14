# OpenSpec Actions

## `mohist/openspec-tasks`

Loads `tasks.json` and adds its tasks to the current Workflow execution.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `path` | Yes | - | Path to `tasks.json`. The value is text. |
| `task` | Yes | - | Default task fields applied to every task entry. `task.uses` is required and is resolved by the Profile before the Action runs. |
| `items` | No | `tasks` | Top-level path to the task list in the JSON document. The value is text. |
| `buildPrompt` | No | - | Text used to build each task prompt. |

### Outputs

| Field | Meaning |
|---|---|
| `loaded` | Number of tasks added to this run. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `missing-source` | The `tasks.json` file does not exist. |
| `server-unavailable` | The Server cannot be reached. |

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

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `changeDir` | Yes | - | Path to the OpenSpec change directory. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `changeDir` | Resolved change directory. |
| `present` | Whether all required artifacts exist. |
| `missing` | Paths of missing artifacts. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `artifacts-missing` | A required OpenSpec artifact does not exist. |

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

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `changeDir` | Yes | - | Path to the OpenSpec change directory. The value is text. |
| `archiveHint` | No | `vars.archive` when present | Runner-owned archive destination from a prior successful attempt. It is injected from the dispatch snapshot, not declared in profile `with`. |

When no `archive` Run Variable exists, Runner omits `archiveHint` and the Action selects a fresh
dated destination. A successful Action writes the chosen workspace-relative destination to the Run
Variables. A later retry or rerun then receives that destination as `archiveHint` and succeeds
idempotently when the source is already archived.

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `source` | Source change directory. |
| `destination` | Archive destination directory. |
| `changed` | Whether the archive step modified the repository. |
| `noChange` | Whether the archive step produced no change. |
| `commitMessage` | Commit message used when the archive step modified the repository. |
| `commitSha` | Commit SHA produced when the archive step modified the repository. |
| `commitOutput` | Raw Git commit output. |
| `changedFiles` | Files modified by the archive commit. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `retry-safe` | The archive step can be retried safely. |
| `partial-archive` | Both source and archive directories contain files, so overwrite is refused. |
| `missing-source` | The source change directory does not exist. |
| `config-error` | The archive configuration is invalid. |

### Example

```yaml
- id: archive-change
  uses: mohist/archive-change
  with:
    changeDir: openspec/changes/issue-448
```
