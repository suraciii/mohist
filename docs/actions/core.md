# Core Actions

## `core/process`

Runs a process and captures its standard output and exit code.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `command` | Yes | - | Command to invoke. The value is text. |
| `args` | No | `[]` | Arguments to pass to the command. The value is an array. |

### Outputs

| Field | Meaning |
|---|---|
| `stdout` | Command standard output with leading and trailing whitespace removed. |
| `exitCode` | Process exit code. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `process-failed` | The process exited with a nonzero status. |

### Example

```yaml
- id: check-version
  uses: core/process
  with:
    command: node
    args: [--version]
```

## `core/script`

Runs an inline script through the current platform's shell wrapper.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `run` | Yes | - | Script content to run. The value is text. |
| `shell` | No | - | Shell executable. The value is text. |
| `timeout` | No | - | Script execution deadline in milliseconds. The value is numeric. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `run` | Original script content. |
| `shell` | Shell executable that was used. |
| `exitCode` | Shell exit code. |
| `stdout` | Truncated standard output. |
| `stderr` | Truncated standard error. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `script-failed` | The script exited with a nonzero status. |

A script-failure diagnostic includes the shell exit code and bounded tails of
nonempty standard output and standard error. Recovery therefore receives an
actionable failure reason even when a tool writes warnings to standard error
and the actual failure to standard output.

### Example

```yaml
- id: verify-diff
  uses: core/script
  with:
    run: git diff --check
```

## `core/artifact-exists`

Checks whether a file or directory exists at a relative workspace path.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `path` | Yes | - | Path to check. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `path` | Resolved path. |
| `exists` | Whether the path exists. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `artifact-missing` | The required file or directory does not exist. |

### Example

```yaml
- id: check-proposal
  uses: core/artifact-exists
  with:
    path: openspec/changes/issue-448/proposal.md
```

## `core/marker`

Checks whether a workspace file contains specified marker text.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `path` | Yes | - | Path to read. The value is text. |
| `expect` | No | - | Marker text to match. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `path` | Resolved path. |
| `marker` | Marker text matched by this check. |
| `found` | Whether the marker was found. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `artifact-missing` | The marker file does not exist. |
| `marker-missing` | The marker text was not found in the file. |

### Example

```yaml
- id: verify-completion
  uses: core/marker
  with:
    path: openspec/changes/issue-448/progress.txt
    expect: "## Codebase Patterns"
```
